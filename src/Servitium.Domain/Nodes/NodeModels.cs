using System;

using Servitium.Domain.Primitives;

namespace Servitium.Domain.Nodes;

/// <summary>定义节点连接状态。</summary>
public enum NodeStatus
{
    /// <summary>在线。</summary>
    Online,
    /// <summary>离线。</summary>
    Offline,
    /// <summary>协议不兼容。</summary>
    Incompatible,
    /// <summary>凭据已撤销。</summary>
    Revoked,
}

/// <summary>表示受管 Windows 节点。</summary>
/// <remarks>初始化节点。</remarks>
public sealed class Node(Guid id, string name, DateTimeOffset createdAt) : Entity(id)
{

    /// <summary>获取显示名。</summary>
    public string Name { get; private set; } = Guard.AgainstControlCharacters(Guard.AgainstNullOrWhiteSpace(name, nameof(name)), nameof(name));

    /// <summary>获取状态。</summary>
    public NodeStatus Status { get; private set; } = NodeStatus.Offline;

    /// <summary>获取创建时间。</summary>
    public DateTimeOffset CreatedAt { get; } = Guard.AsUtc(createdAt);

    /// <summary>重命名节点。</summary>
    public void Rename(string name)
    {
        Name = Guard.AgainstControlCharacters(Guard.AgainstNullOrWhiteSpace(name, nameof(name)), nameof(name));
    }

    /// <summary>更新连接状态。</summary>
    public void SetStatus(NodeStatus status)
    {
        Status = status;
    }
}

/// <summary>表示节点凭据哈希。</summary>
public sealed record NodeCredential(Guid Id, Guid NodeId, string CredentialHash, DateTimeOffset CreatedAt, DateTimeOffset? RevokedAt);

/// <summary>表示一次性节点注册令牌哈希。</summary>
public sealed record NodeRegistrationToken(Guid Id, string TokenHash, DateTimeOffset ExpiresAt, DateTimeOffset? ConsumedAt);
