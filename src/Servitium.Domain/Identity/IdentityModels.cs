using System;
using System.Collections.Generic;
using System.Linq;

using Servitium.Domain.Primitives;

namespace Servitium.Domain.Identity;

/// <summary>
/// 定义系统角色。
/// </summary>
public enum Role
{
    /// <summary>唯一所有者。</summary>
    Owner,
    /// <summary>管理员。</summary>
    Administrator,
    /// <summary>普通用户。</summary>
    User,
    /// <summary>禁用用户。</summary>
    Disabled,
}

/// <summary>
/// 表示系统用户并强制所有者不可变约束。
/// </summary>
/// <remarks>
/// 初始化用户。
/// </remarks>
public sealed class User(Guid id, string userName, string displayName, Role role, DateTimeOffset createdAt) : Entity(id)
{

    /// <summary>获取规范化用户名。</summary>
    public string UserName { get; } = NormalizeUserName(userName);

    /// <summary>获取显示名。</summary>
    public string DisplayName { get; private set; } = Guard.AgainstControlCharacters(
            Guard.AgainstNullOrWhiteSpace(displayName, nameof(displayName)),
            nameof(displayName));

    /// <summary>获取当前角色。</summary>
    public Role Role { get; private set; } = role;

    /// <summary>获取创建时间。</summary>
    public DateTimeOffset CreatedAt { get; } = Guard.AsUtc(createdAt);

    /// <summary>获取上次登录时间。</summary>
    public DateTimeOffset? LastSignedInAt { get; private set; }

    /// <summary>获取用户是否已删除。</summary>
    public bool IsDeleted { get; private set; }

    /// <summary>更新显示名。</summary>
    public void Rename(string displayName)
    {
        DisplayName = Guard.AgainstControlCharacters(
            Guard.AgainstNullOrWhiteSpace(displayName, nameof(displayName)),
            nameof(displayName));
    }

    /// <summary>更新角色。</summary>
    public void ChangeRole(Role role)
    {
        EnsureOwnerCanChange(role == Role.Owner, "change role");
        Role = role;
    }

    /// <summary>禁用用户。</summary>
    public void Disable()
    {
        EnsureOwnerCanChange(false, "disable");
        Role = Role.Disabled;
    }

    /// <summary>删除用户登记。</summary>
    public void Delete()
    {
        EnsureOwnerCanChange(false, "delete");
        IsDeleted = true;
    }

    /// <summary>记录一次成功登录。</summary>
    public void MarkSignedIn(DateTimeOffset occurredAt)
    {
        LastSignedInAt = Guard.AsUtc(occurredAt);
    }

    internal void TransferOwnerRole(Role newRole)
    {
        Role = newRole;
    }

    private static string NormalizeUserName(string userName)
    {
        string normalized = Guard.AgainstControlCharacters(
            Guard.AgainstNullOrWhiteSpace(userName, nameof(userName)),
            nameof(userName));
        return normalized.Length > 64
            ? throw new ArgumentOutOfRangeException(nameof(userName), "The user name cannot exceed 64 characters.")
            : normalized;
    }

    private void EnsureOwnerCanChange(bool requestedOwnerRole, string operation)
    {
        if (Role == Role.Owner && !requestedOwnerRole)
        {
            throw new DomainException("owner-immutable", $"The Owner cannot {operation}.");
        }
    }
}

/// <summary>
/// 在聚合边界内维护用户名唯一与唯一 Owner。
/// </summary>
public sealed class UserDirectory
{
    private readonly List<User> _users;

    /// <summary>
    /// 使用唯一 Owner 初始化目录。
    /// </summary>
    public UserDirectory(User owner)
    {
        ArgumentNullException.ThrowIfNull(owner);
        if (owner.Role != Role.Owner)
        {
            throw new ArgumentException("The initial user must be the Owner.", nameof(owner));
        }

        _users = [owner];
    }

    /// <summary>获取用户只读快照。</summary>
    public IReadOnlyList<User> Users => _users.AsReadOnly();

    /// <summary>添加非 Owner 用户。</summary>
    public void Add(User user)
    {
        ArgumentNullException.ThrowIfNull(user);
        if (user.Role == Role.Owner)
        {
            throw new DomainException("owner-immutable", "Use ownership transfer to replace the Owner.");
        }

        if (_users.Any(existing => string.Equals(existing.UserName, user.UserName, StringComparison.OrdinalIgnoreCase)))
        {
            throw new DomainException("user-name-taken", "The user name is already registered.");
        }

        _users.Add(user);
    }

    /// <summary>原子移交 Owner。</summary>
    public void TransferOwnership(Guid currentOwnerId, Guid newOwnerId)
    {
        User current = _users.Single(user => user.Id == currentOwnerId && user.Role == Role.Owner);
        User next = _users.Single(user => user.Id == newOwnerId && !user.IsDeleted && user.Role != Role.Disabled);
        current.TransferOwnerRole(Role.Administrator);
        next.TransferOwnerRole(Role.Owner);
    }
}

/// <summary>表示持久会话。</summary>
public sealed record Session(Guid Id, Guid UserId, string TokenHash, DateTimeOffset ExpiresAt, DateTimeOffset CreatedAt);

/// <summary>表示包含 Argon2id 参数的用户凭据哈希。</summary>
public sealed record UserCredential(Guid Id, Guid UserId, string PasswordHash, DateTimeOffset UpdatedAt);

/// <summary>表示 API Key 元数据。</summary>
public sealed record ApiKey(Guid Id, Guid UserId, string Name, string KeyHash, DateTimeOffset CreatedAt, DateTimeOffset? ExpiresAt);

/// <summary>表示外部身份绑定。</summary>
public sealed record ExternalIdentity(Guid Id, Guid UserId, string Issuer, string Subject);

/// <summary>表示 Windows SID 与用户的绑定。</summary>
public sealed record WindowsIdentityBinding(Guid Id, Guid UserId, string Sid);
