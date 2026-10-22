using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;

namespace Servitium.Infrastructure.Persistence;

/// <summary>EF Core 用户行。</summary>
public sealed class UserRow
{
    internal Guid Id { get; set; }
    internal string UserName { get; set; } = string.Empty;
    internal string DisplayName { get; set; } = string.Empty;
    internal int Role { get; set; }
    internal int PreviousRole { get; set; } = 2;
    internal string? OwnerMarker { get; set; }
    internal string PasswordHash { get; set; } = string.Empty;
    internal string PreferredLanguage { get; set; } = "zh-Hans";
    internal DateTimeOffset CreatedAt { get; set; }
    internal DateTimeOffset? LastSignedInAt { get; set; }
    internal bool IsDeleted { get; set; }
    internal byte[]? ProtectedTotpSecret { get; set; }
    internal bool TotpConfirmed { get; set; }
    internal long SecurityStamp { get; set; }
}

/// <summary>EF Core 会话行。</summary>
public sealed class SessionRow
{
    internal Guid Id { get; set; }
    internal Guid UserId { get; set; }
    internal string TokenHash { get; set; } = string.Empty;
    internal DateTimeOffset CreatedAt { get; set; }
    internal DateTimeOffset LastSeenAt { get; set; }
    internal DateTimeOffset ExpiresAt { get; set; }
    internal DateTimeOffset AbsoluteExpiresAt { get; set; }
    internal long SecurityStamp { get; set; }
    internal bool IsRestricted { get; set; }
}

/// <summary>EF Core API Key 行。</summary>
public sealed class ApiKeyRow
{
    internal Guid Id { get; set; }
    internal Guid UserId { get; set; }
    internal string Name { get; set; } = string.Empty;
    internal string KeyHash { get; set; } = string.Empty;
    internal string Scope { get; set; } = string.Empty;
    internal DateTimeOffset CreatedAt { get; set; }
    internal DateTimeOffset? ExpiresAt { get; set; }
    internal DateTimeOffset? LastUsedAt { get; set; }
    internal bool IsRevoked { get; set; }
}

/// <summary>EF Core 实例授权行。</summary>
public sealed class InstanceGrantRow
{
    internal Guid UserId { get; set; }
    internal Guid InstanceId { get; set; }
}

/// <summary>TOTP 恢复码哈希行。</summary>
public sealed class RecoveryCodeRow
{
    internal Guid Id { get; set; }
    internal Guid UserId { get; set; }
    internal string CodeHash { get; set; } = string.Empty;
    internal DateTimeOffset CreatedAt { get; set; }
}

/// <summary>外部 OIDC 身份行。</summary>
public sealed class ExternalIdentityRow
{
    internal Guid Id { get; set; }
    internal Guid UserId { get; set; }
    internal string Issuer { get; set; } = string.Empty;
    internal string Subject { get; set; } = string.Empty;
    internal DateTimeOffset CreatedAt { get; set; }
}

/// <summary>Windows SID 绑定行。</summary>
public sealed class WindowsIdentityRow
{
    internal Guid Id { get; set; }
    internal Guid UserId { get; set; }
    internal string Sid { get; set; } = string.Empty;
    internal DateTimeOffset CreatedAt { get; set; }
}

/// <summary>OIDC 授权一次性状态行。</summary>
public sealed class OidcStateRow
{
    internal Guid Id { get; set; }
    internal string StateHash { get; set; } = string.Empty;
    internal string Nonce { get; set; } = string.Empty;
    internal string CodeVerifier { get; set; } = string.Empty;
    internal string? ReturnUrl { get; set; }
    internal Guid? BindingUserId { get; set; }
    internal DateTimeOffset ExpiresAt { get; set; }
    internal bool Consumed { get; set; }
}

/// <summary>本机一次性换票行。</summary>
public sealed class LocalSessionTicketRow
{
    internal Guid Id { get; set; }
    internal Guid UserId { get; set; }
    internal string TokenHash { get; set; } = string.Empty;
    internal DateTimeOffset ExpiresAt { get; set; }
    internal bool Consumed { get; set; }
}

/// <summary>EF Core 审计日志行。</summary>
public sealed class AuditLogRow
{
    internal long Sequence { get; set; }
    internal Guid Id { get; set; }
    internal Guid? ActorId { get; set; }
    internal string ActorNameSnapshot { get; set; } = string.Empty;
    internal string Category { get; set; } = string.Empty;
    internal string Action { get; set; } = string.Empty;
    internal string Resource { get; set; } = string.Empty;
    internal DateTimeOffset OccurredAt { get; set; }
    internal long OccurredAtUnixMilliseconds { get; set; }
    internal string OutcomeCode { get; set; } = string.Empty;
    internal string PropertiesJson { get; set; } = "{}";
}

/// <summary>EF Core 操作日志行。</summary>
public sealed class ActivityLogRow
{
    internal long Sequence { get; set; }
    internal Guid Id { get; set; }
    internal Guid? ActorId { get; set; }
    internal string ActorNameSnapshot { get; set; } = string.Empty;
    internal string Action { get; set; } = string.Empty;
    internal string Resource { get; set; } = string.Empty;
    internal DateTimeOffset OccurredAt { get; set; }
    internal long OccurredAtUnixMilliseconds { get; set; }
    internal string PropertiesJson { get; set; } = "{}";
    internal string OutcomeCode { get; set; } = "success";
    internal string? SourceAddress { get; set; }
}

/// <summary>EF Core 长操作行。</summary>
public sealed class OperationRow
{
    internal Guid Id { get; set; }
    internal string Kind { get; set; } = string.Empty;
    internal string Target { get; set; } = string.Empty;
    internal string Status { get; set; } = string.Empty;
    internal double Progress { get; set; }
    internal string? Stage { get; set; }
    internal string? ErrorCode { get; set; }
    internal DateTimeOffset CreatedAt { get; set; }
    internal long CreatedAtUnixMilliseconds { get; set; }
    internal DateTimeOffset? CompletedAt { get; set; }
    internal Guid? InitiatedBy { get; set; }
    internal string? Result { get; set; }
    internal bool Cancellable { get; set; }
}

/// <summary>EF Core 设置行。</summary>
public sealed class SettingRow
{
    internal string Key { get; set; } = string.Empty;
    internal string ValueJson { get; set; } = string.Empty;
    internal long Revision { get; set; }
    internal DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>按用户与页面保存的仪表盘布局行。</summary>
public sealed class DashboardLayoutRow
{
    internal Guid UserId { get; set; }
    internal string Page { get; set; } = string.Empty;
    internal int Version { get; set; }
    internal string CardsJson { get; set; } = "[]";
    internal DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>EF Core IP 封禁行。</summary>
public sealed class IpBanRow
{
    internal string Address { get; set; } = string.Empty;
    internal DateTimeOffset ExpiresAt { get; set; }
    internal string ReasonCode { get; set; } = string.Empty;
}

/// <summary>EF Core 幂等结果行。</summary>
public sealed class IdempotencyRow
{
    internal string Key { get; set; } = string.Empty;
    internal string RequestHash { get; set; } = string.Empty;
    internal byte[] Response { get; set; } = [];
    internal DateTimeOffset ExpiresAt { get; set; }
}

/// <summary>控制平面分片上传会话行。</summary>
public sealed class UploadSessionRow
{
    internal Guid Id { get; set; }
    internal Guid InstanceId { get; set; }
    internal Guid NodeId { get; set; }
    internal string FileName { get; set; } = string.Empty;
    internal string DestinationPath { get; set; } = string.Empty;
    internal long TotalBytes { get; set; }
    internal int PartSizeBytes { get; set; }
    internal int PartCount { get; set; }
    internal string ReceivedPartsJson { get; set; } = "[]";
    internal bool Overwrite { get; set; }
    internal DateTimeOffset CreatedAt { get; set; }
    internal long ExpiresAtUnixMilliseconds { get; set; }
}

/// <summary>Servitium 控制平面的 SQLite 数据库上下文。</summary>
/// <param name="options">数据库选项。</param>
public sealed partial class ServitiumDbContext(DbContextOptions<ServitiumDbContext> options) : DbContext(options)
{
    internal DbSet<UserRow> Users => Set<UserRow>();
    internal DbSet<SessionRow> Sessions => Set<SessionRow>();
    internal DbSet<ApiKeyRow> ApiKeys => Set<ApiKeyRow>();
    internal DbSet<InstanceGrantRow> InstanceGrants => Set<InstanceGrantRow>();
    internal DbSet<RecoveryCodeRow> RecoveryCodes => Set<RecoveryCodeRow>();
    internal DbSet<ExternalIdentityRow> ExternalIdentities => Set<ExternalIdentityRow>();
    internal DbSet<WindowsIdentityRow> WindowsIdentities => Set<WindowsIdentityRow>();
    internal DbSet<OidcStateRow> OidcStates => Set<OidcStateRow>();
    internal DbSet<LocalSessionTicketRow> LocalSessionTickets => Set<LocalSessionTicketRow>();
    internal DbSet<AuditLogRow> AuditLogs => Set<AuditLogRow>();
    internal DbSet<ActivityLogRow> ActivityLogs => Set<ActivityLogRow>();
    internal DbSet<OperationRow> Operations => Set<OperationRow>();
    internal DbSet<SettingRow> Settings => Set<SettingRow>();
    internal DbSet<DashboardLayoutRow> DashboardLayouts => Set<DashboardLayoutRow>();
    internal DbSet<IpBanRow> IpBans => Set<IpBanRow>();
    internal DbSet<IdempotencyRow> IdempotencyRecords => Set<IdempotencyRow>();
    internal DbSet<UploadSessionRow> UploadSessions => Set<UploadSessionRow>();
    internal DbSet<NodeRow> Nodes => Set<NodeRow>();
    internal DbSet<NodeCredentialRow> NodeCredentials => Set<NodeCredentialRow>();
    internal DbSet<NodeRegistrationTokenRow> NodeRegistrationTokens => Set<NodeRegistrationTokenRow>();
    internal DbSet<NodeMetricRow> NodeMetrics => Set<NodeMetricRow>();
    internal DbSet<NodeInstanceMirrorRow> NodeInstanceMirrors => Set<NodeInstanceMirrorRow>();
    internal DbSet<NodeJavaRuntimeMirrorRow> NodeJavaRuntimeMirrors => Set<NodeJavaRuntimeMirrorRow>();
    internal DbSet<NodeScheduleMirrorRow> NodeScheduleMirrors => Set<NodeScheduleMirrorRow>();
    internal DbSet<NodeMirrorStateRow> NodeMirrorStates => Set<NodeMirrorStateRow>();

    /// <inheritdoc />
    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        RejectAuditMutation();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    /// <inheritdoc />
    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        RejectAuditMutation();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    /// <inheritdoc />
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        ConfigureUsers(modelBuilder);
        ConfigureSessions(modelBuilder);
        ConfigureApiKeys(modelBuilder);
        ConfigureGrants(modelBuilder);
        ConfigureIdentityExtensions(modelBuilder);
        ConfigureLogs(modelBuilder);
        ConfigureOperations(modelBuilder);
        ConfigureSettings(modelBuilder);
        ConfigureDashboardLayouts(modelBuilder);
        ConfigureSecurityState(modelBuilder);
        ConfigureUploadSessions(modelBuilder);
        ConfigureNodes(modelBuilder);
        ConfigureInstanceMetrics(modelBuilder);
        ConfigureHmcl(modelBuilder);
    }

    private static void ConfigureUploadSessions(ModelBuilder modelBuilder)
    {
        _ = modelBuilder.Entity<UploadSessionRow>(entity =>
        {
            _ = entity.ToTable("UploadSessions");
            _ = entity.HasKey(row => row.Id);
            _ = entity.Property(row => row.Id).HasConversion<string>();
            _ = entity.Property(row => row.InstanceId).HasConversion<string>();
            _ = entity.Property(row => row.NodeId).HasConversion<string>();
            _ = entity.Property(row => row.FileName).HasMaxLength(255);
            _ = entity.Property(row => row.DestinationPath).HasMaxLength(1024);
            _ = entity.Property(row => row.TotalBytes);
            _ = entity.Property(row => row.PartSizeBytes);
            _ = entity.Property(row => row.PartCount);
            _ = entity.Property(row => row.ReceivedPartsJson);
            _ = entity.Property(row => row.Overwrite);
            _ = entity.Property(row => row.CreatedAt);
            _ = entity.Property(row => row.ExpiresAtUnixMilliseconds);
            _ = entity.HasIndex(row => row.ExpiresAtUnixMilliseconds);
            _ = entity.HasIndex(row => new { row.InstanceId, row.NodeId });
        });
    }

    private static void ConfigureUsers(ModelBuilder modelBuilder)
    {
        _ = modelBuilder.Entity<UserRow>(entity =>
        {
            _ = entity.ToTable("Users");
            _ = entity.HasKey(row => row.Id);
            _ = entity.Property(row => row.Id).HasConversion<string>();
            _ = entity.Property(row => row.UserName).HasMaxLength(64);
            _ = entity.Property(row => row.DisplayName).HasMaxLength(128);
            _ = entity.Property(row => row.Role);
            _ = entity.Property(row => row.PreviousRole);
            _ = entity.Property(row => row.OwnerMarker).HasMaxLength(8);
            _ = entity.Property(row => row.PasswordHash).HasMaxLength(256);
            _ = entity.Property(row => row.PreferredLanguage).HasMaxLength(16);
            _ = entity.Property(row => row.CreatedAt);
            _ = entity.Property(row => row.LastSignedInAt);
            _ = entity.Property(row => row.IsDeleted);
            _ = entity.Property(row => row.ProtectedTotpSecret);
            _ = entity.Property(row => row.TotpConfirmed);
            _ = entity.Property(row => row.SecurityStamp).IsConcurrencyToken();
            _ = entity.HasIndex(row => row.UserName).IsUnique();
            _ = entity.HasIndex(row => row.OwnerMarker).IsUnique();
        });
    }

    private static void ConfigureSessions(ModelBuilder modelBuilder)
    {
        _ = modelBuilder.Entity<SessionRow>(entity =>
        {
            _ = entity.ToTable("Sessions");
            _ = entity.HasKey(row => row.Id);
            _ = entity.Property(row => row.Id).HasConversion<string>();
            _ = entity.Property(row => row.UserId).HasConversion<string>();
            _ = entity.Property(row => row.TokenHash).HasMaxLength(64);
            _ = entity.Property(row => row.CreatedAt);
            _ = entity.Property(row => row.LastSeenAt);
            _ = entity.Property(row => row.ExpiresAt);
            _ = entity.Property(row => row.AbsoluteExpiresAt);
            _ = entity.Property(row => row.SecurityStamp);
            _ = entity.Property(row => row.IsRestricted);
            _ = entity.HasIndex(row => row.TokenHash).IsUnique();
            _ = entity.HasIndex(row => row.UserId);
            _ = entity.HasOne<UserRow>().WithMany().HasForeignKey(row => row.UserId).OnDelete(DeleteBehavior.Cascade);
        });
    }

    private static void ConfigureApiKeys(ModelBuilder modelBuilder)
    {
        _ = modelBuilder.Entity<ApiKeyRow>(entity =>
        {
            _ = entity.ToTable("ApiKeys");
            _ = entity.HasKey(row => row.Id);
            _ = entity.Property(row => row.Id).HasConversion<string>();
            _ = entity.Property(row => row.UserId).HasConversion<string>();
            _ = entity.Property(row => row.Name).HasMaxLength(128);
            _ = entity.Property(row => row.KeyHash).HasMaxLength(64);
            _ = entity.Property(row => row.Scope).HasMaxLength(512);
            _ = entity.Property(row => row.CreatedAt);
            _ = entity.Property(row => row.ExpiresAt);
            _ = entity.Property(row => row.LastUsedAt);
            _ = entity.Property(row => row.IsRevoked);
            _ = entity.HasIndex(row => row.KeyHash).IsUnique();
            _ = entity.HasIndex(row => row.UserId);
            _ = entity.HasOne<UserRow>().WithMany().HasForeignKey(row => row.UserId).OnDelete(DeleteBehavior.Cascade);
        });
    }

    private static void ConfigureGrants(ModelBuilder modelBuilder)
    {
        _ = modelBuilder.Entity<InstanceGrantRow>(entity =>
        {
            _ = entity.ToTable("InstanceGrants");
            _ = entity.HasKey(row => new { row.UserId, row.InstanceId });
            _ = entity.Property(row => row.UserId).HasConversion<string>();
            _ = entity.Property(row => row.InstanceId).HasConversion<string>();
            _ = entity.HasOne<UserRow>().WithMany().HasForeignKey(row => row.UserId).OnDelete(DeleteBehavior.Cascade);
        });
    }

    private static void ConfigureIdentityExtensions(ModelBuilder modelBuilder)
    {
        _ = modelBuilder.Entity<RecoveryCodeRow>(entity =>
        {
            _ = entity.ToTable("RecoveryCodes");
            _ = entity.HasKey(row => row.Id);
            _ = entity.Property(row => row.Id).HasConversion<string>();
            _ = entity.Property(row => row.UserId).HasConversion<string>();
            _ = entity.Property(row => row.CodeHash).HasMaxLength(64);
            _ = entity.Property(row => row.CreatedAt);
            _ = entity.HasIndex(row => new { row.UserId, row.CodeHash }).IsUnique();
            _ = entity.HasOne<UserRow>().WithMany().HasForeignKey(row => row.UserId).OnDelete(DeleteBehavior.Cascade);
        });
        _ = modelBuilder.Entity<ExternalIdentityRow>(entity =>
        {
            _ = entity.ToTable("ExternalIdentities");
            _ = entity.HasKey(row => row.Id);
            _ = entity.Property(row => row.Id).HasConversion<string>();
            _ = entity.Property(row => row.UserId).HasConversion<string>();
            _ = entity.Property(row => row.Issuer).HasMaxLength(2048);
            _ = entity.Property(row => row.Subject).HasMaxLength(512);
            _ = entity.Property(row => row.CreatedAt);
            _ = entity.HasIndex(row => row.UserId).IsUnique();
            _ = entity.HasIndex(row => new { row.Issuer, row.Subject }).IsUnique();
            _ = entity.HasOne<UserRow>().WithMany().HasForeignKey(row => row.UserId).OnDelete(DeleteBehavior.Cascade);
        });
        _ = modelBuilder.Entity<WindowsIdentityRow>(entity =>
        {
            _ = entity.ToTable("WindowsIdentities");
            _ = entity.HasKey(row => row.Id);
            _ = entity.Property(row => row.Id).HasConversion<string>();
            _ = entity.Property(row => row.UserId).HasConversion<string>();
            _ = entity.Property(row => row.Sid).HasMaxLength(184);
            _ = entity.Property(row => row.CreatedAt);
            _ = entity.HasIndex(row => row.UserId).IsUnique();
            _ = entity.HasIndex(row => row.Sid).IsUnique();
            _ = entity.HasOne<UserRow>().WithMany().HasForeignKey(row => row.UserId).OnDelete(DeleteBehavior.Cascade);
        });
        _ = modelBuilder.Entity<OidcStateRow>(entity =>
        {
            _ = entity.ToTable("OidcStates");
            _ = entity.HasKey(row => row.Id);
            _ = entity.Property(row => row.Id).HasConversion<string>();
            _ = entity.Property(row => row.StateHash).HasMaxLength(64);
            _ = entity.Property(row => row.Nonce).HasMaxLength(128);
            _ = entity.Property(row => row.CodeVerifier).HasMaxLength(128);
            _ = entity.Property(row => row.ReturnUrl).HasMaxLength(2048);
            _ = entity.Property(row => row.BindingUserId).HasConversion<string>();
            _ = entity.Property(row => row.ExpiresAt);
            _ = entity.Property(row => row.Consumed);
            _ = entity.HasIndex(row => row.StateHash).IsUnique();
            _ = entity.HasIndex(row => row.ExpiresAt);
        });
        _ = modelBuilder.Entity<LocalSessionTicketRow>(entity =>
        {
            _ = entity.ToTable("LocalSessionTickets");
            _ = entity.HasKey(row => row.Id);
            _ = entity.Property(row => row.Id).HasConversion<string>();
            _ = entity.Property(row => row.UserId).HasConversion<string>();
            _ = entity.Property(row => row.TokenHash).HasMaxLength(64);
            _ = entity.Property(row => row.ExpiresAt);
            _ = entity.Property(row => row.Consumed);
            _ = entity.HasIndex(row => row.TokenHash).IsUnique();
            _ = entity.HasIndex(row => row.ExpiresAt);
            _ = entity.HasOne<UserRow>().WithMany().HasForeignKey(row => row.UserId).OnDelete(DeleteBehavior.Cascade);
        });
    }

    private static void ConfigureLogs(ModelBuilder modelBuilder)
    {
        _ = modelBuilder.Entity<AuditLogRow>(entity =>
        {
            _ = entity.ToTable("AuditLogs");
            _ = entity.HasKey(row => row.Sequence);
            _ = entity.Property(row => row.Sequence).ValueGeneratedOnAdd();
            _ = entity.Property(row => row.Id).HasConversion<string>();
            _ = entity.Property(row => row.ActorId).HasConversion<string>();
            _ = entity.Property(row => row.ActorNameSnapshot).HasMaxLength(128);
            _ = entity.Property(row => row.Category).HasMaxLength(64);
            _ = entity.Property(row => row.Action).HasMaxLength(128);
            _ = entity.Property(row => row.Resource).HasMaxLength(512);
            _ = entity.Property(row => row.OccurredAt);
            _ = entity.Property(row => row.OccurredAtUnixMilliseconds);
            _ = entity.Property(row => row.OutcomeCode).HasMaxLength(64);
            _ = entity.Property(row => row.PropertiesJson).HasDefaultValue("{}");
            _ = entity.HasIndex(row => row.Id).IsUnique();
            _ = entity.HasIndex(row => row.OccurredAt);
            _ = entity.HasIndex(row => row.OccurredAtUnixMilliseconds);
        });
        _ = modelBuilder.Entity<ActivityLogRow>(entity =>
        {
            _ = entity.ToTable("ActivityLogs");
            _ = entity.HasKey(row => row.Sequence);
            _ = entity.Property(row => row.Sequence).ValueGeneratedOnAdd();
            _ = entity.Property(row => row.Id).HasConversion<string>();
            _ = entity.Property(row => row.ActorId).HasConversion<string>();
            _ = entity.Property(row => row.ActorNameSnapshot).HasMaxLength(128);
            _ = entity.Property(row => row.Action).HasMaxLength(128);
            _ = entity.Property(row => row.Resource).HasMaxLength(512);
            _ = entity.Property(row => row.OccurredAt);
            _ = entity.Property(row => row.OccurredAtUnixMilliseconds);
            _ = entity.Property(row => row.PropertiesJson);
            _ = entity.Property(row => row.OutcomeCode).HasMaxLength(64).HasDefaultValue("success");
            _ = entity.Property(row => row.SourceAddress).HasMaxLength(64);
            _ = entity.HasIndex(row => row.Id).IsUnique();
            _ = entity.HasIndex(row => row.OccurredAt);
            _ = entity.HasIndex(row => row.OccurredAtUnixMilliseconds);
        });
    }

    private static void ConfigureOperations(ModelBuilder modelBuilder)
    {
        _ = modelBuilder.Entity<OperationRow>(entity =>
        {
            _ = entity.ToTable("Operations");
            _ = entity.HasKey(row => row.Id);
            _ = entity.Property(row => row.Id).HasConversion<string>();
            _ = entity.Property(row => row.Kind).HasMaxLength(64);
            _ = entity.Property(row => row.Target).HasMaxLength(512);
            _ = entity.Property(row => row.Status).HasMaxLength(32);
            _ = entity.Property(row => row.Progress);
            _ = entity.Property(row => row.Stage).HasMaxLength(128);
            _ = entity.Property(row => row.ErrorCode).HasMaxLength(64);
            _ = entity.Property(row => row.CreatedAt);
            _ = entity.Property(row => row.CreatedAtUnixMilliseconds);
            _ = entity.Property(row => row.CompletedAt);
            _ = entity.Property(row => row.InitiatedBy).HasConversion<string>();
            _ = entity.Property(row => row.Result);
            _ = entity.Property(row => row.Cancellable);
            _ = entity.HasIndex(row => row.CreatedAtUnixMilliseconds);
        });
    }

    private static void ConfigureSettings(ModelBuilder modelBuilder)
    {
        _ = modelBuilder.Entity<SettingRow>(entity =>
        {
            _ = entity.ToTable("Settings");
            _ = entity.HasKey(row => row.Key);
            _ = entity.Property(row => row.Key).HasMaxLength(128);
            _ = entity.Property(row => row.ValueJson);
            _ = entity.Property(row => row.Revision).IsConcurrencyToken();
            _ = entity.Property(row => row.UpdatedAt);
        });
    }

    private static void ConfigureDashboardLayouts(ModelBuilder modelBuilder)
    {
        _ = modelBuilder.Entity<DashboardLayoutRow>(entity =>
        {
            _ = entity.ToTable("DashboardLayouts");
            _ = entity.HasKey(row => new { row.UserId, row.Page });
            _ = entity.Property(row => row.UserId).HasConversion<string>();
            _ = entity.Property(row => row.Page).HasMaxLength(64);
            _ = entity.Property(row => row.Version);
            _ = entity.Property(row => row.CardsJson);
            _ = entity.Property(row => row.UpdatedAt);
            _ = entity.HasIndex(row => row.UserId);
        });
    }

    private static void ConfigureSecurityState(ModelBuilder modelBuilder)
    {
        _ = modelBuilder.Entity<IpBanRow>(entity =>
        {
            _ = entity.ToTable("IpBans");
            _ = entity.HasKey(row => row.Address);
            _ = entity.Property(row => row.Address).HasMaxLength(64);
            _ = entity.Property(row => row.ExpiresAt);
            _ = entity.Property(row => row.ReasonCode).HasMaxLength(64);
        });
        _ = modelBuilder.Entity<IdempotencyRow>(entity =>
        {
            _ = entity.ToTable("IdempotencyRecords");
            _ = entity.HasKey(row => row.Key);
            _ = entity.Property(row => row.Key).HasMaxLength(128);
            _ = entity.Property(row => row.RequestHash).HasMaxLength(64);
            _ = entity.Property(row => row.Response).HasMaxLength(1024 * 1024);
            _ = entity.Property(row => row.ExpiresAt);
            _ = entity.HasIndex(row => row.ExpiresAt);
        });
    }

    private void RejectAuditMutation()
    {
        IEnumerable<EntityEntry<AuditLogRow>> mutations = ChangeTracker.Entries<AuditLogRow>()
            .Where(entry => entry.State is EntityState.Modified or EntityState.Deleted);
        if (mutations.Any())
        {
            throw new InvalidOperationException("Audit log entries are append-only.");
        }
    }
}
