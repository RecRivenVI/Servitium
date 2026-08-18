using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

using Servitium.Application.Abstractions;
using Servitium.Application.Authorization;
using Servitium.Application.Identity;
using Servitium.Application.Nodes;
using Servitium.Contracts.Governance;
using Servitium.Contracts.Nodes;
using Servitium.Server.OpenApi;
using Servitium.Server.Security;

namespace Servitium.Server.Governance;

internal static class DashboardEndpoints
{
    internal static void MapDashboardEndpoints(this WebApplication app)
    {
        _ = app.MapGet("/api/v1/dashboard", GetAsync)
            .ProducesJson<DashboardOverviewDto>()
            .WithMetadata(new RequiredAuthorization(ServitiumAction.ManageUsers, ResourceKind.Global));
    }

    private static async Task<IResult> GetAsync(
        INodeControlStore nodes,
        IIdentityStore identities,
        IClock clock,
        RequestTelemetry telemetry,
        ControlPlaneProcessMetrics processMetrics,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<StoredNode> nodeRows = await nodes.ListNodesAsync(cancellationToken).ConfigureAwait(false);
        IReadOnlyList<StoredNodeMirror> mirrors = await nodes.ListMirrorsAsync(null, cancellationToken).ConfigureAwait(false);
        int activeSessions = await identities.CountActiveSessionsAsync(clock.UtcNow, cancellationToken).ConfigureAwait(false);
        var mirrorsByNode = mirrors.ToDictionary(mirror => mirror.NodeId);
        List<DashboardNodeDto> overview = [];
        foreach (StoredNode node in nodeRows.OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase))
        {
            IReadOnlyList<StoredNodeMetric> metrics = await nodes.ListMetricsAsync(node.Id, clock.UtcNow.AddHours(-24), cancellationToken).ConfigureAwait(false);
            NodeMetricsDto? latest = metrics.Count == 0 ? null : metrics[^1].Metrics;
            int instanceCount = mirrorsByNode.TryGetValue(node.Id, out StoredNodeMirror? mirror) ? mirror.Instances.Count : 0;
            overview.Add(new DashboardNodeDto(
                node.Id,
                node.Name,
                node.Configuration?.PublicAddress,
                latest?.CpuPercent,
                latest?.MemoryUsedBytes,
                latest?.MemoryTotalBytes,
                instanceCount,
                node.AgentVersion,
                node.ProtocolMajor.ToString(System.Globalization.CultureInfo.InvariantCulture) + "." + node.ProtocolMinor.ToString(System.Globalization.CultureInfo.InvariantCulture),
                node.Status));
        }

        int totalInstances = mirrors.Sum(mirror => mirror.Instances.Count);
        int runningInstances = mirrors.Sum(mirror => mirror.Instances.Count(instance => string.Equals(instance.Status, "Running", StringComparison.OrdinalIgnoreCase)));
        (double cpu, long memory) = processMetrics.Sample();
        return Results.Ok(new DashboardOverviewDto(
            nodeRows.Count(node => string.Equals(node.Status, "Online", StringComparison.OrdinalIgnoreCase)),
            nodeRows.Count,
            runningInstances,
            totalInstances,
            activeSessions,
            cpu,
            memory,
            overview,
            telemetry.Snapshot(totalInstances)));
    }
}
