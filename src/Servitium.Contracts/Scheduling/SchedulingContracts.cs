using System;
using System.Collections.Generic;

namespace Servitium.Contracts.Scheduling;

/// <summary>计划任务摘要。</summary>
/// <param name="Id">任务标识。</param>
/// <param name="InstanceId">实例标识。</param>
/// <param name="Name">名称。</param>
/// <param name="TriggerKind">触发方式。</param>
/// <param name="ActionKinds">有序动作类型。</param>
/// <param name="RemainingRuns">剩余次数。</param>
/// <param name="Enabled">是否启用。</param>
public sealed record ScheduledTaskDto(Guid Id, Guid InstanceId, string Name, string TriggerKind, IReadOnlyList<string> ActionKinds, int RemainingRuns, bool Enabled);

/// <summary>计划任务触发定义。</summary>
/// <param name="Kind">`interval`、`calendar` 或 `specified`。</param>
/// <param name="IntervalSeconds">固定间隔秒数。</param>
/// <param name="DaysOfWeek">日历触发星期集合。</param>
/// <param name="LocalTime">节点本地时刻。</param>
/// <param name="SpecifiedAt">指定的 UTC 时刻。</param>
public sealed record ScheduleTriggerDto(
    string Kind,
    int? IntervalSeconds,
    IReadOnlyList<int>? DaysOfWeek,
    TimeOnly? LocalTime,
    DateTimeOffset? SpecifiedAt);

/// <summary>计划任务动作。</summary>
/// <param name="Kind">动作类型。</param>
/// <param name="Argument">动作参数。</param>
/// <param name="ContinueOnFailure">失败后是否继续。</param>
public sealed record ScheduleActionDto(string Kind, string? Argument, bool ContinueOnFailure);

/// <summary>创建或更新计划任务。</summary>
/// <param name="Name">名称。</param>
/// <param name="Trigger">触发器。</param>
/// <param name="Actions">有序动作链。</param>
/// <param name="RemainingRuns">-1 为无限，否则为正数。</param>
/// <param name="OverlapPolicy">`skip`、`queue` 或 `concurrent`。</param>
/// <param name="Enabled">是否启用。</param>
/// <param name="Revision">更新时的期望修订。</param>
public sealed record UpsertScheduledTaskRequest(
    string Name,
    ScheduleTriggerDto Trigger,
    IReadOnlyList<ScheduleActionDto> Actions,
    int RemainingRuns,
    string OverlapPolicy,
    bool Enabled,
    long? Revision);

/// <summary>计划任务详情。</summary>
/// <param name="Id">任务标识。</param>
/// <param name="InstanceId">实例标识。</param>
/// <param name="Name">名称。</param>
/// <param name="Trigger">触发器。</param>
/// <param name="Actions">动作链。</param>
/// <param name="RemainingRuns">剩余次数。</param>
/// <param name="OverlapPolicy">重叠策略。</param>
/// <param name="Enabled">是否启用。</param>
/// <param name="Revision">修订。</param>
/// <param name="NextRunAt">下次运行时间。</param>
public sealed record ScheduledTaskDetailsDto(
    Guid Id,
    Guid InstanceId,
    string Name,
    ScheduleTriggerDto Trigger,
    IReadOnlyList<ScheduleActionDto> Actions,
    int RemainingRuns,
    string OverlapPolicy,
    bool Enabled,
    long Revision,
    DateTimeOffset? NextRunAt);

/// <summary>计划任务运行记录。</summary>
/// <param name="Id">运行标识。</param>
/// <param name="TaskId">任务标识。</param>
/// <param name="StartedAt">开始时间。</param>
/// <param name="CompletedAt">完成时间。</param>
/// <param name="Succeeded">是否成功。</param>
/// <param name="OutcomeCode">结果码。</param>
/// <param name="ActionResults">逐动作结果码。</param>
public sealed record ScheduledTaskRunDto(
    Guid Id,
    Guid TaskId,
    DateTimeOffset StartedAt,
    DateTimeOffset? CompletedAt,
    bool Succeeded,
    string OutcomeCode,
    IReadOnlyList<string> ActionResults);
