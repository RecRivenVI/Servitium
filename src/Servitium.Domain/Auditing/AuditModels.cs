using System;
using System.Collections.Generic;

using Servitium.Domain.Primitives;

namespace Servitium.Domain.Auditing;

/// <summary>定义审计类别。</summary>
public enum AuditCategory
{
    /// <summary>认证。</summary>
    Authentication,
    /// <summary>授权。</summary>
    Authorization,
    /// <summary>凭据。</summary>
    Credential,
    /// <summary>安全设置。</summary>
    SecuritySetting,
    /// <summary>数据删除。</summary>
    DestructiveAction,
}

/// <summary>表示操作日志条目。</summary>
public sealed record ActivityLogEntry(
    Guid Id,
    Guid? ActorId,
    string ActorNameSnapshot,
    string Action,
    string Resource,
    DateTimeOffset OccurredAt,
    IReadOnlyDictionary<string, string> Properties,
    string OutcomeCode = "success",
    string? SourceAddress = null);

/// <summary>表示只追加、无更新路径的安全审计条目。</summary>
/// <remarks>初始化审计条目。</remarks>
public sealed class AuditLogEntry(
    Guid id,
    Guid? actorId,
    string actorNameSnapshot,
    AuditCategory category,
    string action,
    string resource,
    DateTimeOffset occurredAt,
    string outcomeCode,
    IReadOnlyDictionary<string, string>? properties = null)
{

    /// <summary>获取条目标识。</summary>
    public Guid Id { get; } = Guard.AgainstEmpty(id, nameof(id));

    /// <summary>获取操作者标识。</summary>
    public Guid? ActorId { get; } = actorId;

    /// <summary>获取操作者名称快照。</summary>
    public string ActorNameSnapshot { get; } = Guard.AgainstNullOrWhiteSpace(actorNameSnapshot, nameof(actorNameSnapshot));

    /// <summary>获取类别。</summary>
    public AuditCategory Category { get; } = category;

    /// <summary>获取动作。</summary>
    public string Action { get; } = Guard.AgainstNullOrWhiteSpace(action, nameof(action));

    /// <summary>获取资源。</summary>
    public string Resource { get; } = Guard.AgainstNullOrWhiteSpace(resource, nameof(resource));

    /// <summary>获取 UTC 发生时间。</summary>
    public DateTimeOffset OccurredAt { get; } = Guard.AsUtc(occurredAt);

    /// <summary>获取结果码。</summary>
    public string OutcomeCode { get; } = Guard.AgainstNullOrWhiteSpace(outcomeCode, nameof(outcomeCode));

    /// <summary>获取已经脱敏的结构化属性。</summary>
    public IReadOnlyDictionary<string, string> Properties { get; } = properties ?? new Dictionary<string, string>(StringComparer.Ordinal);
}
