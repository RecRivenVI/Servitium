using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

using Servitium.Application.Abstractions;
using Servitium.Application.Authorization;
using Servitium.Application.Results;
using Servitium.Application.Nodes;
using Servitium.Contracts;
using Servitium.Contracts.Agent;
using Servitium.Contracts.Transport;
using Servitium.Contracts.Instances;
using Servitium.Contracts.Nodes;
using Servitium.Contracts.Operations;
using Servitium.Domain.Identity;
using Servitium.Domain.Operations;
using Servitium.Server.AgentGateway;
using Servitium.Server.Hosting;
using Servitium.Server.OpenApi;
using Servitium.Server.Operations;
using Servitium.Server.Security;

namespace Servitium.Server.Endpoints;

internal static partial class InstanceEndpoints
{
    internal static void MapInstanceEndpoints(this WebApplication app)
    {
        RouteGroupBuilder instances = app.MapGroup("/api/v1/instances");
        _ = instances.MapGet("/", ListAsync).ProducesJson<CursorPage<InstanceDto>>();
        _ = instances.MapGet("/grouped", GroupedAsync).ProducesJson<InstanceGroupsDto>();
        _ = instances.MapGet("/{instanceId:guid}", GetAsync)
            .ProducesJson<InstanceDetailsDto>()
            .WithMetadata(new RequiredAuthorization(ServitiumAction.View, ResourceKind.Instance));
        _ = instances.MapPatch("/{instanceId:guid}", UpdateAsync)
            .ProducesJson<InstanceDetailsDto>()
            .WithMetadata(new RequiredAuthorization(ServitiumAction.EditSafeInstanceSettings, ResourceKind.Instance));
        _ = instances.MapPost("/", CreateEmptyAsync)
            .ProducesJson<OperationAcceptedDto>(StatusCodes.Status202Accepted)
            .WithMetadata(new RequiredAuthorization(ServitiumAction.EditDangerousInstanceSettings, ResourceKind.Global));
        _ = instances.MapPost("/attach", AttachAsync)
            .ProducesJson<OperationAcceptedDto>(StatusCodes.Status202Accepted)
            .WithMetadata(new RequiredAuthorization(ServitiumAction.EditDangerousInstanceSettings, ResourceKind.Global));
        _ = instances.MapPost("/from-archive", ImportArchiveAsync)
            .AcceptsMultipart<ServerArchiveUploadRequest>()
            .ProducesJson<OperationAcceptedDto>(StatusCodes.Status202Accepted)
            .WithMetadata(ArchiveUploadPolicy.RequestSizeLimit)
            .WithMetadata(new RequiredAuthorization(ServitiumAction.EditDangerousInstanceSettings, ResourceKind.Global));
        _ = instances.MapDelete("/{instanceId:guid}", UnregisterAsync)
            .ProducesEmpty()
            .WithMetadata(new RequiredAuthorization(ServitiumAction.EditDangerousInstanceSettings, ResourceKind.Instance));
        _ = instances.MapDelete("/{instanceId:guid}/working-directory", DeleteWorkingDirectoryAsync)
            .ProducesJson<OperationAcceptedDto>(StatusCodes.Status202Accepted)
            .WithMetadata(new RequiredAuthorization(ServitiumAction.EditDangerousInstanceSettings, ResourceKind.Instance));
        _ = instances.MapPost("/{instanceId:guid}/start", StartAsync)
            .ProducesJson<OperationAcceptedDto>(StatusCodes.Status202Accepted)
            .WithMetadata(new RequiredAuthorization(ServitiumAction.ControlInstance, ResourceKind.Instance));
        _ = instances.MapPost("/{instanceId:guid}/stop", StopAsync)
            .ProducesJson<OperationAcceptedDto>(StatusCodes.Status202Accepted)
            .WithMetadata(new RequiredAuthorization(ServitiumAction.ControlInstance, ResourceKind.Instance));
        _ = instances.MapPost("/{instanceId:guid}/restart", RestartAsync)
            .ProducesJson<OperationAcceptedDto>(StatusCodes.Status202Accepted)
            .WithMetadata(new RequiredAuthorization(ServitiumAction.ControlInstance, ResourceKind.Instance));
        _ = instances.MapPost("/{instanceId:guid}/force-stop", ForceStopAsync)
            .ProducesJson<OperationAcceptedDto>(StatusCodes.Status202Accepted)
            .WithMetadata(new RequiredAuthorization(ServitiumAction.ControlInstance, ResourceKind.Instance));
        _ = instances.MapPost("/{instanceId:guid}/command", SendCommandAsync)
            .ProducesJson<InstanceRuntimeDto>()
            .WithMetadata(new RequiredAuthorization(ServitiumAction.UseConsole, ResourceKind.Instance));
        _ = instances.MapPost("/{instanceId:guid}/eula/accept", AcceptEulaAsync)
            .ProducesEmpty()
            .WithMetadata(new RequiredAuthorization(ServitiumAction.ControlInstance, ResourceKind.Instance));
        _ = instances.MapGet("/{instanceId:guid}/status", GetStatusAsync)
            .ProducesJson<InstanceRuntimeDto>()
            .WithMetadata(new RequiredAuthorization(ServitiumAction.View, ResourceKind.Instance));
        _ = instances.MapGet("/{instanceId:guid}/metrics", GetMetricsAsync)
            .ProducesJson<InstanceMetricsDto>()
            .WithMetadata(new RequiredAuthorization(ServitiumAction.View, ResourceKind.Instance));
        _ = instances.MapGet("/{instanceId:guid}/metrics/history", ListMetricHistoryAsync)
            .ProducesJson<CursorPage<InstanceMetricSampleDto>>()
            .WithMetadata(new RequiredAuthorization(ServitiumAction.View, ResourceKind.Instance));
        _ = instances.MapGet("/{instanceId:guid}/players", GetPlayersAsync)
            .ProducesJson<PlayerListDto>()
            .WithMetadata(new RequiredAuthorization(ServitiumAction.View, ResourceKind.Instance));
        _ = instances.MapPost("/{instanceId:guid}/rcon/command", SendRconCommandAsync)
            .ProducesJson<RconCommandResultDto>()
            .WithMetadata(new RequiredAuthorization(ServitiumAction.UseConsole, ResourceKind.Instance));
        _ = instances.MapGet("/{instanceId:guid}/logs", ReadLogAsync)
            .ProducesJson<InstanceLogDto>()
            .WithMetadata(new RequiredAuthorization(ServitiumAction.View, ResourceKind.Instance));
        _ = instances.MapGet("/{instanceId:guid}/terminal/session-log", ExportSessionLogAsync)
            .ProducesFile("text/plain")
            .WithMetadata(new RequiredAuthorization(ServitiumAction.View, ResourceKind.Instance));
        _ = instances.MapPost("/{instanceId:guid}/update", RunUpdateAsync)
            .ProducesJson<OperationAcceptedDto>(StatusCodes.Status202Accepted)
            .WithMetadata(new RequiredAuthorization(ServitiumAction.EditDangerousInstanceSettings, ResourceKind.Instance));
        _ = instances.MapPost("/batch/{action}", BatchAsync)
            .ProducesJson<OperationAcceptedDto>(StatusCodes.Status202Accepted)
            .WithMetadata(new RequiredAuthorization(ServitiumAction.EditDangerousInstanceSettings, ResourceKind.Global));
    }

    private static async Task<IResult> GetAsync(HttpContext context, Guid instanceId, INodeControlStore nodes, CancellationToken cancellationToken)
    {
        (Guid NodeId, AgentInstanceResponse Instance)? found = await FindAsync(instanceId, nodes, cancellationToken).ConfigureAwait(false);
        return found is null
            ? ProblemResponses.Create(context, StatusCodes.Status404NotFound, ErrorCodes.NotFound)
            : Results.Ok(new
            {
                instance = ToDto(found.Value.NodeId, null, found.Value.Instance, null),
                found.Value.Instance.Launch,
                found.Value.Instance.Notes,
                found.Value.Instance.Configuration,
                found.Value.Instance.Revision,
            });
    }

    private static async Task<IResult> GroupedAsync(
        HttpContext context,
        Guid? nodeId,
        string? cursor,
        int? limit,
        INodeControlStore nodes,
        CancellationToken cancellationToken)
    {
        AuthorizationPrincipal principal = RequestAuthentication.GetPrincipal(context)!;
        IReadOnlyList<StoredNode> nodeRows = await nodes.ListNodesAsync(cancellationToken).ConfigureAwait(false);
        IReadOnlyList<StoredNodeMirror> mirrors = await nodes.ListMirrorsAsync(null, cancellationToken).ConfigureAwait(false);
        List<InstanceGroupDto> groups = [];
        foreach (StoredNode node in nodeRows.Where(item => nodeId is null || item.Id == nodeId))
        {
            IEnumerable<AgentInstanceResponse> instances = mirrors.SingleOrDefault(item => item.NodeId == node.Id)?.Instances ?? [];
            if (principal.Role == Role.User)
            {
                instances = instances.Where(instance => principal.GrantedInstanceIds.Contains(instance.Id));
            }

            InstanceDto[] items = [.. instances.Select(instance => ToDto(node.Id, node.Name, instance, null))];
            if (principal.Role == Role.User && items.Length == 0) { continue; }
            CursorPage<InstanceDto> page = CursorPaginator.Page(
                items,
                instance => instance.Name.ToUpperInvariant(),
                instance => instance.Id,
                nodeId == node.Id ? cursor : null,
                limit is null or 0 ? 50 : limit.Value);
            NodeDto nodeDto = new(
                node.Id,
                node.Name,
                node.Status,
                node.AgentVersion,
                node.LastSeenAt,
                node.Configuration?.Notes,
                node.OperatingSystem,
                node.Architecture,
                node.ProtocolMajor,
                node.ProtocolMinor,
                items.Length,
                items.Count(instance => instance.Status == "Running"),
                node.Configuration?.PublicAddress,
                node.HostName,
                node.IsLocal);
            groups.Add(new InstanceGroupDto(nodeDto, page.Items, page.NextCursor));
        }

        return Results.Ok(new { groups });
    }

    private static async Task<IResult> UpdateAsync(
        HttpContext context,
        Guid instanceId,
        UpdateInstanceRequest request,
        INodeControlStore nodes,
        IAgentGateway gateway,
        NodeReconciler reconciler,
        CancellationToken cancellationToken)
    {
        (Guid NodeId, AgentInstanceResponse Instance)? found = await FindAsync(instanceId, nodes, cancellationToken).ConfigureAwait(false);
        if (found is null)
        {
            return Problem(context, StatusCodes.Status404NotFound, ErrorCodes.NotFound);
        }

        UpdateInstanceRequest approvedRequest = request;
        if (RequestAuthentication.Get(context)!.User.Role == Role.User)
        {
            UpdateInstanceRequest? current = found.Value.Instance.Configuration;
            if (current is null || HasDangerousInstanceChange(current, request))
            {
                return Problem(context, StatusCodes.Status403Forbidden, ErrorCodes.Forbidden);
            }

            // 普通用户只能修改名称和标签；重新从可信镜像合并，避免以后新增字段时被批量赋值越权。
            approvedRequest = current with { Name = request.Name, Tags = [.. request.Tags] };
        }

        AgentCommandResult result = await gateway.SendAsync(
            found.Value.NodeId,
            new("UpdateInstance", JsonSerializer.SerializeToUtf8Bytes(new AgentUpdateInstanceCommand(instanceId, approvedRequest))),
            cancellationToken).ConfigureAwait(false);
        if (!result.Succeeded)
        {
            return Problem(context, StatusCodes.Status409Conflict, result.ErrorCode ?? ErrorCodes.InternalError);
        }

        AgentInstanceResponse updated = JsonSerializer.Deserialize<AgentInstanceResponse>(result.Payload.Span)
            ?? throw new InvalidOperationException("The Agent returned an invalid instance response.");
        _ = await reconciler.RefreshAsync(found.Value.NodeId, cancellationToken).ConfigureAwait(false);
        return Results.Ok(new { instance = ToDto(found.Value.NodeId, null, updated, null), updated.Launch, updated.Notes, updated.Configuration, updated.Revision });
    }

    internal static bool HasDangerousInstanceChange(UpdateInstanceRequest current, UpdateInstanceRequest requested)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(requested);
        return !string.Equals(current.Notes, requested.Notes, StringComparison.Ordinal) ||
            !LaunchEquals(current.Launch, requested.Launch) ||
            !string.Equals(current.StopCommand, requested.StopCommand, StringComparison.Ordinal) ||
            current.StopTimeoutSeconds != requested.StopTimeoutSeconds ||
            current.AutoStart != requested.AutoStart ||
            current.AutoRestart != requested.AutoRestart ||
            current.MaximumRestartAttempts != requested.MaximumRestartAttempts ||
            current.Rcon != requested.Rcon ||
            current.Query != requested.Query ||
            !string.Equals(current.WorkingDirectory, requested.WorkingDirectory, StringComparison.Ordinal) ||
            current.PrimaryPort != requested.PrimaryPort ||
            current.AutoStartDelaySeconds != requested.AutoStartDelaySeconds ||
            current.RestartIntervalSeconds != requested.RestartIntervalSeconds ||
            current.RestartResetSeconds != requested.RestartResetSeconds ||
            !string.Equals(current.RunCredentialAlias, requested.RunCredentialAlias, StringComparison.Ordinal) ||
            current.Terminal != requested.Terminal ||
            current.UpdateAction != requested.UpdateAction ||
            !SequenceEqual(current.ShortcutCommands, requested.ShortcutCommands);
    }

    private static bool LaunchEquals(StructuredLaunchRequest current, StructuredLaunchRequest requested)
    {
        return current.JavaRuntimeId == requested.JavaRuntimeId &&
            current.MinimumHeapMegabytes == requested.MinimumHeapMegabytes &&
            current.MaximumHeapMegabytes == requested.MaximumHeapMegabytes &&
            current.JvmArguments.SequenceEqual(requested.JvmArguments, StringComparer.Ordinal) &&
            string.Equals(current.TargetKind, requested.TargetKind, StringComparison.Ordinal) &&
            string.Equals(current.Target, requested.Target, StringComparison.Ordinal) &&
            current.ServerArguments.SequenceEqual(requested.ServerArguments, StringComparer.Ordinal) &&
            DictionaryEqual(current.EnvironmentVariables, requested.EnvironmentVariables) &&
            current.AdvancedMode == requested.AdvancedMode &&
            SequenceEqual(current.AdvancedArguments, requested.AdvancedArguments);
    }

    private static bool DictionaryEqual(
        IReadOnlyDictionary<string, string>? current,
        IReadOnlyDictionary<string, string>? requested)
    {
        return ReferenceEquals(current, requested) ||
            (current is not null && requested is not null && current.Count == requested.Count &&
            current.All(pair => requested.TryGetValue(pair.Key, out string? value) &&
                string.Equals(pair.Value, value, StringComparison.Ordinal)));
    }

    private static bool SequenceEqual<T>(IReadOnlyList<T>? current, IReadOnlyList<T>? requested)
    {
        return ReferenceEquals(current, requested) ||
            (current is not null && requested is not null && current.SequenceEqual(requested));
    }

    private static async Task<IResult> BatchAsync(
        HttpContext context,
        string action,
        BatchInstanceActionRequest request,
        INodeControlStore nodes,
        IAgentGateway gateway,
        NodeReconciler reconciler,
        ServerOperationExecutor operations,
        CancellationToken cancellationToken)
    {
        string normalizedAction = action.ToUpperInvariant();
        if (normalizedAction is not ("START" or "STOP" or "RESTART" or "FORCE-STOP" or "UNREGISTER") || request.Items.Count is < 1 or > 64)
        {
            return Problem(context, StatusCodes.Status400BadRequest, ErrorCodes.ValidationFailed);
        }

        InstanceActionTarget[] items = [.. request.Items.Distinct()];
        OperationAcceptedDto accepted = await operations.EnqueueAsync(
            OperationKind.BatchInstances,
            normalizedAction + ":" + items.Length.ToString(System.Globalization.CultureInfo.InvariantCulture),
            RequestAuthentication.Get(context)!.User.Id,
            async (operationId, operationToken) =>
            {
                List<object> results = [];
                HashSet<Guid> changedNodes = [];
                foreach (InstanceActionTarget item in items)
                {
                    AgentStopInstanceCommand? stop = null;
                    if (normalizedAction == "STOP")
                    {
                        (Guid NodeId, AgentInstanceResponse Instance)? found = await FindAsync(item.InstanceId, nodes, operationToken).ConfigureAwait(false);
                        if (found is null || found.Value.NodeId != item.NodeId)
                        {
                            results.Add(new { item.NodeId, item.InstanceId, Succeeded = false, errorCode = ErrorCodes.NotFound });
                            continue;
                        }

                        UpdateInstanceRequest? configuration = found.Value.Instance.Configuration;
                        stop = new(item.InstanceId, configuration?.StopCommand ?? "stop", configuration?.StopTimeoutSeconds ?? 60);
                    }

                    (string commandType, object payload) = normalizedAction switch
                    {
                        "START" => ("StartInstance", (object)new AgentInstanceIdCommand(item.InstanceId)),
                        "STOP" => ("StopInstance", stop!),
                        "RESTART" => ("RestartInstance", new AgentInstanceIdCommand(item.InstanceId)),
                        "FORCE-STOP" => ("ForceStopInstance", new AgentInstanceIdCommand(item.InstanceId)),
                        _ => ("UnregisterInstance", new AgentInstanceIdCommand(item.InstanceId)),
                    };
                    AgentCommandResult result = await gateway.SendAsync(item.NodeId, new(commandType, JsonSerializer.SerializeToUtf8Bytes(payload)), operationToken).ConfigureAwait(false);
                    results.Add(new { item.NodeId, item.InstanceId, result.Succeeded, errorCode = result.ErrorCode });
                    if (result.Succeeded)
                    {
                        _ = changedNodes.Add(item.NodeId);
                    }
                }

                foreach (Guid changedNodeId in changedNodes)
                {
                    _ = await reconciler.RefreshAsync(changedNodeId, operationToken).ConfigureAwait(false);
                }

                return new ServerOperationResult(true, JsonSerializer.Serialize(new { items = results }), null);
            },
            null,
            null,
            cancellationToken).ConfigureAwait(false);
        return Accepted(accepted);
    }

    private static Task<IResult> StartAsync(HttpContext context, Guid instanceId, Guid nodeId, IAgentGateway gateway, NodeReconciler reconciler, ServerOperationExecutor operations, CancellationToken cancellationToken)
    {
        return QueueAgentOperationAsync(context, nodeId, instanceId, OperationKind.StartInstance, "StartInstance", new AgentInstanceIdCommand(instanceId), gateway, reconciler, operations, null, cancellationToken);
    }

    private static async Task<IResult> StopAsync(
        HttpContext context,
        Guid instanceId,
        Guid nodeId,
        INodeControlStore nodes,
        IAgentGateway gateway,
        NodeReconciler reconciler,
        ServerOperationExecutor operations,
        CancellationToken cancellationToken)
    {
        (Guid NodeId, AgentInstanceResponse Instance)? found = await FindAsync(instanceId, nodes, cancellationToken).ConfigureAwait(false);
        if (found is null || found.Value.NodeId != nodeId)
        {
            return Problem(context, StatusCodes.Status404NotFound, ErrorCodes.NotFound);
        }

        UpdateInstanceRequest? configuration = found.Value.Instance.Configuration;
        return await QueueAgentOperationAsync(context, nodeId, instanceId, OperationKind.StopInstance, "StopInstance", new AgentStopInstanceCommand(instanceId, configuration?.StopCommand ?? "stop", configuration?.StopTimeoutSeconds ?? 60), gateway, reconciler, operations, null, cancellationToken).ConfigureAwait(false);
    }

    private static Task<IResult> RestartAsync(HttpContext context, Guid instanceId, Guid nodeId, IAgentGateway gateway, NodeReconciler reconciler, ServerOperationExecutor operations, CancellationToken cancellationToken)
    {
        return QueueAgentOperationAsync(context, nodeId, instanceId, OperationKind.RestartInstance, "RestartInstance", new AgentInstanceIdCommand(instanceId), gateway, reconciler, operations, null, cancellationToken);
    }

    private static Task<IResult> ForceStopAsync(HttpContext context, Guid instanceId, Guid nodeId, IAgentGateway gateway, NodeReconciler reconciler, ServerOperationExecutor operations, CancellationToken cancellationToken)
    {
        return QueueAgentOperationAsync(context, nodeId, instanceId, OperationKind.ForceStopInstance, "ForceStopInstance", new AgentInstanceIdCommand(instanceId), gateway, reconciler, operations, null, cancellationToken);
    }

    private static Task<IResult> SendCommandAsync(
        HttpContext context,
        Guid instanceId,
        Guid nodeId,
        SendInstanceCommandRequest request,
        IAgentGateway gateway,
        NodeReconciler reconciler,
        CancellationToken cancellationToken)
    {
        return SendRuntimeAsync(context, nodeId, "SendCommand", new AgentSendInstanceCommand(instanceId, request.Command), gateway, reconciler, cancellationToken);
    }

    private static async Task<IResult> AcceptEulaAsync(
        HttpContext context,
        Guid instanceId,
        Guid nodeId,
        IAgentGateway gateway,
        CancellationToken cancellationToken)
    {
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(new AgentInstanceIdCommand(instanceId));
        AgentCommandResult result = await gateway.SendAsync(nodeId, new("AcceptEula", payload), cancellationToken).ConfigureAwait(false);
        return result.Succeeded ? Results.NoContent() : Problem(context, StatusCodes.Status409Conflict, result.ErrorCode ?? ErrorCodes.InternalError);
    }

    private static Task<IResult> GetStatusAsync(HttpContext context, Guid instanceId, Guid nodeId, IAgentGateway gateway, NodeReconciler reconciler, CancellationToken cancellationToken)
    {
        return SendRuntimeAsync(context, nodeId, "GetInstanceStatus", new AgentInstanceIdCommand(instanceId), gateway, reconciler, cancellationToken);
    }

    private static async Task<IResult> SendRuntimeAsync<T>(
        HttpContext context,
        Guid nodeId,
        string commandType,
        T command,
        IAgentGateway gateway,
        NodeReconciler reconciler,
        CancellationToken cancellationToken)
    {
        AgentCommandResult result = await gateway.SendAsync(nodeId, new(commandType, JsonSerializer.SerializeToUtf8Bytes(command)), cancellationToken).ConfigureAwait(false);
        if (!result.Succeeded)
        {
            int status = result.ErrorCode == ErrorCodes.NodeOffline ? StatusCodes.Status503ServiceUnavailable : StatusCodes.Status409Conflict;
            return Problem(context, status, result.ErrorCode ?? ErrorCodes.InternalError);
        }

        AgentInstanceRuntimeResponse response = JsonSerializer.Deserialize<AgentInstanceRuntimeResponse>(result.Payload.Span)
            ?? throw new InvalidOperationException("The Agent returned an invalid instance runtime response.");
        _ = await reconciler.RefreshAsync(nodeId, cancellationToken).ConfigureAwait(false);
        return Results.Ok(new InstanceRuntimeDto(response.InstanceId, nodeId, response.Status, response.ProcessId, response.StartedAt, response.LastExitCode, response.RecentOutput));
    }

    private static Task<IResult> GetMetricsAsync(HttpContext context, Guid instanceId, Guid nodeId, IAgentGateway gateway, CancellationToken cancellationToken)
    {
        return SendTypedAsync<InstanceMetricsDto>(context, nodeId, "GetMetrics", new AgentInstanceIdCommand(instanceId), gateway, cancellationToken);
    }

    private static Task<IResult> GetPlayersAsync(HttpContext context, Guid instanceId, Guid nodeId, IAgentGateway gateway, CancellationToken cancellationToken)
    {
        return SendTypedAsync<PlayerListDto>(context, nodeId, "QueryPlayers", new AgentInstanceIdCommand(instanceId), gateway, cancellationToken);
    }

    private static Task<IResult> SendRconCommandAsync(HttpContext context, Guid instanceId, Guid nodeId, RconCommandRequest request, IAgentGateway gateway, CancellationToken cancellationToken)
    {
        return SendTypedAsync<RconCommandResultDto>(context, nodeId, "RconCommand", new AgentSendInstanceCommand(instanceId, request.Command), gateway, cancellationToken);
    }

    private static async Task<IResult> ExportSessionLogAsync(
        HttpContext context,
        Guid instanceId,
        Guid nodeId,
        bool includeAnsi,
        IAgentGateway gateway,
        CancellationToken cancellationToken)
    {
        AgentCommandResult result = await gateway.SendAsync(
            nodeId,
            new("ExportSessionLog", JsonSerializer.SerializeToUtf8Bytes(new AgentExportSessionLogCommand(instanceId, includeAnsi))),
            cancellationToken).ConfigureAwait(false);
        if (!result.Succeeded)
        {
            return Problem(context, StatusCodes.Status409Conflict, result.ErrorCode ?? ErrorCodes.InternalError);
        }

        SessionLogExportDto export = JsonSerializer.Deserialize<SessionLogExportDto>(result.Payload.Span)
            ?? throw new InvalidOperationException("The Agent returned an invalid session log.");
        return Results.File(Encoding.UTF8.GetBytes(export.Content), "text/plain; charset=utf-8", export.FileName);
    }

    private static Task<IResult> ReadLogAsync(
        HttpContext context,
        Guid instanceId,
        Guid nodeId,
        string? file,
        long startLine,
        int? count,
        string? search,
        IAgentGateway gateway,
        CancellationToken cancellationToken)
    {
        return SendTypedAsync<InstanceLogDto>(
            context,
            nodeId,
            "ReadInstanceLog",
            new AgentReadInstanceLogCommand(instanceId, string.IsNullOrWhiteSpace(file) ? "logs/latest.log" : file, startLine, count ?? 500, search),
            gateway,
            cancellationToken);
    }

    private static Task<IResult> RunUpdateAsync(
        HttpContext context,
        Guid instanceId,
        Guid nodeId,
        RunInstanceUpdateRequest request,
        IAgentGateway gateway,
        NodeReconciler reconciler,
        ServerOperationExecutor operations,
        CancellationToken cancellationToken)
    {
        return QueueAgentOperationAsync(context, nodeId, instanceId, OperationKind.UpdateInstance, "RunUpdateAction", new AgentRunInstanceUpdateCommand(instanceId, request.Confirm), gateway, reconciler, operations, null, cancellationToken);
    }

    private static async Task<IResult> SendTypedAsync<TResponse>(
        HttpContext context,
        Guid nodeId,
        string commandType,
        object command,
        IAgentGateway gateway,
        CancellationToken cancellationToken)
    {
        AgentCommandResult result = await gateway.SendAsync(
            nodeId,
            new(commandType, JsonSerializer.SerializeToUtf8Bytes(command, command.GetType())),
            cancellationToken).ConfigureAwait(false);
        if (!result.Succeeded)
        {
            int status = result.ErrorCode == ErrorCodes.NodeOffline ? StatusCodes.Status503ServiceUnavailable : StatusCodes.Status409Conflict;
            return Problem(context, status, result.ErrorCode ?? ErrorCodes.InternalError);
        }

        TResponse response = JsonSerializer.Deserialize<TResponse>(result.Payload.Span)
            ?? throw new InvalidOperationException("The Agent returned an invalid response.");
        return Results.Ok(response);
    }

    private static async Task<IResult> ListAsync(
        HttpContext context,
        Guid? nodeId,
        string? status,
        string? name,
        string[]? tag,
        string? cursor,
        int? limit,
        INodeControlStore nodes,
        InstanceTelemetryStore telemetry,
        IClock clock,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<StoredNodeMirror> mirrors = await nodes.ListMirrorsAsync(nodeId, cancellationToken).ConfigureAwait(false);
        IReadOnlyList<StoredNode> nodeRows = await nodes.ListNodesAsync(cancellationToken).ConfigureAwait(false);
        var nodeNames = nodeRows.ToDictionary(item => item.Id, item => item.Name);
        IEnumerable<InstanceDto> query = mirrors.SelectMany(mirror => mirror.Instances.Select(instance =>
            ToDto(mirror.NodeId, nodeNames.GetValueOrDefault(mirror.NodeId), instance, telemetry.Find(instance.Id, clock.UtcNow))));
        AuthorizationPrincipal principal = RequestAuthentication.GetPrincipal(context)!;
        if (principal.Role == Role.User)
        {
            query = query.Where(instance => principal.GrantedInstanceIds.Contains(instance.Id));
        }
        if (!string.IsNullOrWhiteSpace(status))
        {
            query = query.Where(instance => string.Equals(instance.Status, status, StringComparison.OrdinalIgnoreCase));
        }

        if (!string.IsNullOrWhiteSpace(name))
        {
            query = query.Where(instance => instance.Name.Contains(name, StringComparison.OrdinalIgnoreCase));
        }

        string[] requestedTags = tag?.Where(item => !string.IsNullOrWhiteSpace(item)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray() ?? [];
        if (requestedTags.Length > 0)
        {
            query = query.Where(instance => requestedTags.All(requested => instance.Tags.Contains(requested, StringComparer.OrdinalIgnoreCase)));
        }

        return Results.Ok(CursorPaginator.Page(query, instance => instance.Name.ToUpperInvariant(), instance => instance.Id, cursor, limit is null or 0 ? 50 : limit.Value));
    }

    private static async Task<IResult> CreateEmptyAsync(
        HttpContext context,
        CreateInstanceRequest request,
        IAgentGateway gateway,
        NodeReconciler reconciler,
        IGuidFactory guidFactory,
        ServerOperationExecutor operations,
        CancellationToken cancellationToken)
    {
        if (!ValidInitialConfiguration(request.StopCommand, request.StopTimeoutSeconds))
        {
            return Problem(context, StatusCodes.Status400BadRequest, ErrorCodes.ValidationFailed);
        }

        AgentCreateInstanceCommand command = new(
            guidFactory.Create(),
            request.Name,
            AgentInstanceCreationMode.Empty,
            null,
            null,
            request.Launch,
            request.Tags,
            request.Notes,
            InitialConfiguration(
                request.Name,
                request.Tags,
                request.Notes,
                request.Launch,
                request.StopCommand,
                request.StopTimeoutSeconds));
        return await QueueAgentOperationAsync(context, request.NodeId, command.InstanceId, OperationKind.CreateInstance, "CreateInstance", command, gateway, reconciler, operations, null, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<IResult> AttachAsync(
        HttpContext context,
        AttachInstanceDirectoryRequest request,
        IAgentGateway gateway,
        NodeReconciler reconciler,
        IGuidFactory guidFactory,
        ServerOperationExecutor operations,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<string> tags = request.Tags ?? [];
        if (!ValidInitialConfiguration(request.StopCommand, request.StopTimeoutSeconds))
        {
            return Problem(context, StatusCodes.Status400BadRequest, ErrorCodes.ValidationFailed);
        }

        AgentCreateInstanceCommand command = new(
            guidFactory.Create(),
            request.Name,
            AgentInstanceCreationMode.AttachExternal,
            request.AbsolutePath,
            null,
            request.Launch,
            tags,
            request.Notes,
            InitialConfiguration(
                request.Name,
                tags,
                request.Notes,
                request.Launch,
                request.StopCommand,
                request.StopTimeoutSeconds));
        return await QueueAgentOperationAsync(context, request.NodeId, command.InstanceId, OperationKind.CreateInstance, "CreateInstance", command, gateway, reconciler, operations, null, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<IResult> ImportArchiveAsync(
        HttpContext context,
        IAgentFileTransferGateway gateway,
        NodeReconciler reconciler,
        IGuidFactory guidFactory,
        ServerHostOptions options,
        ServerOperationExecutor operations,
        CancellationToken cancellationToken)
    {
        IFormCollection form = await context.Request.ReadFormAsync(cancellationToken).ConfigureAwait(false);
        if (form.Files.Count != 1 ||
            !Guid.TryParse(form["nodeId"], out Guid nodeId) ||
            string.IsNullOrWhiteSpace(form["name"]) ||
            string.IsNullOrWhiteSpace(form["launch"]))
        {
            return Problem(context, StatusCodes.Status400BadRequest, ErrorCodes.ValidationFailed);
        }

        string stopCommand = string.IsNullOrWhiteSpace(form["stopCommand"])
            ? "stop"
            : form["stopCommand"].ToString().Trim();
        int stopTimeoutSeconds = string.IsNullOrWhiteSpace(form["stopTimeoutSeconds"])
            ? 60
            : int.TryParse(
                form["stopTimeoutSeconds"],
                System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture,
                out int requestedStopTimeout)
                ? requestedStopTimeout
                : 0;
        if (!ValidInitialConfiguration(stopCommand, stopTimeoutSeconds))
        {
            return Problem(context, StatusCodes.Status400BadRequest, ErrorCodes.ValidationFailed);
        }

        string[] tags = [.. form["tags"]
            .Where(tag => !string.IsNullOrWhiteSpace(tag))
            .Select(tag => tag!.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)];
        string? notes = string.IsNullOrWhiteSpace(form["notes"])
            ? null
            : form["notes"].ToString().Trim();

        if (ArchiveUploadPolicy.ExceedsFileLimit(form.Files[0]))
        {
            return Problem(context, StatusCodes.Status413PayloadTooLarge, ErrorCodes.FileTooLarge);
        }

        StructuredLaunchRequest? launch;
        try
        {
            launch = JsonSerializer.Deserialize<StructuredLaunchRequest>(form["launch"]!);
        }
        catch (JsonException)
        {
            return Problem(context, StatusCodes.Status400BadRequest, ErrorCodes.ValidationFailed);
        }

        if (launch is null)
        {
            return Problem(context, StatusCodes.Status400BadRequest, ErrorCodes.ValidationFailed);
        }

        string staging = Path.Combine(options.DataRoot, "staging", "server-uploads");
        _ = Directory.CreateDirectory(staging);
        string upload = Path.Combine(staging, Guid.NewGuid().ToString("N") + ".zip");
        try
        {
            using (FileStream destination = new(upload, FileMode.CreateNew, FileAccess.Write, FileShare.None, 256 * 1024, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await form.Files[0].CopyToAsync(destination, cancellationToken).ConfigureAwait(false);
                await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            AgentCreateInstanceCommand command = new(
                guidFactory.Create(),
                form["name"].ToString(),
                AgentInstanceCreationMode.ServerArchive,
                null,
                null,
                launch,
                tags,
                notes,
                InitialConfiguration(
                    form["name"].ToString(),
                    tags,
                    notes,
                    launch,
                    stopCommand,
                    stopTimeoutSeconds));
            OperationAcceptedDto accepted = await operations.EnqueueAsync(
                OperationKind.ImportServerArchive,
                command.InstanceId.ToString("D"),
                RequestAuthentication.Get(context)!.User.Id,
                async (operationId, operationToken) =>
                {
                    AgentCommandResult result = await gateway.SendFileCommandAsync(
                        nodeId,
                        upload,
                        new("CreateInstance", JsonSerializer.SerializeToUtf8Bytes(command)),
                        operationToken).ConfigureAwait(false);
                    if (result.Succeeded)
                    {
                        _ = await reconciler.RefreshAsync(nodeId, operationToken).ConfigureAwait(false);
                    }

                    return ServerOperationResult.FromAgent(result);
                },
                null,
                _ =>
                {
                    if (File.Exists(upload))
                    {
                        File.Delete(upload);
                    }

                    return Task.CompletedTask;
                },
                cancellationToken).ConfigureAwait(false);
            return Accepted(accepted);
        }
        catch
        {
            if (File.Exists(upload))
            {
                File.Delete(upload);
            }

            throw;
        }
    }

    private static bool ValidInitialConfiguration(string stopCommand, int stopTimeoutSeconds)
    {
        return !string.IsNullOrWhiteSpace(stopCommand) &&
            !stopCommand.Any(char.IsControl) &&
            stopTimeoutSeconds is >= 1 and <= 600;
    }

    private static UpdateInstanceRequest InitialConfiguration(
        string name,
        IReadOnlyList<string> tags,
        string? notes,
        StructuredLaunchRequest launch,
        string stopCommand,
        int stopTimeoutSeconds)
    {
        return new(
            name,
            tags,
            notes,
            launch,
            stopCommand,
            stopTimeoutSeconds,
            false,
            false,
            3,
            new RconSettingsRequest(false, 25575, null),
            new QuerySettingsRequest(false, "127.0.0.1", 25565));
    }

    private static async Task<IResult> UnregisterAsync(
        HttpContext context,
        Guid instanceId,
        Guid nodeId,
        IAgentGateway gateway,
        NodeReconciler reconciler,
        CancellationToken cancellationToken)
    {
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(new AgentInstanceIdCommand(instanceId));
        AgentCommandResult result = await gateway.SendAsync(nodeId, new("UnregisterInstance", payload), cancellationToken).ConfigureAwait(false);
        if (!result.Succeeded)
        {
            return Problem(context, StatusCodes.Status409Conflict, result.ErrorCode ?? ErrorCodes.InternalError);
        }

        _ = await reconciler.RefreshAsync(nodeId, cancellationToken).ConfigureAwait(false);
        return Results.NoContent();
    }

    private static async Task<IResult> DeleteWorkingDirectoryAsync(
        HttpContext context,
        Guid instanceId,
        Guid nodeId,
        IAgentGateway gateway,
        NodeReconciler reconciler,
        ServerOperationExecutor operations,
        CancellationToken cancellationToken)
    {
        return await QueueAgentOperationAsync(context, nodeId, instanceId, OperationKind.DeleteWorkingDirectory, "DeleteWorkingDirectory", new AgentInstanceIdCommand(instanceId), gateway, reconciler, operations, null, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<IResult> QueueAgentOperationAsync(
        HttpContext context,
        Guid nodeId,
        Guid instanceId,
        OperationKind kind,
        string commandType,
        object command,
        IAgentGateway gateway,
        NodeReconciler reconciler,
        ServerOperationExecutor operations,
        Func<CancellationToken, Task>? cleanup,
        CancellationToken cancellationToken)
    {
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(command, command.GetType());
        OperationAcceptedDto accepted = await operations.EnqueueAsync(
            kind,
            instanceId.ToString("D"),
            RequestAuthentication.Get(context)!.User.Id,
            async (operationId, operationToken) =>
            {
                AgentCommandResult result = await gateway.SendAsync(nodeId, new(commandType, payload), operationToken).ConfigureAwait(false);
                if (result.Succeeded)
                {
                    _ = await reconciler.RefreshAsync(nodeId, operationToken).ConfigureAwait(false);
                }

                return ServerOperationResult.FromAgent(result);
            },
            null,
            cleanup,
            cancellationToken).ConfigureAwait(false);
        return Accepted(accepted);
    }

    private static IResult Accepted(OperationAcceptedDto accepted)
    {
        return Results.Accepted("/api/v1/operations/" + accepted.OperationId.ToString("D"), accepted);
    }

    private static InstanceDto ToDto(Guid nodeId, string? nodeName, AgentInstanceResponse response, InstanceTelemetrySnapshot? telemetry)
    {
        return new(
            response.Id,
            nodeId,
            response.Name,
            response.Status,
            response.WorkingDirectory,
            response.OwnedByServitium,
            response.Tags,
            nodeName,
            response.Launch.JavaRuntimeId,
            response.Configuration?.PrimaryPort,
            telemetry?.OnlinePlayers,
            response.StartedAt,
            telemetry?.CpuPercent,
            telemetry?.MemoryBytes,
            response.Software);
    }

    private static async Task<(Guid NodeId, AgentInstanceResponse Instance)?> FindAsync(
        Guid instanceId,
        INodeControlStore nodes,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<StoredNodeMirror> mirrors = await nodes.ListMirrorsAsync(null, cancellationToken).ConfigureAwait(false);
        foreach (StoredNodeMirror mirror in mirrors)
        {
            AgentInstanceResponse? instance = mirror.Instances.SingleOrDefault(item => item.Id == instanceId);
            if (instance is not null)
            {
                return (mirror.NodeId, instance);
            }
        }

        return null;
    }

    private static IResult Problem(HttpContext context, int status, string code)
    {
        return ProblemResponses.Create(context, status, code);
    }
}
