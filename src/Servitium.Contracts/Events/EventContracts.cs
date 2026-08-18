using System;
using System.Collections.Generic;
using System.Text.Json;

using Servitium.Contracts.Instances;

namespace Servitium.Contracts.Events;

/// <summary>定义协议中固定的事件类型。</summary>
public static class EventTypes
{
    /// <summary>节点状态变化。</summary>
    public const string NodeStatusChanged = "node.status-changed";
    /// <summary>节点指标。</summary>
    public const string NodeMetrics = "node.metrics";
    /// <summary>节点凭据轮换。</summary>
    public const string NodeCredentialRotated = "node.credential-rotated";
    /// <summary>实例创建。</summary>
    public const string InstanceCreated = "instance.created";
    /// <summary>实例更新。</summary>
    public const string InstanceUpdated = "instance.updated";
    /// <summary>实例删除登记。</summary>
    public const string InstanceUnregistered = "instance.unregistered";
    /// <summary>实例状态变化。</summary>
    public const string InstanceStatusChanged = "instance.status-changed";
    /// <summary>实例资源占用。</summary>
    public const string InstanceResourceUsage = "instance.resource-usage";
    /// <summary>实例崩溃。</summary>
    public const string InstanceCrashed = "instance.crashed";
    /// <summary>自动重启耗尽。</summary>
    public const string InstanceAutoRestartExhausted = "instance.auto-restart-exhausted";
    /// <summary>玩家数变化。</summary>
    public const string InstancePlayersChanged = "instance.players-changed";
    /// <summary>长操作开始。</summary>
    public const string OperationStarted = "operation.started";
    /// <summary>长操作进度。</summary>
    public const string OperationProgress = "operation.progress";
    /// <summary>长操作完成。</summary>
    public const string OperationCompleted = "operation.completed";
    /// <summary>文件任务进度。</summary>
    public const string FileTaskProgress = "file.task-progress";
    /// <summary>计划任务运行完成。</summary>
    public const string ScheduleRunCompleted = "schedule.run-completed";
    /// <summary>HMCL Release 创建。</summary>
    public const string HmclReleaseCreated = "hmcl.release-created";
    /// <summary>HMCL Channel 发布。</summary>
    public const string HmclPublished = "hmcl.published";
    /// <summary>HMCL 完整性告警。</summary>
    public const string HmclIntegrityAlert = "hmcl.integrity-alert";
    /// <summary>安全审计事件。</summary>
    public const string AuditSecurityEvent = "audit.security-event";
    /// <summary>会话撤销。</summary>
    public const string SessionRevoked = "session.revoked";
    /// <summary>授权变化。</summary>
    public const string AuthorizationChanged = "authorization.changed";

    /// <summary>获取完整事件类型集合。</summary>
    public static IReadOnlySet<string> All { get; } = new HashSet<string>(
        [
            NodeStatusChanged,
            NodeMetrics,
            NodeCredentialRotated,
            InstanceCreated,
            InstanceUpdated,
            InstanceUnregistered,
            InstanceStatusChanged,
            InstanceResourceUsage,
            InstanceCrashed,
            InstanceAutoRestartExhausted,
            InstancePlayersChanged,
            OperationStarted,
            OperationProgress,
            OperationCompleted,
            FileTaskProgress,
            ScheduleRunCompleted,
            HmclReleaseCreated,
            HmclPublished,
            HmclIntegrityAlert,
            AuditSecurityEvent,
            SessionRevoked,
            AuthorizationChanged,
        ],
        StringComparer.Ordinal);
}

/// <summary>实时事件信封。</summary>
/// <typeparam name="TPayload">载荷类型。</typeparam>
/// <param name="EventId">服务端单调递增事件编号。</param>
/// <param name="Type">事件类型。</param>
/// <param name="OccurredAt">UTC 发生时间。</param>
/// <param name="Scope">授权作用域。</param>
/// <param name="Payload">事件载荷。</param>
public sealed record EventEnvelope<TPayload>(long EventId, string Type, DateTimeOffset OccurredAt, string Scope, TPayload Payload);

/// <summary>非泛型实时事件，用于 SignalR 补发。</summary>
public sealed record RealtimeEventDto(long EventId, string Type, DateTimeOffset OccurredAt, string Scope, JsonElement Payload);

/// <summary>事件补发结果。</summary>
public sealed record EventResumeDto(IReadOnlyList<RealtimeEventDto> Events, bool RequiresFullRefresh);

/// <summary>节点状态变化载荷。</summary>
public sealed record NodeStatusChangedPayload(Guid NodeId, string Status, string? Reason);
/// <summary>节点指标载荷。</summary>
public sealed record NodeMetricsPayload(
    Guid NodeId,
    double CpuPercent,
    long MemoryBytes,
    long FreeDiskBytes,
    DateTimeOffset SampledAt,
    long MemoryTotalBytes,
    long NetworkReceiveBytesPerSecond,
    long NetworkSendBytesPerSecond,
    TimeSpan Uptime);
/// <summary>节点凭据轮换载荷。</summary>
public sealed record NodeCredentialRotatedPayload(Guid NodeId);
/// <summary>实例创建载荷。</summary>
public sealed record InstanceCreatedPayload(InstanceDto Instance);
/// <summary>实例更新载荷。</summary>
public sealed record InstanceUpdatedPayload(Guid InstanceId, IReadOnlyList<string> ChangedFields);
/// <summary>实例删除登记载荷。</summary>
public sealed record InstanceUnregisteredPayload(Guid InstanceId, Guid NodeId);
/// <summary>实例状态变化载荷。</summary>
public sealed record InstanceStatusChangedPayload(Guid InstanceId, string PreviousStatus, string Status, string? Reason, int? ExitCode);
/// <summary>实例资源占用载荷。</summary>
public sealed record InstanceResourceUsagePayload(Guid InstanceId, double CpuPercent, long MemoryBytes, int? OnlinePlayers);
/// <summary>实例崩溃载荷。</summary>
public sealed record InstanceCrashedPayload(Guid InstanceId, int ExitCode, IReadOnlyList<string> RecentOutput);
/// <summary>自动重启耗尽载荷。</summary>
public sealed record InstanceAutoRestartExhaustedPayload(Guid InstanceId, int AttemptCount);
/// <summary>玩家数变化载荷。</summary>
public sealed record InstancePlayersChangedPayload(Guid InstanceId, int OnlinePlayers, int MaximumPlayers);
/// <summary>长操作开始载荷。</summary>
public sealed record OperationStartedPayload(Guid OperationId, string Kind, string Target);
/// <summary>长操作进度载荷。</summary>
public sealed record OperationProgressPayload(Guid OperationId, double Progress, string Stage);
/// <summary>长操作完成载荷。</summary>
public sealed record OperationCompletedPayload(Guid OperationId, string Status, string? Result, string? ErrorCode);
/// <summary>文件任务进度载荷。</summary>
public sealed record FileTaskProgressPayload(Guid InstanceId, Guid TaskId, string Kind, double Progress);
/// <summary>计划任务完成载荷。</summary>
public sealed record ScheduleRunCompletedPayload(Guid InstanceId, Guid TaskId, Guid RunId, bool Succeeded, string? ErrorCode);
/// <summary>Release 创建载荷。</summary>
public sealed record HmclReleaseCreatedPayload(Guid ChannelId, Guid ReleaseId);
/// <summary>Channel 发布载荷。</summary>
public sealed record HmclPublishedPayload(Guid ChannelId, Guid ReleaseId, int Added, int Replaced, int Deleted);
/// <summary>HMCL 完整性告警载荷。</summary>
public sealed record HmclIntegrityAlertPayload(Guid ChannelId, Guid ReleaseId, int MissingEntries);
/// <summary>安全审计摘要载荷。</summary>
public sealed record AuditSecurityEventPayload(string Category, string SummaryCode);
/// <summary>会话撤销载荷。</summary>
public sealed record SessionRevokedPayload(Guid UserId, string Reason);
/// <summary>授权变化载荷。</summary>
public sealed record AuthorizationChangedPayload(Guid UserId);
