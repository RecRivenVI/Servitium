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
using Servitium.Application.Common;
using Servitium.Application.Nodes;
using Servitium.Contracts;
using Servitium.Contracts.Agent;
using Servitium.Contracts.Common;
using Servitium.Contracts.Scheduling;
using Servitium.Server.AgentGateway;
using Servitium.Server.OpenApi;
using Servitium.Server.Security;

namespace Servitium.Server.Endpoints;

/// <summary>把计划任务管理请求转发到拥有权威数据的 Agent，并在成功后刷新控制面镜像。</summary>
internal static class ScheduleEndpoints
{
    internal static void MapScheduleEndpoints(this WebApplication app)
    {
        RouteGroupBuilder schedules = app.MapGroup("/api/v1/instances/{instanceId:guid}/schedules")
            .WithMetadata(new RequiredAuthorization(ServitiumAction.ManageSchedules, ResourceKind.Instance));
        _ = schedules.MapGet("/", ListAsync).ProducesJson<CursorPage<ScheduledTaskDetailsDto>>();
        _ = schedules.MapPost("/", CreateAsync).ProducesJson<ScheduledTaskDetailsDto>(StatusCodes.Status201Created);
        _ = schedules.MapGet("/{scheduleId:guid}", GetAsync).ProducesJson<ScheduledTaskDetailsDto>();
        _ = schedules.MapPut("/{scheduleId:guid}", UpdateAsync).ProducesJson<ScheduledTaskDetailsDto>();
        _ = schedules.MapDelete("/{scheduleId:guid}", DeleteAsync).ProducesEmpty();
        _ = schedules.MapPost("/{scheduleId:guid}/enable", EnableAsync).ProducesJson<ScheduledTaskDetailsDto>();
        _ = schedules.MapPost("/{scheduleId:guid}/disable", DisableAsync).ProducesJson<ScheduledTaskDetailsDto>();
        _ = schedules.MapPost("/{scheduleId:guid}/run", RunAsync).ProducesJson<ScheduledTaskRunDto>();
        _ = schedules.MapGet("/{scheduleId:guid}/runs", ListRunsAsync).ProducesJson<CursorPage<ScheduledTaskRunDto>>();
    }

    private static async Task<IResult> ListAsync(
        HttpContext context,
        Guid instanceId,
        string? cursor,
        int? limit,
        INodeControlStore nodes,
        IAgentGateway gateway,
        CancellationToken cancellationToken)
    {
        Guid? nodeId = await FindNodeIdAsync(instanceId, nodes, cancellationToken).ConfigureAwait(false);
        if (nodeId is null)
        {
            return Problem(context, StatusCodes.Status404NotFound, ErrorCodes.NotFound);
        }

        AgentCommandResult result = await SendAsync(nodeId.Value, "ListSchedules", new AgentInstanceIdCommand(instanceId), gateway, cancellationToken).ConfigureAwait(false);
        if (!result.Succeeded)
        {
            return Problem(context, result);
        }

        IReadOnlyList<ScheduledTaskDetailsDto> items = Deserialize<IReadOnlyList<ScheduledTaskDetailsDto>>(result);
        return Results.Ok(CursorPaginator.Page(items, item => item.Name.ToUpperInvariant(), item => item.Id, cursor, limit is null or 0 ? 50 : limit.Value));
    }

    private static async Task<IResult> CreateAsync(
        HttpContext context,
        Guid instanceId,
        UpsertScheduledTaskRequest request,
        INodeControlStore nodes,
        IAgentGateway gateway,
        NodeReconciler reconciler,
        IGuidFactory guids,
        CancellationToken cancellationToken)
    {
        Guid taskId = guids.Create();
        (Guid? nodeId, AgentCommandResult? result) = await SendForInstanceAsync(
            instanceId,
            "UpsertSchedule",
            new AgentUpsertScheduleCommand(instanceId, taskId, request),
            nodes,
            gateway,
            cancellationToken).ConfigureAwait(false);
        if (nodeId is null)
        {
            return Problem(context, StatusCodes.Status404NotFound, ErrorCodes.NotFound);
        }

        if (!result!.Succeeded)
        {
            return Problem(context, result);
        }

        _ = await reconciler.RefreshAsync(nodeId.Value, cancellationToken).ConfigureAwait(false);
        ScheduledTaskDetailsDto created = Deserialize<ScheduledTaskDetailsDto>(result);
        return Results.Created($"/api/v1/instances/{instanceId:D}/schedules/{created.Id:D}", created);
    }

    private static Task<IResult> GetAsync(
        HttpContext context,
        Guid instanceId,
        Guid scheduleId,
        INodeControlStore nodes,
        IAgentGateway gateway,
        CancellationToken cancellationToken)
    {
        return SendResultAsync<ScheduledTaskDetailsDto>(
            context, instanceId, "GetSchedule", new AgentScheduleIdCommand(instanceId, scheduleId), nodes, gateway, cancellationToken);
    }

    private static async Task<IResult> UpdateAsync(
        HttpContext context,
        Guid instanceId,
        Guid scheduleId,
        UpsertScheduledTaskRequest request,
        INodeControlStore nodes,
        IAgentGateway gateway,
        NodeReconciler reconciler,
        CancellationToken cancellationToken)
    {
        return await SendMutationAsync<ScheduledTaskDetailsDto>(
            context, instanceId, "UpsertSchedule", new AgentUpsertScheduleCommand(instanceId, scheduleId, request),
            nodes, gateway, reconciler, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<IResult> DeleteAsync(
        HttpContext context,
        Guid instanceId,
        Guid scheduleId,
        INodeControlStore nodes,
        IAgentGateway gateway,
        NodeReconciler reconciler,
        CancellationToken cancellationToken)
    {
        (Guid? nodeId, AgentCommandResult? result) = await SendForInstanceAsync(
            instanceId, "DeleteSchedule", new AgentScheduleIdCommand(instanceId, scheduleId), nodes, gateway, cancellationToken).ConfigureAwait(false);
        if (nodeId is null)
        {
            return Problem(context, StatusCodes.Status404NotFound, ErrorCodes.NotFound);
        }

        if (!result!.Succeeded)
        {
            return Problem(context, result);
        }

        _ = await reconciler.RefreshAsync(nodeId.Value, cancellationToken).ConfigureAwait(false);
        return Results.NoContent();
    }

    private static Task<IResult> EnableAsync(
        HttpContext context,
        Guid instanceId,
        Guid scheduleId,
        INodeControlStore nodes,
        IAgentGateway gateway,
        NodeReconciler reconciler,
        CancellationToken cancellationToken)
    {
        return SendMutationAsync<ScheduledTaskDetailsDto>(
            context, instanceId, "SetScheduleEnabled", new AgentSetScheduleEnabledCommand(instanceId, scheduleId, true),
            nodes, gateway, reconciler, cancellationToken);
    }

    private static Task<IResult> DisableAsync(
        HttpContext context,
        Guid instanceId,
        Guid scheduleId,
        INodeControlStore nodes,
        IAgentGateway gateway,
        NodeReconciler reconciler,
        CancellationToken cancellationToken)
    {
        return SendMutationAsync<ScheduledTaskDetailsDto>(
            context, instanceId, "SetScheduleEnabled", new AgentSetScheduleEnabledCommand(instanceId, scheduleId, false),
            nodes, gateway, reconciler, cancellationToken);
    }

    private static Task<IResult> RunAsync(
        HttpContext context,
        Guid instanceId,
        Guid scheduleId,
        INodeControlStore nodes,
        IAgentGateway gateway,
        CancellationToken cancellationToken)
    {
        return SendResultAsync<ScheduledTaskRunDto>(
            context, instanceId, "RunScheduleNow", new AgentScheduleIdCommand(instanceId, scheduleId), nodes, gateway, cancellationToken);
    }

    private static async Task<IResult> ListRunsAsync(
        HttpContext context,
        Guid instanceId,
        Guid scheduleId,
        string? cursor,
        int? limit,
        INodeControlStore nodes,
        IAgentGateway gateway,
        CancellationToken cancellationToken)
    {
        (Guid? nodeId, AgentCommandResult? result) = await SendForInstanceAsync(
            instanceId, "ListScheduleRuns", new AgentScheduleIdCommand(instanceId, scheduleId), nodes, gateway, cancellationToken).ConfigureAwait(false);
        if (nodeId is null)
        {
            return Problem(context, StatusCodes.Status404NotFound, ErrorCodes.NotFound);
        }

        if (!result!.Succeeded)
        {
            return Problem(context, result);
        }

        IReadOnlyList<ScheduledTaskRunDto> items = Deserialize<IReadOnlyList<ScheduledTaskRunDto>>(result);
        return Results.Ok(CursorPaginator.Page(
            items,
            item => item.StartedAt.ToUniversalTime().ToString("O", System.Globalization.CultureInfo.InvariantCulture),
            item => item.Id,
            cursor,
            limit is null or 0 ? 50 : limit.Value));
    }

    private static async Task<IResult> SendResultAsync<TResult>(
        HttpContext context,
        Guid instanceId,
        string type,
        object command,
        INodeControlStore nodes,
        IAgentGateway gateway,
        CancellationToken cancellationToken)
    {
        (Guid? nodeId, AgentCommandResult? result) = await SendForInstanceAsync(instanceId, type, command, nodes, gateway, cancellationToken).ConfigureAwait(false);
        return nodeId is null
            ? Problem(context, StatusCodes.Status404NotFound, ErrorCodes.NotFound)
            : result!.Succeeded ? Results.Ok(Deserialize<TResult>(result)) : Problem(context, result);
    }

    private static async Task<IResult> SendMutationAsync<TResult>(
        HttpContext context,
        Guid instanceId,
        string type,
        object command,
        INodeControlStore nodes,
        IAgentGateway gateway,
        NodeReconciler reconciler,
        CancellationToken cancellationToken)
    {
        (Guid? nodeId, AgentCommandResult? result) = await SendForInstanceAsync(instanceId, type, command, nodes, gateway, cancellationToken).ConfigureAwait(false);
        if (nodeId is null)
        {
            return Problem(context, StatusCodes.Status404NotFound, ErrorCodes.NotFound);
        }

        if (!result!.Succeeded)
        {
            return Problem(context, result);
        }

        _ = await reconciler.RefreshAsync(nodeId.Value, cancellationToken).ConfigureAwait(false);
        return Results.Ok(Deserialize<TResult>(result));
    }

    private static async Task<(Guid? NodeId, AgentCommandResult? Result)> SendForInstanceAsync(
        Guid instanceId,
        string type,
        object command,
        INodeControlStore nodes,
        IAgentGateway gateway,
        CancellationToken cancellationToken)
    {
        Guid? nodeId = await FindNodeIdAsync(instanceId, nodes, cancellationToken).ConfigureAwait(false);
        if (nodeId is null)
        {
            return (null, null);
        }

        return (nodeId, await SendAsync(nodeId.Value, type, command, gateway, cancellationToken).ConfigureAwait(false));
    }

    private static async Task<Guid?> FindNodeIdAsync(Guid instanceId, INodeControlStore nodes, CancellationToken cancellationToken)
    {
        IReadOnlyList<StoredNodeMirror> mirrors = await nodes.ListMirrorsAsync(null, cancellationToken).ConfigureAwait(false);
        return mirrors.SingleOrDefault(mirror => mirror.Instances.Any(instance => instance.Id == instanceId))?.NodeId;
    }

    private static Task<AgentCommandResult> SendAsync(
        Guid nodeId,
        string type,
        object command,
        IAgentGateway gateway,
        CancellationToken cancellationToken)
    {
        return gateway.SendAsync(nodeId, new(type, JsonSerializer.SerializeToUtf8Bytes(command, command.GetType())), cancellationToken);
    }

    private static TResult Deserialize<TResult>(AgentCommandResult result)
    {
        return JsonSerializer.Deserialize<TResult>(result.Payload.Span)
            ?? throw new InvalidOperationException("The Agent returned an invalid scheduled task response.");
    }

    private static IResult Problem(HttpContext context, AgentCommandResult result)
    {
        int status = result.ErrorCode switch
        {
            ErrorCodes.NodeOffline => StatusCodes.Status503ServiceUnavailable,
            ErrorCodes.NotFound => StatusCodes.Status404NotFound,
            ErrorCodes.ValidationFailed => StatusCodes.Status400BadRequest,
            _ => StatusCodes.Status409Conflict,
        };
        return Problem(context, status, result.ErrorCode ?? ErrorCodes.InternalError);
    }

    private static IResult Problem(HttpContext context, int status, string code)
    {
        return ProblemResponses.Create(context, status, code);
    }
}
