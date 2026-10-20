using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using Servitium.Domain.Identity;

namespace Servitium.Application.Identity;

/// <summary>持久化用户身份快照。</summary>
public sealed record StoredUser(
    Guid Id,
    string UserName,
    string DisplayName,
    Role Role,
    string PasswordHash,
    string PreferredLanguage,
    DateTimeOffset CreatedAt,
    DateTimeOffset? LastSignedInAt,
    bool IsDeleted,
    ReadOnlyMemory<byte>? ProtectedTotpSecret,
    bool TotpConfirmed,
    long SecurityStamp);

/// <summary>持久化会话快照。</summary>
public sealed record StoredSession(
    Guid Id,
    Guid UserId,
    string TokenHash,
    DateTimeOffset CreatedAt,
    DateTimeOffset LastSeenAt,
    DateTimeOffset ExpiresAt,
    DateTimeOffset AbsoluteExpiresAt,
    long SecurityStamp,
    bool IsRestricted);

/// <summary>持久化 API Key 快照。</summary>
public sealed record StoredApiKey(
    Guid Id,
    Guid UserId,
    string Name,
    string KeyHash,
    string Scope,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ExpiresAt,
    DateTimeOffset? LastUsedAt,
    bool IsRevoked);

/// <summary>持久化的 TOTP 恢复码哈希。</summary>
public sealed record StoredRecoveryCode(Guid Id, Guid UserId, string CodeHash, DateTimeOffset CreatedAt);

/// <summary>外部 OIDC 身份绑定。</summary>
public sealed record StoredExternalIdentity(Guid Id, Guid UserId, string Issuer, string Subject, DateTimeOffset CreatedAt);

/// <summary>Windows SID 与用户的唯一绑定。</summary>
public sealed record StoredWindowsIdentity(Guid Id, Guid UserId, string Sid, DateTimeOffset CreatedAt);

/// <summary>OIDC 授权请求的一次性状态。</summary>
public sealed record StoredOidcState(
    Guid Id,
    string StateHash,
    string Nonce,
    string CodeVerifier,
    Uri? ReturnUrl,
    Guid? BindingUserId,
    DateTimeOffset ExpiresAt,
    bool Consumed);

/// <summary>本机命名管道签发的一次性换票。</summary>
public sealed record StoredLocalSessionTicket(Guid Id, Guid UserId, string TokenHash, DateTimeOffset ExpiresAt, bool Consumed);

/// <summary>身份、会话和授权实例白名单的持久化端口。</summary>
public interface IIdentityStore
{
    /// <summary>判断系统是否已有用户。</summary>
    Task<bool> HasUsersAsync(CancellationToken cancellationToken);
    /// <summary>原子创建唯一初始 Owner。</summary>
    Task<bool> TryCreateInitialOwnerAsync(StoredUser owner, CancellationToken cancellationToken);
    /// <summary>按标识读取用户。</summary>
    Task<StoredUser?> FindUserAsync(Guid id, CancellationToken cancellationToken);
    /// <summary>按规范化用户名读取用户。</summary>
    Task<StoredUser?> FindUserByNameAsync(string normalizedUserName, CancellationToken cancellationToken);
    /// <summary>按游标列出用户。</summary>
    Task<IReadOnlyList<StoredUser>> ListUsersAsync(Guid? afterId, int limit, CancellationToken cancellationToken);
    /// <summary>创建普通用户。</summary>
    Task<bool> TryCreateUserAsync(StoredUser user, CancellationToken cancellationToken);
    /// <summary>保存用户快照。</summary>
    Task UpdateUserAsync(StoredUser user, CancellationToken cancellationToken);
    /// <summary>禁用或启用用户并保留其启用前角色。</summary>
    Task<StoredUser?> SetUserDisabledAsync(Guid userId, bool disabled, CancellationToken cancellationToken);
    /// <summary>软删除用户并在一个事务内移除其全部认证材料、授权与布局。</summary>
    Task<bool> SoftDeleteUserAsync(Guid userId, CancellationToken cancellationToken);
    /// <summary>读取用户统计。</summary>
    Task<(int Total, int Enabled, int Disabled, int WithTotp, int WithExternalIdentity)> GetUserOverviewAsync(CancellationToken cancellationToken);
    /// <summary>统计指定时刻仍有效的非受限会话。</summary>
    Task<int> CountActiveSessionsAsync(DateTimeOffset now, CancellationToken cancellationToken);
    /// <summary>读取用户获授权的实例标识。</summary>
    Task<IReadOnlySet<Guid>> ListInstanceGrantsAsync(Guid userId, CancellationToken cancellationToken);
    /// <summary>替换用户获授权的实例标识。</summary>
    Task ReplaceInstanceGrantsAsync(Guid userId, IReadOnlySet<Guid> instanceIds, CancellationToken cancellationToken);
    /// <summary>移除所有用户对指定实例的授权。</summary>
    Task<int> RemoveInstanceGrantsAsync(Guid instanceId, CancellationToken cancellationToken);
    /// <summary>创建会话。</summary>
    Task AddSessionAsync(StoredSession session, CancellationToken cancellationToken);
    /// <summary>按令牌哈希读取会话。</summary>
    Task<StoredSession?> FindSessionAsync(string tokenHash, CancellationToken cancellationToken);
    /// <summary>更新会话活动时间和滑动过期时间。</summary>
    Task TouchSessionAsync(Guid sessionId, DateTimeOffset lastSeenAt, DateTimeOffset expiresAt, CancellationToken cancellationToken);
    /// <summary>撤销指定会话。</summary>
    Task RevokeSessionAsync(Guid sessionId, CancellationToken cancellationToken);
    /// <summary>撤销用户除指定会话外的所有会话。</summary>
    Task RevokeOtherSessionsAsync(Guid userId, Guid retainedSessionId, CancellationToken cancellationToken);
    /// <summary>撤销用户全部会话。</summary>
    Task RevokeAllSessionsAsync(Guid userId, CancellationToken cancellationToken);
    /// <summary>列出用户的活动会话。</summary>
    Task<IReadOnlyList<StoredSession>> ListSessionsAsync(Guid userId, CancellationToken cancellationToken);
    /// <summary>原子移交唯一 Owner。</summary>
    Task TransferOwnershipAsync(Guid currentOwnerId, Guid newOwnerId, CancellationToken cancellationToken);
    /// <summary>创建 API Key。</summary>
    Task AddApiKeyAsync(StoredApiKey apiKey, CancellationToken cancellationToken);
    /// <summary>按密钥哈希读取 API Key。</summary>
    Task<StoredApiKey?> FindApiKeyAsync(string keyHash, CancellationToken cancellationToken);
    /// <summary>记录 API Key 使用时间。</summary>
    Task TouchApiKeyAsync(Guid apiKeyId, DateTimeOffset usedAt, CancellationToken cancellationToken);
    /// <summary>撤销 API Key。</summary>
    Task RevokeApiKeyAsync(Guid apiKeyId, Guid ownerUserId, CancellationToken cancellationToken);
    /// <summary>列出用户的 API Key。</summary>
    Task<IReadOnlyList<StoredApiKey>> ListApiKeysAsync(Guid userId, CancellationToken cancellationToken);
    /// <summary>替换用户的恢复码哈希。</summary>
    Task ReplaceRecoveryCodesAsync(Guid userId, IReadOnlyList<StoredRecoveryCode> codes, CancellationToken cancellationToken);
    /// <summary>原子消费一个恢复码哈希。</summary>
    Task<bool> ConsumeRecoveryCodeAsync(Guid userId, string codeHash, CancellationToken cancellationToken);
    /// <summary>读取用户的外部身份。</summary>
    Task<StoredExternalIdentity?> FindExternalIdentityAsync(Guid userId, CancellationToken cancellationToken);
    /// <summary>按发行者和 subject 读取外部身份。</summary>
    Task<StoredExternalIdentity?> FindExternalIdentityAsync(string issuer, string subject, CancellationToken cancellationToken);
    /// <summary>创建外部身份绑定；冲突时返回 false。</summary>
    Task<bool> TrySetExternalIdentityAsync(StoredExternalIdentity identity, CancellationToken cancellationToken);
    /// <summary>删除外部身份绑定。</summary>
    Task RemoveExternalIdentityAsync(Guid userId, CancellationToken cancellationToken);
    /// <summary>读取 Windows SID 绑定。</summary>
    Task<StoredWindowsIdentity?> FindWindowsIdentityAsync(Guid userId, CancellationToken cancellationToken);
    /// <summary>按 SID 读取 Windows 身份绑定。</summary>
    Task<StoredWindowsIdentity?> FindWindowsIdentityBySidAsync(string sid, CancellationToken cancellationToken);
    /// <summary>设置 Windows SID 绑定；冲突时返回 false。</summary>
    Task<bool> TrySetWindowsIdentityAsync(StoredWindowsIdentity identity, CancellationToken cancellationToken);
    /// <summary>保存 OIDC 一次性状态。</summary>
    Task AddOidcStateAsync(StoredOidcState state, CancellationToken cancellationToken);
    /// <summary>原子消费 OIDC state。</summary>
    Task<StoredOidcState?> ConsumeOidcStateAsync(string stateHash, DateTimeOffset now, CancellationToken cancellationToken);
    /// <summary>保存本机换票。</summary>
    Task AddLocalSessionTicketAsync(StoredLocalSessionTicket ticket, CancellationToken cancellationToken);
    /// <summary>原子消费本机换票。</summary>
    Task<StoredLocalSessionTicket?> ConsumeLocalSessionTicketAsync(string tokenHash, DateTimeOffset now, CancellationToken cancellationToken);
}
