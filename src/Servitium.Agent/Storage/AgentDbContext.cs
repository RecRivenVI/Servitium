using System;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.EntityFrameworkCore;

namespace Servitium.Agent.Storage;

internal sealed class LocalInstanceRow
{
    internal Guid Id { get; set; }
    internal string Name { get; set; } = string.Empty;
    internal string WorkingDirectory { get; set; } = string.Empty;
    internal bool OwnedByServitium { get; set; }
    internal string? OwnedBaseDirectory { get; set; }
    internal string Status { get; set; } = "Stopped";
    internal int? ProcessId { get; set; }
    internal DateTimeOffset? StartedAt { get; set; }
    internal int? LastExitCode { get; set; }
    internal string LaunchJson { get; set; } = "{}";
    internal string RuntimeSettingsJson { get; set; } = "{}";
    internal string TagsJson { get; set; } = "[]";
    internal string? Notes { get; set; }
    internal int? PrimaryPort { get; set; }
    internal DateTimeOffset CreatedAt { get; set; }
    internal DateTimeOffset UpdatedAt { get; set; }
    internal long Revision { get; set; }
}

internal sealed class LocalJavaRuntimeRow
{
    internal Guid Id { get; set; }
    internal string JavaExecutablePath { get; set; } = string.Empty;
    internal string Version { get; set; } = string.Empty;
    internal string Vendor { get; set; } = string.Empty;
    internal string Architecture { get; set; } = string.Empty;
    internal string VmName { get; set; } = string.Empty;
    internal string Source { get; set; } = string.Empty;
    internal bool Imported { get; set; }
    internal string? ImportedRoot { get; set; }
    internal DateTimeOffset RegisteredAt { get; set; }
}

internal sealed class LocalOutboxEventRow
{
    internal long Sequence { get; set; }
    internal string Type { get; set; } = string.Empty;
    internal string Scope { get; set; } = string.Empty;
    internal string PayloadJson { get; set; } = "{}";
    internal bool Critical { get; set; }
    internal DateTimeOffset OccurredAt { get; set; }
}

internal sealed class LocalScheduledTaskRow
{
    internal Guid Id { get; set; }
    internal Guid InstanceId { get; set; }
    internal string Name { get; set; } = string.Empty;
    internal string DefinitionJson { get; set; } = "{}";
    internal bool Enabled { get; set; }
    internal int RemainingRuns { get; set; }
    internal string OverlapPolicy { get; set; } = "skip";
    internal DateTimeOffset? NextRunAt { get; set; }
    internal DateTimeOffset CreatedAt { get; set; }
    internal DateTimeOffset UpdatedAt { get; set; }
    internal long Revision { get; set; }
}

internal sealed class LocalScheduledTaskRunRow
{
    internal long Sequence { get; set; }
    internal Guid Id { get; set; }
    internal Guid TaskId { get; set; }
    internal DateTimeOffset StartedAt { get; set; }
    internal DateTimeOffset? CompletedAt { get; set; }
    internal bool Succeeded { get; set; }
    internal string OutcomeCode { get; set; } = string.Empty;
    internal string ActionResultsJson { get; set; } = "[]";
}

internal sealed class LocalFileTaskRow
{
    internal Guid Id { get; set; }
    internal Guid InstanceId { get; set; }
    internal string Kind { get; set; } = string.Empty;
    internal string Status { get; set; } = "Pending";
    internal string TargetPath { get; set; } = string.Empty;
    internal long BytesCompleted { get; set; }
    internal long? TotalBytes { get; set; }
    internal long CreatedAtUnixMilliseconds { get; set; }
    internal long UpdatedAtUnixMilliseconds { get; set; }
    internal string? ErrorCode { get; set; }
    internal bool CancellationRequested { get; set; }
}

internal sealed class LocalUploadPartRow
{
    internal Guid UploadId { get; set; }
    internal int PartIndex { get; set; }
    internal Guid InstanceId { get; set; }
    internal string StoragePath { get; set; } = string.Empty;
    internal long Size { get; set; }
    internal string Sha256 { get; set; } = string.Empty;
    internal long ExpiresAtUnixMilliseconds { get; set; }
}

internal sealed class LocalConfigBackupRow
{
    internal Guid Id { get; set; }
    internal Guid InstanceId { get; set; }
    internal string RelativePath { get; set; } = string.Empty;
    internal string StoragePath { get; set; } = string.Empty;
    internal long CreatedAtUnixMilliseconds { get; set; }
    internal long Size { get; set; }
    internal string Sha256 { get; set; } = string.Empty;
}

internal sealed class AgentDbContext(DbContextOptions<AgentDbContext> options) : DbContext(options)
{
    internal DbSet<LocalInstanceRow> Instances => Set<LocalInstanceRow>();
    internal DbSet<LocalJavaRuntimeRow> JavaRuntimes => Set<LocalJavaRuntimeRow>();
    internal DbSet<LocalOutboxEventRow> OutboxEvents => Set<LocalOutboxEventRow>();
    internal DbSet<LocalScheduledTaskRow> ScheduledTasks => Set<LocalScheduledTaskRow>();
    internal DbSet<LocalScheduledTaskRunRow> ScheduledTaskRuns => Set<LocalScheduledTaskRunRow>();
    internal DbSet<LocalFileTaskRow> FileTasks => Set<LocalFileTaskRow>();
    internal DbSet<LocalUploadPartRow> UploadParts => Set<LocalUploadPartRow>();
    internal DbSet<LocalConfigBackupRow> ConfigBackups => Set<LocalConfigBackupRow>();

    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        _ = modelBuilder.Entity<LocalInstanceRow>(entity =>
        {
            _ = entity.ToTable("LocalInstances");
            _ = entity.HasKey(row => row.Id);
            _ = entity.Property(row => row.Id).HasConversion<string>();
            _ = entity.Property(row => row.Name).HasMaxLength(64);
            _ = entity.Property(row => row.WorkingDirectory).HasMaxLength(1024);
            _ = entity.Property(row => row.OwnedByServitium);
            _ = entity.Property(row => row.OwnedBaseDirectory).HasMaxLength(1024);
            _ = entity.Property(row => row.Status).HasMaxLength(32);
            _ = entity.Property(row => row.ProcessId);
            _ = entity.Property(row => row.StartedAt);
            _ = entity.Property(row => row.LastExitCode);
            _ = entity.Property(row => row.LaunchJson);
            _ = entity.Property(row => row.RuntimeSettingsJson);
            _ = entity.Property(row => row.TagsJson);
            _ = entity.Property(row => row.Notes).HasMaxLength(4096);
            _ = entity.Property(row => row.PrimaryPort);
            _ = entity.Property(row => row.CreatedAt);
            _ = entity.Property(row => row.UpdatedAt);
            _ = entity.Property(row => row.Revision).IsConcurrencyToken();
            _ = entity.HasIndex(row => row.Name).IsUnique();
            _ = entity.HasIndex(row => row.WorkingDirectory).IsUnique();
        });
        _ = modelBuilder.Entity<LocalJavaRuntimeRow>(entity =>
        {
            _ = entity.ToTable("LocalJavaRuntimes");
            _ = entity.HasKey(row => row.Id);
            _ = entity.Property(row => row.Id).HasConversion<string>();
            _ = entity.Property(row => row.JavaExecutablePath).HasMaxLength(1024);
            _ = entity.Property(row => row.Version).HasMaxLength(64);
            _ = entity.Property(row => row.Vendor).HasMaxLength(256);
            _ = entity.Property(row => row.Architecture).HasMaxLength(32);
            _ = entity.Property(row => row.VmName).HasMaxLength(256);
            _ = entity.Property(row => row.Source).HasMaxLength(32);
            _ = entity.Property(row => row.Imported);
            _ = entity.Property(row => row.ImportedRoot).HasMaxLength(1024);
            _ = entity.Property(row => row.RegisteredAt);
            _ = entity.HasIndex(row => row.JavaExecutablePath).IsUnique();
        });
        _ = modelBuilder.Entity<LocalOutboxEventRow>(entity =>
        {
            _ = entity.ToTable("LocalOutboxEvents");
            _ = entity.HasKey(row => row.Sequence);
            _ = entity.Property(row => row.Sequence).ValueGeneratedOnAdd();
            _ = entity.Property(row => row.Type).HasMaxLength(128);
            _ = entity.Property(row => row.Scope).HasMaxLength(128);
            _ = entity.Property(row => row.PayloadJson);
            _ = entity.Property(row => row.Critical);
            _ = entity.Property(row => row.OccurredAt);
            _ = entity.HasIndex(row => row.OccurredAt);
        });
        _ = modelBuilder.Entity<LocalScheduledTaskRow>(entity =>
        {
            _ = entity.ToTable("LocalScheduledTasks");
            _ = entity.HasKey(row => row.Id);
            _ = entity.Property(row => row.Id).HasConversion<string>();
            _ = entity.Property(row => row.InstanceId).HasConversion<string>();
            _ = entity.Property(row => row.Name).HasMaxLength(64);
            _ = entity.Property(row => row.DefinitionJson);
            _ = entity.Property(row => row.Enabled);
            _ = entity.Property(row => row.RemainingRuns);
            _ = entity.Property(row => row.OverlapPolicy).HasMaxLength(16);
            _ = entity.Property(row => row.NextRunAt);
            _ = entity.Property(row => row.CreatedAt);
            _ = entity.Property(row => row.UpdatedAt);
            _ = entity.Property(row => row.Revision).IsConcurrencyToken();
            _ = entity.HasIndex(row => new { row.InstanceId, row.Name }).IsUnique();
            _ = entity.HasIndex(row => row.NextRunAt);
        });
        _ = modelBuilder.Entity<LocalScheduledTaskRunRow>(entity =>
        {
            _ = entity.ToTable("LocalScheduledTaskRuns");
            _ = entity.HasKey(row => row.Sequence);
            _ = entity.Property(row => row.Sequence).ValueGeneratedOnAdd();
            _ = entity.Property(row => row.Id).HasConversion<string>();
            _ = entity.Property(row => row.TaskId).HasConversion<string>();
            _ = entity.Property(row => row.StartedAt);
            _ = entity.Property(row => row.CompletedAt);
            _ = entity.Property(row => row.Succeeded);
            _ = entity.Property(row => row.OutcomeCode).HasMaxLength(64);
            _ = entity.Property(row => row.ActionResultsJson);
            _ = entity.HasIndex(row => new { row.TaskId, row.Sequence });
        });
        _ = modelBuilder.Entity<LocalFileTaskRow>(entity =>
        {
            _ = entity.ToTable("LocalFileTasks");
            _ = entity.HasKey(row => row.Id);
            _ = entity.Property(row => row.Id).HasConversion<string>();
            _ = entity.Property(row => row.InstanceId).HasConversion<string>();
            _ = entity.Property(row => row.Kind).HasMaxLength(32);
            _ = entity.Property(row => row.Status).HasMaxLength(32);
            _ = entity.Property(row => row.TargetPath).HasMaxLength(1024);
            _ = entity.Property(row => row.BytesCompleted);
            _ = entity.Property(row => row.TotalBytes);
            _ = entity.Property(row => row.CreatedAtUnixMilliseconds);
            _ = entity.Property(row => row.UpdatedAtUnixMilliseconds);
            _ = entity.Property(row => row.ErrorCode).HasMaxLength(64);
            _ = entity.Property(row => row.CancellationRequested);
            _ = entity.HasIndex(row => new { row.InstanceId, row.Status });
            _ = entity.HasIndex(row => row.UpdatedAtUnixMilliseconds);
        });
        _ = modelBuilder.Entity<LocalUploadPartRow>(entity =>
        {
            _ = entity.ToTable("LocalUploadParts");
            _ = entity.HasKey(row => new { row.UploadId, row.PartIndex });
            _ = entity.Property(row => row.UploadId).HasConversion<string>();
            _ = entity.Property(row => row.InstanceId).HasConversion<string>();
            _ = entity.Property(row => row.StoragePath).HasMaxLength(1024);
            _ = entity.Property(row => row.Size);
            _ = entity.Property(row => row.Sha256).HasMaxLength(64);
            _ = entity.HasIndex(row => row.ExpiresAtUnixMilliseconds);
        });
        _ = modelBuilder.Entity<LocalConfigBackupRow>(entity =>
        {
            _ = entity.ToTable("LocalConfigBackups");
            _ = entity.HasKey(row => row.Id);
            _ = entity.Property(row => row.Id).HasConversion<string>();
            _ = entity.Property(row => row.InstanceId).HasConversion<string>();
            _ = entity.Property(row => row.RelativePath).HasMaxLength(1024);
            _ = entity.Property(row => row.StoragePath).HasMaxLength(1024);
            _ = entity.Property(row => row.CreatedAtUnixMilliseconds);
            _ = entity.Property(row => row.Size);
            _ = entity.Property(row => row.Sha256).HasMaxLength(64);
            _ = entity.HasIndex(row => new { row.InstanceId, row.RelativePath, row.CreatedAtUnixMilliseconds });
        });
    }
}
