using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

using Servitium.Application.Abstractions;
using Servitium.Application.Authorization;
using Servitium.Application.Results;
using Servitium.Application.Files;
using Servitium.Contracts;
using Servitium.Contracts.Agent;
using Servitium.Contracts.Transport;
using Servitium.Contracts.Configurations;
using Servitium.Contracts.Files;
using Servitium.Contracts.Mods;
using Servitium.Contracts.Operations;
using Servitium.Domain.Operations;
using Servitium.Server.Files;
using Servitium.Server.OpenApi;
using Servitium.Server.Operations;
using Servitium.Server.Security;

namespace Servitium.Server.Endpoints;

internal static class ConfigurationAndModEndpoints
{
    internal static void MapConfigurationAndModEndpoints(this WebApplication app)
    {
        RouteGroupBuilder configurations = app.MapGroup("/api/v1/instances/{instanceId:guid}/configurations")
            .WithMetadata(new RequiredAuthorization(ServitiumAction.ManageConfigurations, ResourceKind.Instance));
        _ = configurations.MapGet("/", ListConfigurationsAsync).ProducesJson<CursorPage<ConfigurationDto>>();
        _ = configurations.MapGet("/{configKey}", ReadConfigurationAsync).ProducesJson<ConfigurationDetailsDto>();
        _ = configurations.MapPut("/{configKey}", WriteConfigurationAsync).ProducesJson<ConfigurationDetailsDto>();
        _ = configurations.MapGet("/schema/{configKey}", GetConfigurationSchemaAsync).ProducesJson<IReadOnlyList<ConfigurationFieldDto>>();
        _ = configurations.MapGet("/{configKey}/backups", ListConfigurationBackupsAsync).ProducesJson<CursorPage<ConfigurationBackupDto>>();
        _ = configurations.MapPost("/{configKey}/backups/{backupId:guid}/restore", RestoreConfigurationBackupAsync).ProducesJson<ConfigurationDetailsDto>();

        RouteGroupBuilder mods = app.MapGroup("/api/v1/instances/{instanceId:guid}/mods")
            .WithMetadata(new RequiredAuthorization(ServitiumAction.ManageMods, ResourceKind.Instance));
        _ = mods.MapGet("/", ListModsAsync).ProducesJson<CursorPage<ModDetailsDto>>();
        _ = mods.MapGet("/{modId}", GetModAsync).ProducesJson<ModDetailsDto>();
        _ = mods.MapPost("/{modId}/enable", EnableModAsync).ProducesJson<ModDetailsDto>();
        _ = mods.MapPost("/{modId}/disable", DisableModAsync).ProducesJson<ModDetailsDto>();
        _ = mods.MapDelete("/{modId}", DeleteModAsync).ProducesEmpty();
        _ = mods.MapGet("/{modId}/config-files", ListModConfigurationsAsync).ProducesJson<CursorPage<string>>();
        _ = mods.MapPost("/upload", UploadModAsync).ProducesJson<OperationAcceptedDto>(StatusCodes.Status202Accepted);
    }

    private static Task<IResult> ListConfigurationsAsync(HttpContext context, Guid instanceId, Guid nodeId, string? cursor, int? limit, IAgentGateway gateway, CancellationToken cancellationToken)
    {
        return SendStringPageAsync<ConfigurationDto>(context, nodeId, "ListConfigurations", new AgentInstanceIdCommand(instanceId), item => item.Key.ToUpperInvariant(), item => item.RelativePath.ToUpperInvariant(), cursor, limit ?? 0, gateway, cancellationToken);
    }

    private static Task<IResult> ReadConfigurationAsync(HttpContext context, Guid instanceId, Guid nodeId, string configKey, IAgentGateway gateway, CancellationToken cancellationToken)
    {
        return SendAsync<ConfigurationDetailsDto>(context, nodeId, "ReadConfiguration", new AgentFilePathCommand(instanceId, configKey), gateway, cancellationToken);
    }

    private static Task<IResult> WriteConfigurationAsync(HttpContext context, Guid instanceId, Guid nodeId, string configKey, WriteConfigurationRequest request, IAgentGateway gateway, CancellationToken cancellationToken)
    {
        return SendAsync<ConfigurationDetailsDto>(context, nodeId, "WriteConfiguration", new AgentWriteConfigurationCommand(instanceId, configKey, request), gateway, cancellationToken);
    }

    private static IResult GetConfigurationSchemaAsync(string configKey)
    {
        return Results.Ok(string.Equals(configKey, "server-properties", StringComparison.Ordinal)
            ? ServerPropertiesSchema.Fields
            : []);
    }

    private static Task<IResult> ListConfigurationBackupsAsync(HttpContext context, Guid instanceId, Guid nodeId, string configKey, string? cursor, int? limit, IAgentGateway gateway, CancellationToken cancellationToken)
    {
        return SendGuidPageAsync<ConfigurationBackupDto>(context, nodeId, "ListConfigBackups", new AgentFilePathCommand(instanceId, configKey), item => item.CreatedAt.ToUniversalTime().ToString("O", System.Globalization.CultureInfo.InvariantCulture), item => item.Id, cursor, limit ?? 0, gateway, cancellationToken);
    }

    private static Task<IResult> RestoreConfigurationBackupAsync(HttpContext context, Guid instanceId, Guid nodeId, string configKey, Guid backupId, IAgentGateway gateway, CancellationToken cancellationToken)
    {
        return SendAsync<ConfigurationDetailsDto>(context, nodeId, "RestoreConfigBackup", new AgentConfigurationBackupCommand(instanceId, configKey, backupId), gateway, cancellationToken);
    }

    private static Task<IResult> ListModsAsync(HttpContext context, Guid instanceId, Guid nodeId, string kind, string? search, string? cursor, int? limit, IAgentGateway gateway, CancellationToken cancellationToken)
    {
        return SendStringPageAsync<ModDetailsDto>(
            context,
            nodeId,
            "ListMods",
            new AgentFilePathCommand(instanceId, kind),
            item => item.Name.ToUpperInvariant(),
            item => item.RelativePath.ToUpperInvariant(),
            cursor,
            limit ?? 0,
            gateway,
            cancellationToken,
            item => string.IsNullOrWhiteSpace(search) || item.Name.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                item.Id.Contains(search, StringComparison.OrdinalIgnoreCase) || item.Authors.Any(author => author.Contains(search, StringComparison.OrdinalIgnoreCase)));
    }

    private static Task<IResult> GetModAsync(HttpContext context, Guid instanceId, Guid nodeId, string kind, string modId, IAgentGateway gateway, CancellationToken cancellationToken)
    {
        return SendAsync<ModDetailsDto>(context, nodeId, "GetModMetadata", new AgentModCommand(instanceId, modId, kind), gateway, cancellationToken);
    }

    private static Task<IResult> EnableModAsync(HttpContext context, Guid instanceId, Guid nodeId, string kind, string modId, IAgentGateway gateway, CancellationToken cancellationToken)
    {
        return SendAsync<ModDetailsDto>(context, nodeId, "ToggleMod", new AgentModToggleCommand(instanceId, modId, kind, true), gateway, cancellationToken);
    }

    private static Task<IResult> DisableModAsync(HttpContext context, Guid instanceId, Guid nodeId, string kind, string modId, IAgentGateway gateway, CancellationToken cancellationToken)
    {
        return SendAsync<ModDetailsDto>(context, nodeId, "ToggleMod", new AgentModToggleCommand(instanceId, modId, kind, false), gateway, cancellationToken);
    }

    private static async Task<IResult> UploadModAsync(
        HttpContext context,
        Guid instanceId,
        Guid nodeId,
        UploadModRequest request,
        FileUploadSessionManager uploads,
        IAgentGateway gateway,
        ServerOperationExecutor operations,
        CancellationToken cancellationToken)
    {
        if (request.Kind is not ("mods" or "plugins"))
        {
            return ProblemResponses.Create(context, StatusCodes.Status400BadRequest, ErrorCodes.ValidationFailed);
        }

        UploadSessionDto session = await uploads.GetAsync(request.UploadId, instanceId, nodeId, cancellationToken).ConfigureAwait(false);
        if (!session.FileName.EndsWith(".jar", StringComparison.OrdinalIgnoreCase))
        {
            return ProblemResponses.Create(context, StatusCodes.Status400BadRequest, ErrorCodes.ValidationFailed);
        }

        OperationAcceptedDto accepted = await operations.EnqueueAsync(
            OperationKind.UploadMod,
            instanceId.ToString("D") + ":" + session.FileName,
            RequestAuthentication.Get(context)!.User.Id,
            async (_, operationToken) =>
            {
                (StoredUploadSession Session, string Path) = await uploads.AssembleAsync(request.UploadId, instanceId, nodeId, request.Sha256, operationToken).ConfigureAwait(false);
                AgentCompleteModUploadCommand command = new(
                    instanceId,
                    request.UploadId,
                    request.Kind,
                    Session.FileName,
                    Session.PartCount,
                    Session.TotalBytes,
                    request.Sha256,
                    Session.Overwrite);
                AgentCommandResult result = await gateway.SendAsync(
                    nodeId,
                    new("CompleteModUpload", JsonSerializer.SerializeToUtf8Bytes(command)),
                    operationToken).ConfigureAwait(false);
                if (result.Succeeded)
                {
                    await uploads.CancelAsync(request.UploadId, instanceId, nodeId, CancellationToken.None).ConfigureAwait(false);
                }

                return ServerOperationResult.FromAgent(result);
            },
            null,
            null,
            cancellationToken).ConfigureAwait(false);
        return Results.Accepted("/api/v1/operations/" + accepted.OperationId.ToString("D"), accepted);
    }

    private static async Task<IResult> DeleteModAsync(HttpContext context, Guid instanceId, Guid nodeId, string kind, string modId, IAgentGateway gateway, CancellationToken cancellationToken)
    {
        AgentCommandResult result = await gateway.SendAsync(
            nodeId,
            new("DeleteMod", JsonSerializer.SerializeToUtf8Bytes(new AgentModCommand(instanceId, modId, kind))),
            cancellationToken).ConfigureAwait(false);
        return result.Succeeded ? Results.NoContent() : Problem(context, result);
    }

    private static Task<IResult> ListModConfigurationsAsync(HttpContext context, Guid instanceId, Guid nodeId, string kind, string modId, string? cursor, int? limit, IAgentGateway gateway, CancellationToken cancellationToken)
    {
        return SendStringPageAsync<string>(
            context,
            nodeId,
            "ListModConfigFiles",
            new AgentModCommand(instanceId, modId, kind),
            path => path.ToUpperInvariant(),
            path => path,
            cursor,
            limit ?? 0,
            gateway,
            cancellationToken);
    }

    private static async Task<IResult> SendAsync<TResult>(HttpContext context, Guid nodeId, string type, object command, IAgentGateway gateway, CancellationToken cancellationToken)
    {
        AgentCommandResult result = await gateway.SendAsync(nodeId, new(type, JsonSerializer.SerializeToUtf8Bytes(command)), cancellationToken).ConfigureAwait(false);
        if (!result.Succeeded)
        {
            return Problem(context, result);
        }

        TResult response = JsonSerializer.Deserialize<TResult>(result.Payload.Span)
            ?? throw new InvalidOperationException("The Agent returned an invalid configuration or mod response.");
        return Results.Ok(response);
    }

    private static async Task<IResult> SendStringPageAsync<T>(
        HttpContext context,
        Guid nodeId,
        string type,
        object command,
        Func<T, string> sortKey,
        Func<T, string> tieBreaker,
        string? cursor,
        int limit,
        IAgentGateway gateway,
        CancellationToken cancellationToken,
        Func<T, bool>? predicate = null)
    {
        AgentCommandResult result = await gateway.SendAsync(nodeId, new(type, JsonSerializer.SerializeToUtf8Bytes(command)), cancellationToken).ConfigureAwait(false);
        if (!result.Succeeded)
        {
            return Problem(context, result);
        }

        IReadOnlyList<T> response = JsonSerializer.Deserialize<IReadOnlyList<T>>(result.Payload.Span)
            ?? throw new InvalidOperationException("The Agent returned an invalid paged response.");
        IEnumerable<T> source = predicate is null ? response : response.Where(predicate);
        return Results.Ok(CursorPaginator.Page(source, sortKey, tieBreaker, cursor, limit == 0 ? 50 : limit));
    }

    private static async Task<IResult> SendGuidPageAsync<T>(
        HttpContext context,
        Guid nodeId,
        string type,
        object command,
        Func<T, string> sortKey,
        Func<T, Guid> id,
        string? cursor,
        int limit,
        IAgentGateway gateway,
        CancellationToken cancellationToken)
    {
        AgentCommandResult result = await gateway.SendAsync(nodeId, new(type, JsonSerializer.SerializeToUtf8Bytes(command)), cancellationToken).ConfigureAwait(false);
        if (!result.Succeeded)
        {
            return Problem(context, result);
        }

        IReadOnlyList<T> response = JsonSerializer.Deserialize<IReadOnlyList<T>>(result.Payload.Span)
            ?? throw new InvalidOperationException("The Agent returned an invalid paged response.");
        return Results.Ok(CursorPaginator.Page(response, sortKey, id, cursor, limit == 0 ? 50 : limit));
    }

    private static IResult Problem(HttpContext context, AgentCommandResult result)
    {
        int status = result.ErrorCode == ErrorCodes.NodeOffline ? StatusCodes.Status503ServiceUnavailable : StatusCodes.Status409Conflict;
        return ProblemResponses.Create(context, status, result.ErrorCode ?? ErrorCodes.InternalError);
    }
}
