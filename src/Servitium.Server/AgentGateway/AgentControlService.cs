using System;
using System.Net;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;

using Servitium.Application.Abstractions;
using Servitium.Application.Identity;
using Servitium.Application.Nodes;
using Servitium.Contracts.Agent.V1;
using Servitium.Contracts.Events;
using Servitium.Contracts.Nodes;
using Servitium.Server.Realtime;

namespace Servitium.Server.AgentGateway;

[System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1812:Avoid uninstantiated internal classes", Justification = "ASP.NET Core gRPC activates the service through endpoint routing and dependency injection.")]
internal sealed class AgentControlService(
    INodeControlStore store,
    ITokenGenerator tokenGenerator,
    IGuidFactory guidFactory,
    IClock clock,
    AgentConnectionManager connections,
    IRealtimeEventStream realtimeEvents,
    NodeReconciler reconciler,
    InstanceTelemetryStore instanceTelemetry) : AgentControl.AgentControlBase
{
    public override async Task Connect(
        IAsyncStreamReader<AgentMessage> requestStream,
        IServerStreamWriter<ServerMessage> responseStream,
        ServerCallContext context)
    {
        IPAddress? remoteAddress = context.GetHttpContext().Connection.RemoteIpAddress;
        bool isLocal = remoteAddress is null || IPAddress.IsLoopback(remoteAddress);
        if (!context.GetHttpContext().Request.IsHttps && !isLocal)
        {
            throw new RpcException(new Status(StatusCode.PermissionDenied, "tls-required"));
        }

        if (!await requestStream.MoveNext(context.CancellationToken).ConfigureAwait(false) || requestStream.Current.BodyCase != AgentMessage.BodyOneofCase.Hello)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "validation-failed"));
        }

        Hello hello = requestStream.Current.Hello;
        if (string.IsNullOrWhiteSpace(hello.CredentialOrRegistrationToken) || hello.CredentialOrRegistrationToken.Length > 512 ||
            string.IsNullOrWhiteSpace(hello.MachineFingerprint) || hello.MachineFingerprint.Length > 256 ||
            string.IsNullOrWhiteSpace(hello.AgentVersion) || hello.AgentVersion.Length > 64 ||
            hello.OperatingSystem.Length > 256 || hello.Architecture.Length > 32 || hello.NodeName.Length > 128)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "validation-failed"));
        }

        string secretHash = IdentityApplicationService.HashToken(hello.CredentialOrRegistrationToken);
        StoredNode? node = await store.AuthenticateCredentialAsync(secretHash, context.CancellationToken).ConfigureAwait(false);
        if (hello.ProtocolMajor != 1)
        {
            if (node is not null)
            {
                await store.UpdateHeartbeatAsync(node.Id, "Incompatible", hello.AgentVersion, clock.UtcNow, context.CancellationToken).ConfigureAwait(false);
                _ = await realtimeEvents.PublishAsync(
                    EventTypes.NodeStatusChanged,
                    "node:" + node.Id.ToString("D"),
                    new NodeStatusChangedPayload(node.Id, "Incompatible", "protocol-major-mismatch"),
                    context.CancellationToken).ConfigureAwait(false);
            }

            throw new RpcException(new Status(StatusCode.FailedPrecondition, "node-incompatible"));
        }

        string? newCredential = null;
        if (node is null)
        {
            DateTimeOffset now = clock.UtcNow;
            Guid nodeId = guidFactory.Create();
            newCredential = tokenGenerator.Generate(32);
            node = new StoredNode(
                nodeId,
                string.IsNullOrWhiteSpace(hello.NodeName) ? hello.MachineFingerprint : hello.NodeName.Trim(),
                "Online",
                hello.AgentVersion,
                hello.ProtocolMajor,
                hello.ProtocolMinor,
                hello.OperatingSystem,
                hello.Architecture,
                hello.MachineFingerprint,
                now,
                now,
                false);
            StoredNodeCredential credential = new(
                guidFactory.Create(),
                nodeId,
                IdentityApplicationService.HashToken(newCredential),
                now,
                null,
                1);
            node = await store.TryRegisterAsync(secretHash, node, credential, now, context.CancellationToken).ConfigureAwait(false) ?? throw new RpcException(new Status(StatusCode.PermissionDenied, "registration-token-invalid"));
        }

        ByteString configuration = newCredential is null && node.Configuration is null
            ? ByteString.Empty
            : ByteString.CopyFromUtf8(JsonSerializer.Serialize(new AgentWelcomeConfiguration(newCredential, node.Configuration)));
        AgentConnection connection = connections.Register(node.Id, responseStream, context.CancellationToken);
        await connection.SendAsync(new ServerMessage
        {
            Welcome = new Welcome
            {
                NodeId = node.Id.ToString("D"),
                ProtocolMajor = 1,
                ProtocolMinor = 0,
                ServerTime = Timestamp.FromDateTimeOffset(clock.UtcNow),
                NodeConfiguration = configuration,
            },
        }, context.CancellationToken).ConfigureAwait(false);
        await store.UpdateHeartbeatAsync(node.Id, "Online", hello.AgentVersion, clock.UtcNow, context.CancellationToken).ConfigureAwait(false);
        _ = await realtimeEvents.PublishAsync(
            EventTypes.NodeStatusChanged,
            "node:" + node.Id.ToString("D"),
            new NodeStatusChangedPayload(node.Id, "Online", null),
            context.CancellationToken).ConfigureAwait(false);
        string reconcileCommandId = guidFactory.Create().ToString("N");
        await connection.SendAsync(new ServerMessage
        {
            CommandRequest = new CommandRequest
            {
                CommandId = reconcileCommandId,
                Type = CommandType.Reconcile,
                TimeoutSeconds = 30,
            },
        }, context.CancellationToken).ConfigureAwait(false);
        DateTimeOffset reconcileDeadline = clock.UtcNow.AddSeconds(30);
        bool reconciled = false;
        System.Collections.Generic.List<AgentEvent> deferredEvents = [];

        try
        {
            while (await requestStream.MoveNext(context.CancellationToken).WaitAsync(TimeSpan.FromSeconds(35), context.CancellationToken).ConfigureAwait(false))
            {
                AgentMessage message = requestStream.Current;
                if (message.BodyCase == AgentMessage.BodyOneofCase.CommandResponse)
                {
                    if (string.Equals(message.CommandResponse.CommandId, reconcileCommandId, StringComparison.Ordinal))
                    {
                        if (!message.CommandResponse.Succeeded)
                        {
                            throw new RpcException(new Status(StatusCode.FailedPrecondition, message.CommandResponse.Error?.Code ?? "agent-reconcile-failed"));
                        }

                        _ = await reconciler.ApplyAsync(node.Id, message.CommandResponse.Payload.Memory, context.CancellationToken).ConfigureAwait(false);
                        reconciled = true;
                        foreach (AgentEvent deferredEvent in deferredEvents)
                        {
                            await HandleEventAndAcknowledgeAsync(node.Id, deferredEvent, connection, context.CancellationToken).ConfigureAwait(false);
                        }

                        deferredEvents.Clear();
                    }
                    else
                    {
                        connections.Complete(message.CommandResponse, node.Id);
                    }
                }
                else if (message.BodyCase == AgentMessage.BodyOneofCase.Heartbeat)
                {
                    await connection.SendAsync(new ServerMessage
                    {
                        HeartbeatAck = new HeartbeatAck
                        {
                            Sequence = message.Heartbeat.Sequence,
                            ReceivedAt = Timestamp.FromDateTimeOffset(clock.UtcNow),
                        },
                    }, context.CancellationToken).ConfigureAwait(false);
                    await store.UpdateHeartbeatAsync(node.Id, "Online", hello.AgentVersion, clock.UtcNow, context.CancellationToken).ConfigureAwait(false);
                }
                else if (message.BodyCase == AgentMessage.BodyOneofCase.Event)
                {
                    if (!reconciled)
                    {
                        if (deferredEvents.Count >= 100 || message.Event.Payload.Length > 1024 * 1024)
                        {
                            throw new RpcException(new Status(StatusCode.ResourceExhausted, "agent-event-buffer-full"));
                        }

                        deferredEvents.Add(message.Event.Clone());
                    }
                    else
                    {
                        await HandleEventAndAcknowledgeAsync(node.Id, message.Event, connection, context.CancellationToken).ConfigureAwait(false);
                    }
                }
                else if (message.BodyCase == AgentMessage.BodyOneofCase.StreamChunk)
                {
                    await connections.ReceiveStreamChunkAsync(node.Id, message.StreamChunk, context.CancellationToken).ConfigureAwait(false);
                }

                if (!reconciled && clock.UtcNow > reconcileDeadline)
                {
                    throw new RpcException(new Status(StatusCode.DeadlineExceeded, "agent-reconcile-timeout"));
                }
            }
        }
        finally
        {
            connections.Remove(node.Id, connection);
            await store.UpdateHeartbeatAsync(node.Id, "Offline", hello.AgentVersion, clock.UtcNow, default).ConfigureAwait(false);
            _ = await realtimeEvents.PublishAsync(
                EventTypes.NodeStatusChanged,
                "node:" + node.Id.ToString("D"),
                new NodeStatusChangedPayload(node.Id, "Offline", "connection-closed"),
                default).ConfigureAwait(false);
        }
    }

    private async Task HandleEventAndAcknowledgeAsync(
        Guid nodeId,
        AgentEvent agentEvent,
        AgentConnection connection,
        CancellationToken cancellationToken)
    {
        await HandleEventAsync(nodeId, agentEvent, cancellationToken).ConfigureAwait(false);
        if (agentEvent.Sequence > 0)
        {
            await connection.SendAsync(new ServerMessage
            {
                EventAck = new EventAck { Sequence = agentEvent.Sequence },
            }, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task HandleEventAsync(Guid nodeId, AgentEvent agentEvent, CancellationToken cancellationToken)
    {
        string expectedScope = "node:" + nodeId.ToString("D");
        if (string.Equals(agentEvent.Type, EventTypes.NodeMetrics, StringComparison.Ordinal))
        {
            if (!string.Equals(agentEvent.Scope, expectedScope, StringComparison.Ordinal))
            {
                throw new RpcException(new Status(StatusCode.PermissionDenied, "agent-event-invalid"));
            }

            await HandleNodeMetricAsync(nodeId, expectedScope, agentEvent, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (agentEvent.Payload.Length is < 2 or > 1024 * 1024 ||
            !AgentProducedEventTypes.Contains(agentEvent.Type))
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "agent-event-invalid"));
        }

        System.Collections.Generic.IReadOnlyList<StoredNodeMirror> mirrors = await store.ListMirrorsAsync(nodeId, cancellationToken).ConfigureAwait(false);
        if (mirrors.Count != 1)
        {
            throw new RpcException(new Status(StatusCode.FailedPrecondition, "agent-reconcile-required"));
        }

        ValidateOwnedScope(mirrors[0], agentEvent.Scope);
        JsonElement payload;
        try
        {
            payload = JsonSerializer.Deserialize<JsonElement>(agentEvent.Payload.Span);
        }
        catch (JsonException)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "agent-event-invalid"));
        }

        if (agentEvent.Type == EventTypes.InstanceStatusChanged)
        {
            InstanceStatusChangedPayload status;
            try
            {
                status = payload.Deserialize<InstanceStatusChangedPayload>()
                    ?? throw new JsonException("The instance status payload is empty.");
            }
            catch (JsonException)
            {
                throw new RpcException(new Status(StatusCode.InvalidArgument, "agent-event-invalid"));
            }

            string instanceScope = "instance:" + status.InstanceId.ToString("D");
            if (!string.Equals(instanceScope, agentEvent.Scope, StringComparison.Ordinal) ||
                status.Status is not ("Starting" or "Running" or "Stopping" or "Stopped" or "Crashed") ||
                status.PreviousStatus is not ("Starting" or "Running" or "Stopping" or "Stopped" or "Crashed"))
            {
                throw new RpcException(new Status(StatusCode.InvalidArgument, "agent-event-invalid"));
            }

            await store.UpdateInstanceMirrorStatusAsync(nodeId, status.InstanceId, status.Status, clock.UtcNow, cancellationToken).ConfigureAwait(false);
        }
        else if (agentEvent.Type == EventTypes.InstanceResourceUsage)
        {
            InstanceResourceUsagePayload metrics = DeserializeOwnedPayload<InstanceResourceUsagePayload>(payload, agentEvent.Scope);
            if (!double.IsFinite(metrics.CpuPercent) || metrics.CpuPercent is < 0 or > 100 || metrics.MemoryBytes < 0 || metrics.OnlinePlayers < 0)
            {
                throw new RpcException(new Status(StatusCode.InvalidArgument, "agent-event-invalid"));
            }

            instanceTelemetry.Update(metrics, clock.UtcNow);
        }
        else if (agentEvent.Type == EventTypes.InstancePlayersChanged)
        {
            InstancePlayersChangedPayload players = DeserializeOwnedPayload<InstancePlayersChangedPayload>(payload, agentEvent.Scope);
            if (players.OnlinePlayers < 0 || players.MaximumPlayers < players.OnlinePlayers)
            {
                throw new RpcException(new Status(StatusCode.InvalidArgument, "agent-event-invalid"));
            }

            instanceTelemetry.Update(players, clock.UtcNow);
        }

        _ = await realtimeEvents.PublishAsync(agentEvent.Type, agentEvent.Scope, payload, cancellationToken).ConfigureAwait(false);
    }

    private static T DeserializeOwnedPayload<T>(JsonElement payload, string scope)
        where T : class
    {
        try
        {
            T value = payload.Deserialize<T>() ?? throw new JsonException("The Agent event payload is empty.");
            Guid instanceId = value switch
            {
                InstanceResourceUsagePayload resource => resource.InstanceId,
                InstancePlayersChangedPayload players => players.InstanceId,
                _ => Guid.Empty,
            };
            return instanceId != Guid.Empty && string.Equals(scope, "instance:" + instanceId.ToString("D"), StringComparison.Ordinal)
                ? value
                : throw new JsonException("The Agent event scope does not match its payload.");
        }
        catch (JsonException)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "agent-event-invalid"));
        }
    }

    private async Task HandleNodeMetricAsync(Guid nodeId, string expectedScope, AgentEvent agentEvent, CancellationToken cancellationToken)
    {
        NodeMetricsDto sample;
        try
        {
            sample = JsonSerializer.Deserialize<NodeMetricsDto>(agentEvent.Payload.Span)
                ?? throw new JsonException("The node metrics payload is empty.");
        }
        catch (JsonException)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "agent-event-invalid"));
        }

        DateTimeOffset now = clock.UtcNow;
        if (!double.IsFinite(sample.CpuPercent) || sample.CpuPercent is < 0 or > 100 ||
            sample.MemoryUsedBytes < 0 || sample.MemoryTotalBytes < sample.MemoryUsedBytes || sample.DiskFreeBytes < 0 ||
            sample.NetworkReceiveBytesPerSecond < 0 || sample.NetworkSendBytesPerSecond < 0 || sample.Uptime < TimeSpan.Zero ||
            sample.SampledAt < now.AddMinutes(-2) || sample.SampledAt > now.AddMinutes(1))
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "agent-event-invalid"));
        }

        await store.AddMetricAsync(new StoredNodeMetric(nodeId, sample), now.AddHours(-24), cancellationToken).ConfigureAwait(false);
        _ = await realtimeEvents.PublishAsync(
            EventTypes.NodeMetrics,
            expectedScope,
            new NodeMetricsPayload(
                nodeId,
                sample.CpuPercent,
                sample.MemoryUsedBytes,
                sample.DiskFreeBytes,
                sample.SampledAt,
                sample.MemoryTotalBytes,
                sample.NetworkReceiveBytesPerSecond,
                sample.NetworkSendBytesPerSecond,
                sample.Uptime),
            cancellationToken).ConfigureAwait(false);
    }

    private static void ValidateOwnedScope(StoredNodeMirror mirror, string scope)
    {
        const string instancePrefix = "instance:";
        const string schedulePrefix = "schedule:";
        if (scope.StartsWith(instancePrefix, StringComparison.Ordinal) &&
            Guid.TryParse(scope[instancePrefix.Length..], out Guid instanceId))
        {
            foreach (Contracts.Agent.AgentInstanceResponse item in mirror.Instances)
            {
                if (item.Id == instanceId)
                {
                    return;
                }
            }
        }

        if (scope.StartsWith(schedulePrefix, StringComparison.Ordinal) &&
            Guid.TryParse(scope[schedulePrefix.Length..], out Guid scheduleId))
        {
            foreach (Contracts.Scheduling.ScheduledTaskDetailsDto item in mirror.Schedules)
            {
                if (item.Id == scheduleId)
                {
                    return;
                }
            }
        }

        throw new RpcException(new Status(StatusCode.PermissionDenied, "agent-event-invalid"));
    }

    private static System.Collections.Generic.HashSet<string> AgentProducedEventTypes { get; } =
        new System.Collections.Generic.HashSet<string>(
            [
                EventTypes.InstanceStatusChanged,
                EventTypes.InstanceResourceUsage,
                EventTypes.InstanceCrashed,
                EventTypes.InstanceAutoRestartExhausted,
                EventTypes.InstancePlayersChanged,
                EventTypes.FileTaskProgress,
                EventTypes.ScheduleRunCompleted,
            ],
            StringComparer.Ordinal);

    private sealed record AgentWelcomeConfiguration(string? Credential, UpdateNodeRequest? Configuration);
}
