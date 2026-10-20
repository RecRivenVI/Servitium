using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Grpc.Net.Client;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using Servitium.Agent.ControlPlane;
using Servitium.Agent.Security;
using Servitium.Agent.Metrics;
using Servitium.Agent.Terminals;
using Servitium.Contracts.Events;
using Servitium.Contracts.Nodes;
using Servitium.Application.Abstractions;
using Servitium.Contracts.Agent.V1;

namespace Servitium.Agent.Hosting;

internal sealed partial class AgentWorker(
    AgentHostOptions options,
    AgentCredentialStore credentialStore,
    IClock clock,
    NodeMetricSampler nodeMetrics,
    AgentNodeConfigurationStore nodeConfiguration,
    AgentCommandDispatcher commandDispatcher,
    InboundFileTransferStore transfers,
    AgentOutboxStore outbox,
    OutboundFileTransferManager outboundFiles,
    AgentTerminalStreamManager terminals,
    ILogger<AgentWorker> logger) : BackgroundService
{
    private static readonly TimeSpan[] RetryDelays =
    [
        TimeSpan.FromSeconds(1),
        TimeSpan.FromSeconds(2),
        TimeSpan.FromSeconds(4),
        TimeSpan.FromSeconds(8),
        TimeSpan.FromSeconds(16),
        TimeSpan.FromSeconds(30),
    ];

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        int retry = 0;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunConnectionAsync(stoppingToken).ConfigureAwait(false);
                retry = 0;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (RpcException exception)
            {
                ConnectionFailed(logger, exception.Status.ToString());
            }
            catch (IOException)
            {
                ConnectionFailed(logger, "io-error");
            }
            catch (InvalidOperationException exception)
            {
                ConnectionFailed(logger, exception.Message);
            }

            TimeSpan delay = RetryDelays[Math.Min(retry++, RetryDelays.Length - 1)];
            await Task.Delay(delay, stoppingToken).ConfigureAwait(false);
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken).ConfigureAwait(false);
        await terminals.DisposeAsync().ConfigureAwait(false);
        await transfers.DisposeAsync().ConfigureAwait(false);
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Reliability", "CA2025:Do not pass IDisposable instances into unawaited tasks", Justification = "Both reader and heartbeat tasks are cancelled and awaited with Task.WhenAll before the gRPC call and writer gate leave scope.")]
    private async Task RunConnectionAsync(CancellationToken cancellationToken)
    {
        Uri server = new(options.Server, UriKind.Absolute);
        bool loopback = string.Equals(server.Host, "localhost", StringComparison.OrdinalIgnoreCase) ||
                        (System.Net.IPAddress.TryParse(server.Host, out System.Net.IPAddress? address) && System.Net.IPAddress.IsLoopback(address));
        if (server.Scheme != Uri.UriSchemeHttps && !loopback)
        {
            throw new InvalidOperationException("A non-loopback Agent connection requires HTTPS.");
        }

        string credential = await credentialStore.LoadAsync(cancellationToken).ConfigureAwait(false) ?? options.RegistrationToken
            ?? throw new InvalidOperationException("The Agent needs a registration token for its first connection.");
        using HttpClientHandler handler = CreatePinnedCertificateHandler(options.ServerCertificateSha256);
        using var channel = GrpcChannel.ForAddress(server, new GrpcChannelOptions { HttpHandler = handler });
        AgentControl.AgentControlClient client = new(channel);
        string csrfToken = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        Metadata headers =
        [
            new Metadata.Entry("x-csrf-token", csrfToken),
            new Metadata.Entry("cookie", "servitium.csrf=" + csrfToken),
        ];
        using AsyncDuplexStreamingCall<AgentMessage, ServerMessage> call = client.Connect(headers, cancellationToken: cancellationToken);
        using SemaphoreSlim writerGate = new(1, 1);
        await WriteAsync(call.RequestStream, writerGate, new AgentMessage
        {
            Hello = new Hello
            {
                CredentialOrRegistrationToken = credential,
                AgentVersion = typeof(AgentWorker).Assembly.GetName().Version?.ToString() ?? "0.0.0",
                ProtocolMajor = 1,
                ProtocolMinor = 0,
                OperatingSystem = RuntimeInformation.OSDescription,
                Architecture = RuntimeInformation.OSArchitecture.ToString(),
                MachineFingerprint = MachineFingerprint(),
                NodeName = options.NodeName,
            },
        }, cancellationToken).ConfigureAwait(false);

        if (!await call.ResponseStream.MoveNext(cancellationToken).ConfigureAwait(false) || call.ResponseStream.Current.BodyCase != ServerMessage.BodyOneofCase.Welcome)
        {
            throw new RpcException(new Status(StatusCode.Unauthenticated, "registration-token-invalid"));
        }

        Welcome welcome = call.ResponseStream.Current.Welcome;
        if (welcome.ProtocolMajor != 1)
        {
            throw new RpcException(new Status(StatusCode.FailedPrecondition, "node-incompatible"));
        }

        if (!welcome.NodeConfiguration.IsEmpty)
        {
            AgentWelcomeConfiguration? configuration = JsonSerializer.Deserialize<AgentWelcomeConfiguration>(welcome.NodeConfiguration.Span);
            if (!string.IsNullOrWhiteSpace(configuration?.Credential))
            {
                await credentialStore.SaveAsync(configuration.Credential, cancellationToken).ConfigureAwait(false);
            }

            if (configuration?.Configuration is not null)
            {
                _ = await nodeConfiguration.UpdateAsync(configuration.Configuration, cancellationToken).ConfigureAwait(false);
            }
        }

        using var connection = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        HeartbeatMonitor heartbeatMonitor = new(clock.UtcNow);
        try
        {
            Task reader = ReadLoopAsync(call, writerGate, heartbeatMonitor, connection.Token);
            Task heartbeat = HeartbeatLoopAsync(call.RequestStream, writerGate, heartbeatMonitor, connection.Token);
            Task metrics = MetricLoopAsync(call.RequestStream, writerGate, welcome.NodeId, connection.Token);
            Task terminalOutput = TerminalOutputLoopAsync(call.RequestStream, writerGate, connection.Token);
            Task fileOutput = FileOutputLoopAsync(call.RequestStream, writerGate, connection.Token);
            Task outboxOutput = OutboxLoopAsync(call.RequestStream, writerGate, connection.Token);
            _ = await Task.WhenAny(reader, heartbeat, metrics, terminalOutput, fileOutput, outboxOutput).ConfigureAwait(false);
            await connection.CancelAsync().ConfigureAwait(false);
            await Task.WhenAll(IgnoreCancellation(reader), IgnoreCancellation(heartbeat), IgnoreCancellation(metrics), IgnoreCancellation(terminalOutput), IgnoreCancellation(fileOutput), IgnoreCancellation(outboxOutput)).ConfigureAwait(false);
        }
        finally
        {
            await terminals.ResetAsync().ConfigureAwait(false);
            await outboundFiles.ResetAsync().ConfigureAwait(false);
        }
    }

    private async Task ReadLoopAsync(
        AsyncDuplexStreamingCall<AgentMessage, ServerMessage> call,
        SemaphoreSlim writerGate,
        HeartbeatMonitor heartbeatMonitor,
        CancellationToken cancellationToken)
    {
        List<Task> commandTasks = [];
        try
        {
            while (await call.ResponseStream.MoveNext(cancellationToken).ConfigureAwait(false))
            {
                for (int index = commandTasks.Count - 1; index >= 0; index--)
                {
                    if (commandTasks[index].IsCompleted)
                    {
                        await commandTasks[index].ConfigureAwait(false);
                        commandTasks.RemoveAt(index);
                    }
                }

                ServerMessage message = call.ResponseStream.Current;
                switch (message.BodyCase)
                {
                    case ServerMessage.BodyOneofCase.CommandRequest:
                        commandTasks.Add(DispatchAndWriteCommandAsync(call.RequestStream, writerGate, message.CommandRequest.Clone(), cancellationToken));
                        break;
                    case ServerMessage.BodyOneofCase.StreamControl:
                        if (message.StreamControl.Kind == StreamKind.Terminal)
                        {
                            await terminals.HandleControlAsync(message.StreamControl, cancellationToken).ConfigureAwait(false);
                        }
                        else if (message.StreamControl.Kind == StreamKind.FileDownload)
                        {
                            await outboundFiles.HandleControlAsync(message.StreamControl, cancellationToken).ConfigureAwait(false);
                        }
                        else
                        {
                            await transfers.HandleControlAsync(message.StreamControl, cancellationToken).ConfigureAwait(false);
                        }
                        break;
                    case ServerMessage.BodyOneofCase.StreamChunk:
                        if (terminals.Owns(message.StreamChunk.StreamId))
                        {
                            await terminals.HandleChunkAsync(message.StreamChunk, cancellationToken).ConfigureAwait(false);
                        }
                        else
                        {
                            await transfers.HandleChunkAsync(message.StreamChunk, cancellationToken).ConfigureAwait(false);
                        }
                        break;
                    case ServerMessage.BodyOneofCase.None:
                        break;
                    case ServerMessage.BodyOneofCase.Welcome:
                        break;
                    case ServerMessage.BodyOneofCase.HeartbeatAck:
                        heartbeatMonitor.Acknowledge(call.ResponseStream.Current.HeartbeatAck.Sequence, clock.UtcNow);
                        break;
                    case ServerMessage.BodyOneofCase.EventAck:
                        await outbox.AcknowledgeAsync(checked((long)message.EventAck.Sequence), cancellationToken).ConfigureAwait(false);
                        break;
                    default:
                        break;
                }
            }
        }
        finally
        {
            await Task.WhenAll(commandTasks.Select(IgnoreCancellation)).ConfigureAwait(false);
        }
    }

    private async Task DispatchAndWriteCommandAsync(
        IClientStreamWriter<AgentMessage> writer,
        SemaphoreSlim writerGate,
        CommandRequest request,
        CancellationToken cancellationToken)
    {
        CommandResponse response = await commandDispatcher.DispatchAsync(request, cancellationToken).ConfigureAwait(false);
        await WriteAsync(writer, writerGate, new AgentMessage { CommandResponse = response }, cancellationToken).ConfigureAwait(false);
    }

    private async Task HeartbeatLoopAsync(
        IClientStreamWriter<AgentMessage> writer,
        SemaphoreSlim writerGate,
        HeartbeatMonitor heartbeatMonitor,
        CancellationToken cancellationToken)
    {
        ulong sequence = 0;
        using PeriodicTimer timer = new(TimeSpan.FromSeconds(10));
        while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
        {
            if (clock.UtcNow - heartbeatMonitor.LastAcknowledgedAt > TimeSpan.FromSeconds(35))
            {
                throw new InvalidOperationException("heartbeat-ack-timeout");
            }

            await WriteAsync(writer, writerGate, new AgentMessage
            {
                Heartbeat = new Heartbeat
                {
                    Sequence = sequence++,
                    SentAt = Timestamp.FromDateTimeOffset(clock.UtcNow),
                },
            }, cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task WriteAsync(
        IClientStreamWriter<AgentMessage> writer,
        SemaphoreSlim writerGate,
        AgentMessage message,
        CancellationToken cancellationToken)
    {
        await writerGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await writer.WriteAsync(message, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _ = writerGate.Release();
        }
    }

    private async Task MetricLoopAsync(
        IClientStreamWriter<AgentMessage> writer,
        SemaphoreSlim writerGate,
        string nodeId,
        CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(nodeId, out Guid parsedNodeId))
        {
            throw new InvalidOperationException("The Server returned an invalid node identifier.");
        }

        while (!cancellationToken.IsCancellationRequested)
        {
            int interval = Math.Clamp(nodeConfiguration.Current.MetricIntervalSeconds, 1, 60);
            await Task.Delay(TimeSpan.FromSeconds(interval), cancellationToken).ConfigureAwait(false);
            NodeMetricsDto sample = nodeMetrics.Sample();
            await WriteAsync(writer, writerGate, new AgentMessage
            {
                Event = new AgentEvent
                {
                    Type = EventTypes.NodeMetrics,
                    Scope = "node:" + parsedNodeId.ToString("D"),
                    OccurredAt = Timestamp.FromDateTimeOffset(sample.SampledAt),
                    Payload = Google.Protobuf.ByteString.CopyFrom(JsonSerializer.SerializeToUtf8Bytes(sample)),
                },
            }, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task TerminalOutputLoopAsync(
        IClientStreamWriter<AgentMessage> writer,
        SemaphoreSlim writerGate,
        CancellationToken cancellationToken)
    {
        await foreach (AgentMessage message in terminals.ReadOutboundAsync(cancellationToken).ConfigureAwait(false))
        {
            await WriteAsync(writer, writerGate, message, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task FileOutputLoopAsync(
        IClientStreamWriter<AgentMessage> writer,
        SemaphoreSlim writerGate,
        CancellationToken cancellationToken)
    {
        await foreach (AgentMessage message in outboundFiles.ReadOutboundAsync(cancellationToken).ConfigureAwait(false))
        {
            await WriteAsync(writer, writerGate, message, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task OutboxLoopAsync(
        IClientStreamWriter<AgentMessage> writer,
        SemaphoreSlim writerGate,
        CancellationToken cancellationToken)
    {
        using PeriodicTimer idleTimer = new(TimeSpan.FromMilliseconds(250));
        while (!cancellationToken.IsCancellationRequested)
        {
            IReadOnlyList<AgentOutboxItem> pending = await outbox.ReadAsync(1, cancellationToken).ConfigureAwait(false);
            if (pending.Count == 0)
            {
                _ = await idleTimer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false);
                continue;
            }

            AgentOutboxItem item = pending[0];
            using var acknowledgementTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            acknowledgementTimeout.CancelAfter(TimeSpan.FromSeconds(30));
            Task acknowledgement = outbox.WaitForAcknowledgementAsync(item.Sequence, acknowledgementTimeout.Token);
            await WriteAsync(writer, writerGate, new AgentMessage
            {
                Event = new AgentEvent
                {
                    Type = item.Type,
                    Scope = item.Scope,
                    OccurredAt = Timestamp.FromDateTimeOffset(item.OccurredAt),
                    Payload = Google.Protobuf.ByteString.CopyFromUtf8(item.PayloadJson),
                    Sequence = checked((ulong)item.Sequence),
                },
            }, cancellationToken).ConfigureAwait(false);
            await acknowledgement.ConfigureAwait(false);
        }
    }

    private static string MachineFingerprint()
    {
        byte[] data = Encoding.UTF8.GetBytes(Environment.MachineName + "|" + RuntimeInformation.OSArchitecture);
        return Convert.ToHexString(SHA256.HashData(data));
    }

    private static HttpClientHandler CreatePinnedCertificateHandler(string? configuredSha256)
    {
        HttpClientHandler handler = new()
        {
            CheckCertificateRevocationList = true,
            SslProtocols = SslProtocols.None,
        };
        if (string.IsNullOrWhiteSpace(configuredSha256))
        {
            return handler;
        }

        string normalized = configuredSha256.Replace(":", string.Empty, StringComparison.Ordinal).Replace(" ", string.Empty, StringComparison.Ordinal).ToUpperInvariant();
        if (normalized.Length != 64 || normalized.Any(character => !Uri.IsHexDigit(character)))
        {
            handler.Dispose();
            throw new InvalidOperationException("The pinned Server certificate SHA-256 fingerprint is invalid.");
        }

        handler.ServerCertificateCustomValidationCallback = (_, certificate, _, _) =>
            certificate is not null && string.Equals(certificate.GetCertHashString(HashAlgorithmName.SHA256), normalized, StringComparison.Ordinal);
        return handler;
    }

    private static async Task IgnoreCancellation(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
    }

    [LoggerMessage(3001, LogLevel.Warning, "Agent control-plane connection failed with {Reason}")]
    private static partial void ConnectionFailed(ILogger logger, string reason);

    private sealed record AgentWelcomeConfiguration(string? Credential, UpdateNodeRequest? Configuration);

    private sealed class HeartbeatMonitor(DateTimeOffset acknowledgedAt)
    {
        private long _acknowledgedTicks = acknowledgedAt.UtcTicks;
        private long _sequence = -1;

        internal DateTimeOffset LastAcknowledgedAt => new(Interlocked.Read(ref _acknowledgedTicks), TimeSpan.Zero);

        internal void Acknowledge(ulong sequence, DateTimeOffset acknowledgedAt)
        {
            long signedSequence = sequence > long.MaxValue ? long.MaxValue : (long)sequence;
            if (signedSequence < Interlocked.Read(ref _sequence))
            {
                return;
            }

            _ = Interlocked.Exchange(ref _sequence, signedSequence);
            _ = Interlocked.Exchange(ref _acknowledgedTicks, acknowledgedAt.UtcTicks);
        }
    }
}
