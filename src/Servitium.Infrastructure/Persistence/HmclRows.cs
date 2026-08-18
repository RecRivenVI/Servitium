using System;

using Microsoft.EntityFrameworkCore;

namespace Servitium.Infrastructure.Persistence;

internal sealed class HmclChannelRow
{
    internal Guid Id { get; set; }
    internal string Slug { get; set; } = string.Empty;
    internal string Name { get; set; } = string.Empty;
    internal string Description { get; set; } = string.Empty;
    internal string Visibility { get; set; } = string.Empty;
    internal string? TokenHash { get; set; }
    internal Guid? ActiveReleaseId { get; set; }
    internal int RetentionCount { get; set; }
    internal Guid? LinkedInstanceId { get; set; }
    internal string IntegrityStatus { get; set; } = "unknown";
    internal DateTimeOffset? IntegrityCheckedAt { get; set; }
    internal DateTimeOffset CreatedAt { get; set; }
}

internal sealed class HmclReleaseRow
{
    internal Guid Id { get; set; }
    internal Guid ChannelId { get; set; }
    internal long Sequence { get; set; }
    internal string Name { get; set; } = string.Empty;
    internal string Author { get; set; } = string.Empty;
    internal string Version { get; set; } = string.Empty;
    internal string Description { get; set; } = string.Empty;
    internal string GameVersion { get; set; } = string.Empty;
    internal string? LoaderId { get; set; }
    internal string? LoaderVersion { get; set; }
    internal string? OptiFineVersion { get; set; }
    internal int FileCount { get; set; }
    internal long TotalSize { get; set; }
    internal byte[] Manifest { get; set; } = [];
    internal string ManifestSha1 { get; set; } = string.Empty;
    internal DateTimeOffset CreatedAt { get; set; }
    internal Guid CreatedBy { get; set; }
    internal DateTimeOffset? PublishedAt { get; set; }
}

internal sealed class HmclReleaseFileRow
{
    internal Guid ReleaseId { get; set; }
    internal string Path { get; set; } = string.Empty;
    internal string ContentSha1 { get; set; } = string.Empty;
    internal long Size { get; set; }
}

internal sealed class HmclReleaseActivationRow
{
    internal Guid Id { get; set; }
    internal Guid ChannelId { get; set; }
    internal Guid ReleaseId { get; set; }
    internal DateTimeOffset StartedAt { get; set; }
    internal DateTimeOffset? EndedAt { get; set; }
}

internal sealed class HmclContentObjectRow
{
    internal string Sha1 { get; set; } = string.Empty;
    internal long Size { get; set; }
    internal long ReferenceCount { get; set; }
    internal DateTimeOffset CreatedAt { get; set; }
}

internal sealed class HmclArchiveRow
{
    internal Guid Id { get; set; }
    internal string FileName { get; set; } = string.Empty;
    internal long TotalBytes { get; set; }
    internal int PartSizeBytes { get; set; }
    internal int PartCount { get; set; }
    internal string ReceivedPartsJson { get; set; } = "[]";
    internal string? Sha256 { get; set; }
    internal string Status { get; set; } = "uploading";
    internal Guid CreatedBy { get; set; }
    internal DateTimeOffset CreatedAt { get; set; }
    internal DateTimeOffset ExpiresAt { get; set; }
}

internal sealed class HmclAccessStatRow
{
    internal Guid ChannelId { get; set; }
    internal Guid ReleaseId { get; set; }
    internal DateOnly Date { get; set; }
    internal long Manifest200 { get; set; }
    internal long Manifest304 { get; set; }
    internal long File200 { get; set; }
    internal long File206 { get; set; }
    internal long Errors { get; set; }
    internal long Bytes { get; set; }
}

internal sealed class HmclAccessSourceRow
{
    internal Guid ChannelId { get; set; }
    internal Guid ReleaseId { get; set; }
    internal DateOnly Date { get; set; }
    internal string PrefixHash { get; set; } = string.Empty;
}

public sealed partial class ServitiumDbContext
{
    internal DbSet<HmclChannelRow> HmclChannels => Set<HmclChannelRow>();
    internal DbSet<HmclReleaseRow> HmclReleases => Set<HmclReleaseRow>();
    internal DbSet<HmclReleaseFileRow> HmclReleaseFiles => Set<HmclReleaseFileRow>();
    internal DbSet<HmclReleaseActivationRow> HmclReleaseActivations => Set<HmclReleaseActivationRow>();
    internal DbSet<HmclContentObjectRow> HmclContentObjects => Set<HmclContentObjectRow>();
    internal DbSet<HmclArchiveRow> HmclArchives => Set<HmclArchiveRow>();
    internal DbSet<HmclAccessStatRow> HmclAccessStats => Set<HmclAccessStatRow>();
    internal DbSet<HmclAccessSourceRow> HmclAccessSources => Set<HmclAccessSourceRow>();

    private static void ConfigureHmcl(ModelBuilder modelBuilder)
    {
        _ = modelBuilder.Entity<HmclChannelRow>(entity =>
        {
            _ = entity.ToTable("HmclChannels");
            _ = entity.HasKey(row => row.Id);
            _ = entity.Property(row => row.Id).HasConversion<string>();
            _ = entity.Property(row => row.Slug).HasMaxLength(48);
            _ = entity.Property(row => row.Name).HasMaxLength(64);
            _ = entity.Property(row => row.Description).HasMaxLength(1000);
            _ = entity.Property(row => row.Visibility).HasMaxLength(32);
            _ = entity.Property(row => row.TokenHash).HasMaxLength(64);
            _ = entity.Property(row => row.ActiveReleaseId).HasConversion<string>();
            _ = entity.Property(row => row.RetentionCount);
            _ = entity.Property(row => row.LinkedInstanceId).HasConversion<string>();
            _ = entity.Property(row => row.IntegrityStatus).HasMaxLength(32).HasDefaultValue("unknown");
            _ = entity.Property(row => row.IntegrityCheckedAt);
            _ = entity.Property(row => row.CreatedAt);
            _ = entity.HasIndex(row => row.Slug).IsUnique();
        });
        _ = modelBuilder.Entity<HmclReleaseRow>(entity =>
        {
            _ = entity.ToTable("HmclReleases");
            _ = entity.HasKey(row => row.Id);
            _ = entity.Property(row => row.Id).HasConversion<string>();
            _ = entity.Property(row => row.ChannelId).HasConversion<string>();
            _ = entity.Property(row => row.Sequence);
            _ = entity.Property(row => row.Name).HasMaxLength(128);
            _ = entity.Property(row => row.Author).HasMaxLength(128);
            _ = entity.Property(row => row.Version).HasMaxLength(64);
            _ = entity.Property(row => row.Description).HasMaxLength(4000);
            _ = entity.Property(row => row.GameVersion).HasMaxLength(64);
            _ = entity.Property(row => row.LoaderId).HasMaxLength(32);
            _ = entity.Property(row => row.LoaderVersion).HasMaxLength(64);
            _ = entity.Property(row => row.OptiFineVersion).HasMaxLength(64);
            _ = entity.Property(row => row.FileCount);
            _ = entity.Property(row => row.TotalSize);
            _ = entity.Property(row => row.Manifest).HasMaxLength(16 * 1024 * 1024);
            _ = entity.Property(row => row.ManifestSha1).HasMaxLength(40);
            _ = entity.Property(row => row.CreatedAt);
            _ = entity.Property(row => row.CreatedBy).HasConversion<string>();
            _ = entity.Property(row => row.PublishedAt);
            _ = entity.HasIndex(row => new { row.ChannelId, row.Sequence }).IsUnique();
            _ = entity.HasOne<HmclChannelRow>().WithMany().HasForeignKey(row => row.ChannelId).OnDelete(DeleteBehavior.Restrict);
        });
        _ = modelBuilder.Entity<HmclReleaseFileRow>(entity =>
        {
            _ = entity.ToTable("HmclReleaseFiles");
            _ = entity.HasKey(row => new { row.ReleaseId, row.Path });
            _ = entity.Property(row => row.ReleaseId).HasConversion<string>();
            _ = entity.Property(row => row.Path).HasMaxLength(240);
            _ = entity.Property(row => row.ContentSha1).HasMaxLength(40);
            _ = entity.Property(row => row.Size);
            _ = entity.HasIndex(row => row.ContentSha1);
            _ = entity.HasOne<HmclReleaseRow>().WithMany().HasForeignKey(row => row.ReleaseId).OnDelete(DeleteBehavior.Cascade);
        });
        _ = modelBuilder.Entity<HmclReleaseActivationRow>(entity =>
        {
            _ = entity.ToTable("HmclReleaseActivations");
            _ = entity.HasKey(row => row.Id);
            _ = entity.Property(row => row.Id).HasConversion<string>();
            _ = entity.Property(row => row.ChannelId).HasConversion<string>();
            _ = entity.Property(row => row.ReleaseId).HasConversion<string>();
            _ = entity.Property(row => row.StartedAt);
            _ = entity.Property(row => row.EndedAt);
            _ = entity.HasIndex(row => new { row.ChannelId, row.StartedAt });
            _ = entity.HasIndex(row => row.ReleaseId);
            _ = entity.HasIndex(row => row.ChannelId).HasFilter("\"EndedAt\" IS NULL").IsUnique();
            _ = entity.HasOne<HmclChannelRow>().WithMany().HasForeignKey(row => row.ChannelId).OnDelete(DeleteBehavior.Cascade);
            _ = entity.HasOne<HmclReleaseRow>().WithMany().HasForeignKey(row => row.ReleaseId).OnDelete(DeleteBehavior.Cascade);
        });
        _ = modelBuilder.Entity<HmclContentObjectRow>(entity =>
        {
            _ = entity.ToTable("HmclContentObjects");
            _ = entity.HasKey(row => row.Sha1);
            _ = entity.Property(row => row.Sha1).HasMaxLength(40);
            _ = entity.Property(row => row.Size);
            _ = entity.Property(row => row.ReferenceCount);
            _ = entity.Property(row => row.CreatedAt);
        });
        _ = modelBuilder.Entity<HmclArchiveRow>(entity =>
        {
            _ = entity.ToTable("HmclArchives");
            _ = entity.HasKey(row => row.Id);
            _ = entity.Property(row => row.Id).HasConversion<string>();
            _ = entity.Property(row => row.FileName).HasMaxLength(255);
            _ = entity.Property(row => row.TotalBytes);
            _ = entity.Property(row => row.PartSizeBytes);
            _ = entity.Property(row => row.PartCount);
            _ = entity.Property(row => row.ReceivedPartsJson).HasMaxLength(100_000);
            _ = entity.Property(row => row.Sha256).HasMaxLength(64);
            _ = entity.Property(row => row.Status).HasMaxLength(32);
            _ = entity.Property(row => row.CreatedBy).HasConversion<string>();
            _ = entity.Property(row => row.CreatedAt);
            _ = entity.Property(row => row.ExpiresAt);
            _ = entity.HasIndex(row => row.ExpiresAt);
        });
        _ = modelBuilder.Entity<HmclAccessStatRow>(entity =>
        {
            _ = entity.ToTable("HmclAccessStats");
            _ = entity.HasKey(row => new { row.ChannelId, row.ReleaseId, row.Date });
            _ = entity.Property(row => row.ChannelId).HasConversion<string>();
            _ = entity.Property(row => row.ReleaseId).HasConversion<string>();
            _ = entity.Property(row => row.Date).HasConversion<string>();
            _ = entity.Property(row => row.Manifest200);
            _ = entity.Property(row => row.Manifest304);
            _ = entity.Property(row => row.File200);
            _ = entity.Property(row => row.File206);
            _ = entity.Property(row => row.Errors);
            _ = entity.Property(row => row.Bytes);
            _ = entity.HasIndex(row => row.Date);
        });
        _ = modelBuilder.Entity<HmclAccessSourceRow>(entity =>
        {
            _ = entity.ToTable("HmclAccessSources");
            _ = entity.HasKey(row => new { row.ChannelId, row.ReleaseId, row.Date, row.PrefixHash });
            _ = entity.Property(row => row.ChannelId).HasConversion<string>();
            _ = entity.Property(row => row.ReleaseId).HasConversion<string>();
            _ = entity.Property(row => row.Date).HasConversion<string>();
            _ = entity.Property(row => row.PrefixHash).HasMaxLength(64);
            _ = entity.HasIndex(row => row.Date);
        });
    }
}
