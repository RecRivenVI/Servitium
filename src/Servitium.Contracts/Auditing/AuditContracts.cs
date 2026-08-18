using System;

namespace Servitium.Contracts.Auditing;

/// <summary>操作日志响应。</summary>
/// <param name="Id">条目标识。</param>
/// <param name="ActorName">操作者快照。</param>
/// <param name="Action">动作。</param>
/// <param name="Resource">资源。</param>
/// <param name="OccurredAt">发生时间。</param>
/// <param name="OutcomeCode">稳定结果码。</param>
/// <param name="SourceAddress">来源地址。</param>
/// <param name="Properties">已脱敏结构化属性。</param>
public sealed record ActivityLogDto(
    Guid Id,
    string ActorName,
    string Action,
    string Resource,
    DateTimeOffset OccurredAt,
    string OutcomeCode = "success",
    string? SourceAddress = null,
    System.Collections.Generic.IReadOnlyDictionary<string, string>? Properties = null);

/// <summary>安全审计响应。</summary>
/// <param name="Id">条目标识。</param>
/// <param name="Category">类别。</param>
/// <param name="Action">动作。</param>
/// <param name="OutcomeCode">结果码。</param>
/// <param name="OccurredAt">发生时间。</param>
/// <param name="Resource">资源快照。</param>
/// <param name="ActorName">操作者名称快照。</param>
/// <param name="Properties">已脱敏结构化属性。</param>
public sealed record AuditLogDto(
    Guid Id,
    string Category,
    string Action,
    string OutcomeCode,
    DateTimeOffset OccurredAt,
    string? Resource = null,
    string? ActorName = null,
    System.Collections.Generic.IReadOnlyDictionary<string, string>? Properties = null);
