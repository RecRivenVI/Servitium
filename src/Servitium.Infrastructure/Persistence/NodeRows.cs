using System;

using Microsoft.EntityFrameworkCore;

namespace Servitium.Infrastructure.Persistence;

internal sealed class NodeRow
{
    internal Guid Id { get; set; }
    internal string Name { get; set; } = string.Empty;
    internal string Status { get; set; } = "Offline";
    internal string? AgentVersion { get; set; }
    internal uint ProtocolMajor { get; set; }
    internal uint ProtocolMinor { get; set; }
    internal string OperatingSystem { get; set; } = string.Empty;
    internal string Architecture { get; set; } = string.Empty;
    internal string? HostName { get; set; }
    internal bool? IsLocal { get; set; }
    internal string MachineFingerprint { get; set; } = string.Empty;
    internal DateTimeOffset CreatedAt { get; set; }
    internal DateTimeOffset? LastSeenAt { get; set; }
    internal bool CredentialRevoked { get; set; }
    internal string? ConfigurationJson { get; set; }
}

internal sealed class NodeCredentialRow
{
    internal Guid Id { get; set; }
    internal Guid NodeId { get; set; }
    internal string CredentialHash { get; set; } = string.Empty;
    internal DateTimeOffset CreatedAt { get; set; }
    internal DateTimeOffset? RevokedAt { get; set; }
    internal int Revision { get; set; }
}

internal sealed class NodeRegistrationTokenRow
{
    internal Guid Id { get; set; }
    internal string TokenHash { get; set; } = string.Empty;
    internal string? ExpectedName { get; set; }
    internal DateTimeOffset CreatedAt { get; set; }
    internal DateTimeOffset ExpiresAt { get; set; }
    internal DateTimeOffset? ConsumedAt { get; set; }
    internal bool Revoked { get; set; }
}

internal sealed class NodeMetricRow
{
    internal long Id { get; set; }
    internal Guid NodeId { get; set; }
    internal DateTimeOffset SampledAt { get; set; }
    internal double CpuPercent { get; set; }
    internal long MemoryUsedBytes { get; set; }
    internal long MemoryTotalBytes { get; set; }
    internal long DiskFreeBytes { get; set; }
    internal long? DiskTotalBytes { get; set; }
    internal long NetworkReceiveBytesPerSecond { get; set; }
    internal long NetworkSendBytesPerSecond { get; set; }
    internal long UptimeTicks { get; set; }
}

internal sealed class NodeInstanceMirrorRow
{
    internal Guid Id { get; set; }
    internal Guid NodeId { get; set; }
    internal string SnapshotJson { get; set; } = "{}";
}

internal sealed class NodeJavaRuntimeMirrorRow
{
    internal Guid Id { get; set; }
    internal Guid NodeId { get; set; }
    internal string SnapshotJson { get; set; } = "{}";
}

internal sealed class NodeScheduleMirrorRow
{
    internal Guid Id { get; set; }
    internal Guid NodeId { get; set; }
    internal string SnapshotJson { get; set; } = "{}";
}

internal sealed class NodeMirrorStateRow
{
    internal Guid NodeId { get; set; }
    internal DateTimeOffset CapturedAt { get; set; }
}

public sealed partial class ServitiumDbContext
{
    private static void ConfigureNodes(ModelBuilder modelBuilder)
    {
        _ = modelBuilder.Entity<NodeRow>(entity =>
        {
            _ = entity.ToTable("Nodes");
            _ = entity.HasKey(row => row.Id);
            _ = entity.Property(row => row.Id).HasConversion<string>();
            _ = entity.Property(row => row.Name).HasMaxLength(128);
            _ = entity.Property(row => row.Status).HasMaxLength(32);
            _ = entity.Property(row => row.AgentVersion).HasMaxLength(64);
            _ = entity.Property(row => row.ProtocolMajor);
            _ = entity.Property(row => row.ProtocolMinor);
            _ = entity.Property(row => row.OperatingSystem).HasMaxLength(256);
            _ = entity.Property(row => row.Architecture).HasMaxLength(32);
            _ = entity.Property(row => row.HostName).HasMaxLength(253);
            _ = entity.Property(row => row.IsLocal);
            _ = entity.Property(row => row.MachineFingerprint).HasMaxLength(256);
            _ = entity.Property(row => row.CreatedAt);
            _ = entity.Property(row => row.LastSeenAt);
            _ = entity.Property(row => row.CredentialRevoked);
            _ = entity.Property(row => row.ConfigurationJson);
            _ = entity.HasIndex(row => row.Name).IsUnique();
            _ = entity.HasIndex(row => row.MachineFingerprint).IsUnique();
        });
        _ = modelBuilder.Entity<NodeCredentialRow>(entity =>
        {
            _ = entity.ToTable("NodeCredentials");
            _ = entity.HasKey(row => row.Id);
            _ = entity.Property(row => row.Id).HasConversion<string>();
            _ = entity.Property(row => row.NodeId).HasConversion<string>();
            _ = entity.Property(row => row.CredentialHash).HasMaxLength(64);
            _ = entity.Property(row => row.CreatedAt);
            _ = entity.Property(row => row.RevokedAt);
            _ = entity.Property(row => row.Revision);
            _ = entity.HasIndex(row => row.CredentialHash).IsUnique();
            _ = entity.HasIndex(row => new { row.NodeId, row.Revision }).IsUnique();
            _ = entity.HasOne<NodeRow>().WithMany().HasForeignKey(row => row.NodeId).OnDelete(DeleteBehavior.Cascade);
        });
        _ = modelBuilder.Entity<NodeRegistrationTokenRow>(entity =>
        {
            _ = entity.ToTable("NodeRegistrationTokens");
            _ = entity.HasKey(row => row.Id);
            _ = entity.Property(row => row.Id).HasConversion<string>();
            _ = entity.Property(row => row.TokenHash).HasMaxLength(64);
            _ = entity.Property(row => row.ExpectedName).HasMaxLength(128);
            _ = entity.Property(row => row.CreatedAt);
            _ = entity.Property(row => row.ExpiresAt);
            _ = entity.Property(row => row.ConsumedAt);
            _ = entity.Property(row => row.Revoked);
            _ = entity.HasIndex(row => row.TokenHash).IsUnique();
            _ = entity.HasIndex(row => row.ExpiresAt);
        });
        _ = modelBuilder.Entity<NodeMetricRow>(entity =>
        {
            _ = entity.ToTable("NodeMetrics");
            _ = entity.HasKey(row => row.Id);
            _ = entity.Property(row => row.NodeId).HasConversion<string>();
            _ = entity.Property(row => row.SampledAt);
            _ = entity.Property(row => row.CpuPercent);
            _ = entity.Property(row => row.MemoryUsedBytes);
            _ = entity.Property(row => row.MemoryTotalBytes);
            _ = entity.Property(row => row.DiskFreeBytes);
            _ = entity.Property(row => row.DiskTotalBytes);
            _ = entity.Property(row => row.NetworkReceiveBytesPerSecond);
            _ = entity.Property(row => row.NetworkSendBytesPerSecond);
            _ = entity.Property(row => row.UptimeTicks);
            _ = entity.HasIndex(row => new { row.NodeId, row.SampledAt });
            _ = entity.HasOne<NodeRow>().WithMany().HasForeignKey(row => row.NodeId).OnDelete(DeleteBehavior.Cascade);
        });
        ConfigureMirror<NodeInstanceMirrorRow>(modelBuilder, "NodeInstanceMirrors");
        ConfigureMirror<NodeJavaRuntimeMirrorRow>(modelBuilder, "NodeJavaRuntimeMirrors");
        ConfigureMirror<NodeScheduleMirrorRow>(modelBuilder, "NodeScheduleMirrors");
        _ = modelBuilder.Entity<NodeMirrorStateRow>(entity =>
        {
            _ = entity.ToTable("NodeMirrorStates");
            _ = entity.HasKey(row => row.NodeId);
            _ = entity.Property(row => row.NodeId).HasConversion<string>();
            _ = entity.Property(row => row.CapturedAt);
            _ = entity.HasOne<NodeRow>().WithOne().HasForeignKey<NodeMirrorStateRow>(row => row.NodeId).OnDelete(DeleteBehavior.Cascade);
        });
    }

    private static void ConfigureMirror<TRow>(ModelBuilder modelBuilder, string table)
        where TRow : class
    {
        _ = modelBuilder.Entity<TRow>(entity =>
        {
            _ = entity.ToTable(table);
            _ = entity.HasKey("Id");
            _ = entity.Property<Guid>("Id").HasConversion<string>();
            _ = entity.Property<Guid>("NodeId").HasConversion<string>();
            _ = entity.Property<string>("SnapshotJson");
            _ = entity.HasIndex("NodeId");
            _ = entity.HasOne<NodeRow>().WithMany().HasForeignKey("NodeId").OnDelete(DeleteBehavior.Cascade);
        });
    }
}
