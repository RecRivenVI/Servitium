using System;

using Microsoft.EntityFrameworkCore;

namespace Servitium.Infrastructure.Persistence;

internal sealed class InstanceMetricRow
{
    internal Guid NodeId { get; set; }
    internal Guid InstanceId { get; set; }
    internal long Minute { get; set; }
    internal long SampledAtUnixMilliseconds { get; set; }
    internal double CpuPercent { get; set; }
    internal long MemoryBytes { get; set; }
    internal int? OnlinePlayers { get; set; }
}

public sealed partial class ServitiumDbContext
{
    internal DbSet<InstanceMetricRow> InstanceMetrics => Set<InstanceMetricRow>();

    private static void ConfigureInstanceMetrics(ModelBuilder modelBuilder)
    {
        _ = modelBuilder.Entity<InstanceMetricRow>(entity =>
        {
            _ = entity.ToTable("InstanceMetrics");
            _ = entity.HasKey(row => new { row.NodeId, row.InstanceId, row.Minute });
            _ = entity.Property(row => row.NodeId).HasConversion<string>();
            _ = entity.Property(row => row.InstanceId).HasConversion<string>();
            _ = entity.Property(row => row.SampledAtUnixMilliseconds);
            _ = entity.Property(row => row.CpuPercent);
            _ = entity.Property(row => row.MemoryBytes);
            _ = entity.Property(row => row.OnlinePlayers);
            _ = entity.HasIndex(row => row.SampledAtUnixMilliseconds);
            _ = entity.HasOne<NodeRow>().WithMany().HasForeignKey(row => row.NodeId).OnDelete(DeleteBehavior.Cascade);
        });
    }
}
