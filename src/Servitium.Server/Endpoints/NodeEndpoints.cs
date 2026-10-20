using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

using Servitium.Application.Abstractions;
using Servitium.Application.Authorization;
using Servitium.Application.Results;
using Servitium.Application.Identity;
using Servitium.Application.Nodes;
using Servitium.Contracts.Agent;
using Servitium.Contracts;
using Servitium.Contracts.Transport;
using Servitium.Contracts.Events;
using Servitium.Contracts.Java;
using Servitium.Contracts.Nodes;
using Servitium.Contracts.Operations;
using Servitium.Domain.Operations;
using Servitium.Server.AgentGateway;
using Servitium.Server.Governance;
using Servitium.Server.Hosting;
using Servitium.Server.Operations;
using Servitium.Server.OpenApi;
using Servitium.Server.Realtime;
using Servitium.Server.Security;

namespace Servitium.Server.Endpoints;

internal static class NodeEndpoints
{
    internal static void MapNodeEndpoints(this WebApplication app)
    {
        RouteGroupBuilder nodes = app.MapGroup("/api/v1/nodes")
            .WithMetadata(new RequiredAuthorization(ServitiumAction.ManageNodes, ResourceKind.Node));
        _ = nodes.MapGet("/", ListNodesAsync).ProducesJson<CursorPage<NodeDto>>();
        _ = nodes.MapGet("/{nodeId:guid}", GetNodeAsync).ProducesJson<NodeDetailsDto>();
        _ = nodes.MapPatch("/{nodeId:guid}", UpdateNodeAsync).ProducesJson<NodeDto>();
        _ = nodes.MapDelete("/{nodeId:guid}", RemoveNodeAsync).ProducesEmpty();
        _ = nodes.MapPost("/registration-tokens", CreateRegistrationTokenAsync).ProducesJson<NodeRegistrationTokenResponse>(StatusCodes.Status201Created);
        _ = nodes.MapGet("/registration-tokens", ListRegistrationTokensAsync).ProducesJson<CursorPage<NodeRegistrationTokenDto>>();
        _ = nodes.MapDelete("/registration-tokens/{tokenId:guid}", RevokeRegistrationTokenAsync).ProducesEmpty();
        _ = nodes.MapPost("/{nodeId:guid}/reconnect", ReconnectAsync).ProducesEmpty(StatusCodes.Status202Accepted);
        _ = nodes.MapPost("/{nodeId:guid}/credential/rotate", RotateCredentialAsync).ProducesJson<NodeCredentialRotationDto>();
        _ = nodes.MapPost("/{nodeId:guid}/credential/revoke", RevokeCredentialAsync).ProducesEmpty();
        _ = nodes.MapGet("/{nodeId:guid}/metrics", ListMetricsAsync).ProducesJson<CursorPage<NodeMetricsDto>>();
        _ = nodes.MapGet("/{nodeId:guid}/java-runtimes", ListJavaRuntimesAsync).ProducesJson<CursorPage<JavaRuntimeDto>>();
        _ = nodes.MapPost("/{nodeId:guid}/java-runtimes/discover", DiscoverJavaRuntimesAsync).ProducesJson<OperationAcceptedDto>(StatusCodes.Status202Accepted);
        _ = nodes.MapPost("/{nodeId:guid}/java-runtimes", RegisterJavaRuntimeAsync).ProducesJson<JavaRuntimeDto>(StatusCodes.Status201Created);
        _ = nodes.MapPost("/{nodeId:guid}/java-runtimes/import", ImportJavaRuntimeAsync)
            .AcceptsMultipart<SingleFileUploadRequest>()
            .ProducesJson<OperationAcceptedDto>(StatusCodes.Status202Accepted)
            .WithMetadata(ArchiveUploadPolicy.RequestSizeLimit);
        _ = nodes.MapGet("/{nodeId:guid}/java-runtimes/{runtimeId:guid}", GetJavaRuntimeAsync).ProducesJson<JavaRuntimeDetailsDto>();
        _ = nodes.MapDelete("/{nodeId:guid}/java-runtimes/{runtimeId:guid}", UnregisterJavaRuntimeAsync).ProducesJson<OperationAcceptedDto>(StatusCodes.Status202Accepted);
    }

    private static async Task<IResult> ListMetricsAsync(
        HttpContext context,
        Guid nodeId,
        DateTimeOffset? since,
        string? cursor,
        int? limit,
        INodeControlStore store,
        IClock clock,
        CancellationToken cancellationToken)
    {
        if (await store.FindNodeAsync(nodeId, cancellationToken).ConfigureAwait(false) is null)
        {
            return ProblemResponses.Create(context, StatusCodes.Status404NotFound, ErrorCodes.NotFound);
        }

        DateTimeOffset earliest = clock.UtcNow.AddHours(-24);
        DateTimeOffset requested = since is null || since < earliest ? earliest : since.Value;
        IReadOnlyList<StoredNodeMetric> metrics = await store.ListMetricsAsync(nodeId, requested, cancellationToken).ConfigureAwait(false);
        return Results.Ok(CursorPaginator.Page(
            metrics.Select(item => item.Metrics),
            item => item.SampledAt.ToUniversalTime().ToString("O", System.Globalization.CultureInfo.InvariantCulture),
            item => FormattableString.Invariant($"{item.CpuPercent:R}:{item.MemoryUsedBytes}:{item.MemoryTotalBytes}:{item.DiskFreeBytes}"),
            cursor,
            limit is null or 0 ? 200 : limit.Value));
    }

    private static async Task<IResult> ListJavaRuntimesAsync(Guid nodeId, string? cursor, int? limit, INodeControlStore store, CancellationToken cancellationToken)
    {
        StoredNodeMirror? mirror = (await store.ListMirrorsAsync(nodeId, cancellationToken).ConfigureAwait(false)).SingleOrDefault();
        IEnumerable<JavaRuntimeDto> runtimes = (mirror?.JavaRuntimes ?? []).Select(runtime =>
            ToJavaDto(nodeId, runtime, mirror is null ? 0 : mirror.Instances.Count(instance => instance.Launch.JavaRuntimeId == runtime.Id)));
        return Results.Ok(CursorPaginator.Page(runtimes, runtime => (runtime.DisplayName ?? runtime.ExecutablePath).ToUpperInvariant(), runtime => runtime.Id, cursor, limit is null or 0 ? 50 : limit.Value));
    }

    private static Task<IResult> DiscoverJavaRuntimesAsync(
        HttpContext context,
        Guid nodeId,
        IAgentGateway gateway,
        NodeReconciler reconciler,
        ServerOperationExecutor operations,
        CancellationToken cancellationToken)
    {
        return QueueJavaOperationAsync(context, nodeId, OperationKind.DiscoverJava, "discover", new("DiscoverJava", ReadOnlyMemory<byte>.Empty), gateway, reconciler, operations, cancellationToken);
    }

    private static async Task<IResult> RegisterJavaRuntimeAsync(
        HttpContext context,
        Guid nodeId,
        RegisterJavaRuntimeRequest request,
        IAgentGateway gateway,
        NodeReconciler reconciler,
        CancellationToken cancellationToken)
    {
        byte[] payload = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new AgentRegisterJavaCommand(request.ExecutablePath));
        AgentCommandResult result = await gateway.SendAsync(nodeId, new("RegisterJava", payload), cancellationToken).ConfigureAwait(false);
        if (!result.Succeeded)
        {
            return JavaProblem(context, result);
        }

        AgentJavaRuntimeResponse response = System.Text.Json.JsonSerializer.Deserialize<AgentJavaRuntimeResponse>(result.Payload.Span)
            ?? throw new InvalidOperationException("The Agent returned an invalid Java runtime response.");
        _ = await reconciler.RefreshAsync(nodeId, cancellationToken).ConfigureAwait(false);
        return Results.Created(
            $"/api/v1/nodes/{nodeId:D}/java-runtimes/{response.Id:D}",
            ToJavaDto(nodeId, response));
    }

    private static async Task<IResult> UnregisterJavaRuntimeAsync(
        HttpContext context,
        Guid nodeId,
        Guid runtimeId,
        bool deleteFiles,
        bool force,
        IAgentGateway gateway,
        NodeReconciler reconciler,
        ServerOperationExecutor operations,
        CancellationToken cancellationToken)
    {
        byte[] payload = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new AgentUnregisterJavaCommand(runtimeId, deleteFiles, force));
        return await QueueJavaOperationAsync(context, nodeId, OperationKind.UnregisterJava, runtimeId.ToString("D"), new("UnregisterJava", payload), gateway, reconciler, operations, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<IResult> ImportJavaRuntimeAsync(
        HttpContext context,
        Guid nodeId,
        IAgentFileTransferGateway gateway,
        NodeReconciler reconciler,
        ServerHostOptions options,
        ServerOperationExecutor operations,
        CancellationToken cancellationToken)
    {
        IFormCollection form = await context.Request.ReadFormAsync(cancellationToken).ConfigureAwait(false);
        if (form.Files.Count != 1)
        {
            return ProblemResponses.Create(context, StatusCodes.Status400BadRequest, ErrorCodes.ValidationFailed);
        }

        IFormFile file = form.Files[0];
        if (ArchiveUploadPolicy.ExceedsFileLimit(file))
        {
            return ProblemResponses.Create(context, StatusCodes.Status413PayloadTooLarge, ErrorCodes.FileTooLarge);
        }

        string staging = Path.Combine(options.DataRoot, "staging", "java-uploads");
        _ = Directory.CreateDirectory(staging);
        string archiveSuffix = file.FileName.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase) ? ".tar.gz" : ".zip";
        string upload = Path.Combine(staging, Guid.NewGuid().ToString("N") + archiveSuffix);
        try
        {
            using (FileStream destination = new(upload, FileMode.CreateNew, FileAccess.Write, FileShare.None, 256 * 1024, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await file.CopyToAsync(destination, cancellationToken).ConfigureAwait(false);
                await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            OperationAcceptedDto accepted = await operations.EnqueueAsync(
                OperationKind.ImportJava,
                nodeId.ToString("D") + ":" + file.FileName,
                RequestAuthentication.Get(context)!.User.Id,
                async (operationId, operationToken) =>
                {
                    AgentCommandResult result = await gateway.SendFileCommandAsync(
                        nodeId,
                        upload,
                        new("ImportJava", System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new AgentImportJavaCommand(null))),
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
            return Results.Accepted("/api/v1/operations/" + accepted.OperationId.ToString("D"), accepted);
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

    private static async Task<IResult> QueueJavaOperationAsync(
        HttpContext context,
        Guid nodeId,
        OperationKind kind,
        string target,
        AgentCommand command,
        IAgentGateway gateway,
        NodeReconciler reconciler,
        ServerOperationExecutor operations,
        CancellationToken cancellationToken)
    {
        OperationAcceptedDto accepted = await operations.EnqueueAsync(
            kind,
            nodeId.ToString("D") + ":" + target,
            RequestAuthentication.Get(context)!.User.Id,
            async (operationId, operationToken) =>
            {
                AgentCommandResult result = await gateway.SendAsync(nodeId, command, operationToken).ConfigureAwait(false);
                if (result.Succeeded)
                {
                    _ = await reconciler.RefreshAsync(nodeId, operationToken).ConfigureAwait(false);
                }

                return ServerOperationResult.FromAgent(result);
            },
            null,
            null,
            cancellationToken).ConfigureAwait(false);
        return Results.Accepted("/api/v1/operations/" + accepted.OperationId.ToString("D"), accepted);
    }

    private static async Task<IResult> GetJavaRuntimeAsync(HttpContext context, Guid nodeId, Guid runtimeId, INodeControlStore store, CancellationToken cancellationToken)
    {
        StoredNodeMirror? mirror = (await store.ListMirrorsAsync(nodeId, cancellationToken).ConfigureAwait(false)).SingleOrDefault();
        AgentJavaRuntimeResponse? runtime = mirror?.JavaRuntimes.SingleOrDefault(item => item.Id == runtimeId);
        if (runtime is null)
        {
            return ProblemResponses.Create(context, StatusCodes.Status404NotFound, ErrorCodes.NotFound);
        }

        Guid[] usedBy = [.. mirror!.Instances.Where(instance => instance.Launch.JavaRuntimeId == runtimeId).Select(instance => instance.Id)];
        return Results.Ok(new { runtime = ToJavaDto(nodeId, runtime, usedBy.Length), usedByInstances = usedBy });
    }

    private static JavaRuntimeDto ToJavaDto(Guid nodeId, AgentJavaRuntimeResponse runtime, int usedBy = 0)
    {
        return new(
            runtime.Id,
            nodeId,
            runtime.ExecutablePath,
            runtime.Version,
            runtime.Vendor,
            runtime.Architecture,
            runtime.Source,
            runtime.Vendor + " Java " + MajorVersion(runtime.Version).ToString(System.Globalization.CultureInfo.InvariantCulture),
            MajorVersion(runtime.Version),
            runtime.Imported,
            runtime.RegisteredAt,
            runtime.VmName,
            usedBy);
    }

    private static int MajorVersion(string version)
    {
        string[] parts = version.Split(['.', '-', '+'], StringSplitOptions.RemoveEmptyEntries);
        int index = parts.Length > 1 && parts[0] == "1" ? 1 : 0;
        return parts.Length > index && int.TryParse(parts[index], out int major) ? major : 0;
    }

    private static IResult JavaProblem(HttpContext context, AgentCommandResult result)
    {
        int status = result.ErrorCode == ErrorCodes.NodeOffline ? StatusCodes.Status503ServiceUnavailable : StatusCodes.Status409Conflict;
        return ProblemResponses.Create(context, status, result.ErrorCode ?? ErrorCodes.InternalError);
    }

    private static async Task<IResult> ListNodesAsync(
        string? status,
        string? name,
        string? cursor,
        int? limit,
        INodeControlStore store,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<StoredNode> nodes = await store.ListNodesAsync(cancellationToken).ConfigureAwait(false);
        IReadOnlyList<StoredNodeMirror> mirrors = await store.ListMirrorsAsync(null, cancellationToken).ConfigureAwait(false);
        var mirrorByNode = mirrors.ToDictionary(item => item.NodeId);
        IEnumerable<NodeDto> query = nodes.Select(node => ToNodeDto(node, mirrorByNode.GetValueOrDefault(node.Id)));
        if (!string.IsNullOrWhiteSpace(status))
        {
            query = query.Where(node => string.Equals(node.Status, status, StringComparison.OrdinalIgnoreCase));
        }

        if (!string.IsNullOrWhiteSpace(name))
        {
            query = query.Where(node => node.Name.Contains(name, StringComparison.OrdinalIgnoreCase));
        }

        return Results.Ok(CursorPaginator.Page(query, node => node.Name.ToUpperInvariant(), node => node.Id, cursor, limit is null or 0 ? 50 : limit.Value));
    }

    private static NodeDto ToNodeDto(StoredNode node, StoredNodeMirror? mirror)
    {
        int total = mirror?.Instances.Count ?? 0;
        int running = mirror?.Instances.Count(instance => string.Equals(instance.Status, "Running", StringComparison.Ordinal)) ?? 0;
        return new(
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
            total,
            running,
            node.Configuration?.PublicAddress);
    }

    private static async Task<IResult> GetNodeAsync(HttpContext context, Guid nodeId, INodeControlStore store, CancellationToken cancellationToken)
    {
        StoredNode? node = await store.FindNodeAsync(nodeId, cancellationToken).ConfigureAwait(false);
        return node is null
            ? ProblemResponses.Create(context, StatusCodes.Status404NotFound, ErrorCodes.NotFound)
            : Results.Ok(new
            {
                node.Id,
                node.Name,
                node.Status,
                node.AgentVersion,
                node.ProtocolMajor,
                node.ProtocolMinor,
                node.OperatingSystem,
                node.Architecture,
                node.CreatedAt,
                node.LastSeenAt,
                configuration = node.Configuration,
            });
    }

    private static async Task<IResult> UpdateNodeAsync(
        HttpContext context,
        Guid nodeId,
        UpdateNodeRequest request,
        INodeControlStore store,
        IAgentGateway gateway,
        RuntimeSettings runtimeSettings,
        CancellationToken cancellationToken)
    {
        NodeConfigurationValidator.Validate(request);
        if (request.AdvancedRawCommandsEnabled && !runtimeSettings.AdvancedRawCommandsEnabled)
        {
            return ProblemResponses.Create(context, StatusCodes.Status409Conflict, ErrorCodes.RawCommandDisabled);
        }

        UpdateNodeRequest normalized = request with
        {
            Name = request.Name.Trim(),
            AdvancedRawCommandsEnabled = runtimeSettings.AdvancedRawCommandsEnabled,
            PrivateSourceAuthorities = runtimeSettings.PrivateSourceAuthorities,
        };
        StoredNode? node = await store.FindNodeAsync(nodeId, cancellationToken).ConfigureAwait(false);
        if (node is null)
        {
            return ProblemResponses.Create(context, StatusCodes.Status404NotFound, ErrorCodes.NotFound);
        }

        AgentCommandResult result = await gateway.SendAsync(
            nodeId,
            new("UpdateNodeConfig", System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new AgentUpdateNodeConfigurationCommand(normalized))),
            cancellationToken).ConfigureAwait(false);
        if (!result.Succeeded)
        {
            return JavaProblem(context, result);
        }

        StoredNode updated = node with { Name = normalized.Name, Configuration = normalized };
        await store.UpdateNodeAsync(updated, cancellationToken).ConfigureAwait(false);
        return Results.Ok(new NodeDto(updated.Id, updated.Name, updated.Status, updated.AgentVersion, updated.LastSeenAt));
    }

    private static async Task<IResult> RemoveNodeAsync(
        HttpContext context,
        Guid nodeId,
        bool removeRegistrations,
        INodeControlStore store,
        IAgentGateway gateway,
        AgentConnectionManager connections,
        IClock clock,
        CancellationToken cancellationToken)
    {
        StoredNode? node = await store.FindNodeAsync(nodeId, cancellationToken).ConfigureAwait(false);
        if (node is null)
        {
            return ProblemResponses.Create(context, StatusCodes.Status404NotFound, ErrorCodes.NotFound);
        }

        StoredNodeMirror? mirror = (await store.ListMirrorsAsync(nodeId, cancellationToken).ConfigureAwait(false)).SingleOrDefault();
        AgentInstanceResponse[] instances = mirror?.Instances.ToArray() ?? [];
        if (instances.Length > 0 && !removeRegistrations)
        {
            return ProblemResponses.Create(
                context,
                StatusCodes.Status409Conflict,
                ErrorCodes.NodeHasInstances,
                new Dictionary<string, string>
                {
                    ["count"] = instances.Length.ToString(System.Globalization.CultureInfo.InvariantCulture),
                });
        }

        foreach (AgentInstanceResponse instance in instances.Where(_ => string.Equals(node.Status, "Online", StringComparison.Ordinal)))
        {
            AgentCommandResult removed = await gateway.SendAsync(
                nodeId,
                new("UnregisterInstance", System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new AgentInstanceIdCommand(instance.Id))),
                cancellationToken).ConfigureAwait(false);
            if (!removed.Succeeded)
            {
                return JavaProblem(context, removed);
            }
        }

        await store.RevokeCredentialsAsync(nodeId, clock.UtcNow, cancellationToken).ConfigureAwait(false);
        _ = connections.Disconnect(nodeId);
        await store.RemoveNodeAsync(nodeId, cancellationToken).ConfigureAwait(false);
        return Results.NoContent();
    }

    private static async Task<IResult> CreateRegistrationTokenAsync(
        HttpContext context,
        CreateNodeRegistrationTokenRequest request,
        INodeControlStore store,
        ITokenGenerator tokenGenerator,
        IGuidFactory guidFactory,
        IClock clock,
        CancellationToken cancellationToken)
    {
        if (request.LifetimeMinutes is < 1 or > 1440)
        {
            return ProblemResponses.Create(
                context,
                StatusCodes.Status400BadRequest,
                ErrorCodes.ValidationFailed,
                errors: new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
                {
                    [nameof(request.LifetimeMinutes)] = ["range"],
                });
        }

        int minutes = request.LifetimeMinutes;
        string plaintext = tokenGenerator.Generate(32);
        DateTimeOffset now = clock.UtcNow;
        StoredNodeRegistrationToken token = new(
            guidFactory.Create(),
            IdentityApplicationService.HashToken(plaintext),
            string.IsNullOrWhiteSpace(request.ExpectedName) ? null : request.ExpectedName.Trim(),
            now,
            now.AddMinutes(minutes),
            null,
            false);
        await store.AddRegistrationTokenAsync(token, cancellationToken).ConfigureAwait(false);
        return Results.Created("/api/v1/nodes/registration-tokens/" + token.Id.ToString("D"), new NodeRegistrationTokenResponse(plaintext, token.ExpiresAt));
    }

    private static async Task<IResult> ListRegistrationTokensAsync(
        string? cursor,
        int? limit,
        INodeControlStore store,
        IClock clock,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<StoredNodeRegistrationToken> tokens = await store.ListRegistrationTokensAsync(clock.UtcNow, cancellationToken).ConfigureAwait(false);
        IEnumerable<NodeRegistrationTokenDto> items = tokens.Select(token => new NodeRegistrationTokenDto(token.Id, token.ExpectedName, token.CreatedAt, token.ExpiresAt));
        return Results.Ok(CursorPaginator.Page(
            items,
            token => token.CreatedAt.ToUniversalTime().ToString("O", System.Globalization.CultureInfo.InvariantCulture),
            token => token.Id,
            cursor,
            limit is null or 0 ? 50 : limit.Value));
    }

    private static async Task<IResult> RevokeRegistrationTokenAsync(
        Guid tokenId,
        INodeControlStore store,
        CancellationToken cancellationToken)
    {
        await store.RevokeRegistrationTokenAsync(tokenId, cancellationToken).ConfigureAwait(false);
        return Results.NoContent();
    }

    private static IResult ReconnectAsync(Guid nodeId, AgentConnectionManager connections)
    {
        _ = connections.Disconnect(nodeId);
        return Results.Accepted();
    }

    private static async Task<IResult> RotateCredentialAsync(
        HttpContext context,
        Guid nodeId,
        INodeControlStore store,
        AgentConnectionManager connections,
        ITokenGenerator tokenGenerator,
        IGuidFactory guidFactory,
        IClock clock,
        IRealtimeEventStream realtimeEvents,
        CancellationToken cancellationToken)
    {
        StoredNode? node = await store.FindNodeAsync(nodeId, cancellationToken).ConfigureAwait(false);
        if (node is null || node.CredentialRevoked)
        {
            return ProblemResponses.Create(context, StatusCodes.Status404NotFound, ErrorCodes.NotFound);
        }

        int revision = await store.GetCredentialRevisionAsync(nodeId, cancellationToken).ConfigureAwait(false) + 1;
        DateTimeOffset now = clock.UtcNow;
        string plaintext = tokenGenerator.Generate(32);
        StoredNodeCredential replacement = new(guidFactory.Create(), nodeId, IdentityApplicationService.HashToken(plaintext), now, null, revision);
        await store.StageCredentialAsync(replacement, cancellationToken).ConfigureAwait(false);
        AgentCommandResult result = await connections.SendAsync(
            nodeId,
            new("RotateCredential", System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new AgentRotateCredentialCommand(plaintext, revision))),
            cancellationToken).ConfigureAwait(false);
        if (!result.Succeeded)
        {
            await store.RevokeCredentialAsync(replacement.Id, clock.UtcNow, cancellationToken).ConfigureAwait(false);
            return JavaProblem(context, result);
        }

        await store.ActivateCredentialAsync(nodeId, replacement.Id, clock.UtcNow, cancellationToken).ConfigureAwait(false);
        _ = await realtimeEvents.PublishAsync(
            EventTypes.NodeCredentialRotated,
            "node:" + nodeId.ToString("D"),
            new NodeCredentialRotatedPayload(nodeId),
            cancellationToken).ConfigureAwait(false);
        _ = connections.Disconnect(nodeId);
        return Results.Ok(new NodeCredentialRotationDto(revision, now));
    }

    private static async Task<IResult> RevokeCredentialAsync(
        Guid nodeId,
        INodeControlStore store,
        AgentConnectionManager connections,
        IClock clock,
        IRealtimeEventStream realtimeEvents,
        CancellationToken cancellationToken)
    {
        await store.RevokeCredentialsAsync(nodeId, clock.UtcNow, cancellationToken).ConfigureAwait(false);
        _ = await realtimeEvents.PublishAsync(
            EventTypes.NodeStatusChanged,
            "node:" + nodeId.ToString("D"),
            new NodeStatusChangedPayload(nodeId, "Revoked", "credential-revoked"),
            cancellationToken).ConfigureAwait(false);
        _ = connections.Disconnect(nodeId);
        return Results.NoContent();
    }
}
