using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.AspNetCore.Http;

using Servitium.Application.Abstractions;
using Servitium.Application.Nodes;
using Servitium.Application.Results;
using Servitium.Contracts;
using Servitium.Contracts.Instances;
using Servitium.Contracts.Transport;
using Servitium.Server.OpenApi;
using Servitium.Server.Security;

namespace Servitium.Server.Endpoints;

internal static partial class InstanceEndpoints
{
    private static async Task<IResult> ListMetricHistoryAsync(HttpContext context, Guid instanceId, DateTimeOffset? since,
        string? cursor, int? limit, INodeControlStore nodes, IClock clock, CancellationToken cancellationToken)
    {
        var instance = await FindAsync(instanceId, nodes, cancellationToken).ConfigureAwait(false);
        if (instance is null) { return ProblemResponses.Create(context, StatusCodes.Status404NotFound, ErrorCodes.NotFound); }
        DateTimeOffset earliest = clock.UtcNow.AddHours(-24);
        DateTimeOffset requested = since is null || since < earliest ? earliest : since.Value;
        IReadOnlyList<InstanceMetricSampleDto> samples = await nodes.ListInstanceMetricsAsync(instance.Value.NodeId, instanceId, requested, cancellationToken).ConfigureAwait(false);
        return Results.Ok(CursorPaginator.Page(samples,
            value => value.SampledAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
            value => FormattableString.Invariant($"{value.CpuPercent:R}:{value.MemoryBytes}:{value.OnlinePlayers}"),
            cursor, limit is null or 0 ? 200 : limit.Value));
    }
}
