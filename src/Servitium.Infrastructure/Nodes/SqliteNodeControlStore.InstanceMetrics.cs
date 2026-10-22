using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.EntityFrameworkCore;

using Servitium.Application.Nodes;
using Servitium.Contracts.Instances;
using Servitium.Infrastructure.Persistence;

namespace Servitium.Infrastructure.Nodes;

internal sealed partial class SqliteNodeControlStore
{
    public Task AddInstanceMetricAsync(StoredInstanceMetric metric, DateTimeOffset retentionCutoff, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(metric);
        return writeCoordinator.ExecuteAsync(async () =>
        {
            using ServitiumDbContext context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            long cutoff = retentionCutoff.ToUnixTimeMilliseconds();
            _ = await context.InstanceMetrics.Where(row => row.NodeId == metric.NodeId && row.SampledAtUnixMilliseconds < cutoff)
                .ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
            long timestamp = metric.Sample.SampledAt.ToUnixTimeMilliseconds();
            if (timestamp < cutoff) { return; }
            long minute = timestamp / 60_000;
            InstanceMetricRow? row = await context.InstanceMetrics.SingleOrDefaultAsync(value => value.NodeId == metric.NodeId && value.InstanceId == metric.InstanceId && value.Minute == minute, cancellationToken).ConfigureAwait(false);
            if (row is null)
            {
                row = new() { NodeId = metric.NodeId, InstanceId = metric.InstanceId, Minute = minute };
                _ = context.InstanceMetrics.Add(row);
            }
            else if (row.SampledAtUnixMilliseconds > timestamp) { return; }
            row.SampledAtUnixMilliseconds = timestamp;
            row.CpuPercent = metric.Sample.CpuPercent;
            row.MemoryBytes = metric.Sample.MemoryBytes;
            row.OnlinePlayers = metric.Sample.OnlinePlayers;
            _ = await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }, cancellationToken);
    }

    public async Task<IReadOnlyList<InstanceMetricSampleDto>> ListInstanceMetricsAsync(Guid nodeId, Guid instanceId, DateTimeOffset since, CancellationToken cancellationToken)
    {
        using ServitiumDbContext context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        long start = since.ToUnixTimeMilliseconds();
        InstanceMetricRow[] rows = await context.InstanceMetrics.AsNoTracking()
            .Where(row => row.NodeId == nodeId && row.InstanceId == instanceId && row.SampledAtUnixMilliseconds >= start)
            .OrderBy(row => row.Minute).ToArrayAsync(cancellationToken).ConfigureAwait(false);
        return [.. rows.Select(row => new InstanceMetricSampleDto(DateTimeOffset.FromUnixTimeMilliseconds(row.SampledAtUnixMilliseconds), row.CpuPercent, row.MemoryBytes, row.OnlinePlayers))];
    }
}
