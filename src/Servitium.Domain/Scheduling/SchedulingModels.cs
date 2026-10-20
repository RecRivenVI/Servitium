using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

using Servitium.Domain.Primitives;

namespace Servitium.Domain.Scheduling;

/// <summary>定义触发方式。</summary>
public enum TriggerKind
{
    /// <summary>固定间隔。</summary>
    Interval,
    /// <summary>星期与时刻。</summary>
    Calendar,
    /// <summary>指定单次时刻。</summary>
    Specified,
}

/// <summary>定义任务动作。</summary>
public enum ScheduleActionKind
{
    /// <summary>延时。</summary>
    Delay,
    /// <summary>发送控制台命令。</summary>
    SendConsoleCommand,
    /// <summary>启动实例。</summary>
    Start,
    /// <summary>停止实例。</summary>
    Stop,
    /// <summary>重启实例。</summary>
    Restart,
    /// <summary>强制终止实例。</summary>
    ForceStop,
}

/// <summary>定义前次运行未完成时的处理策略。</summary>
public enum OverlapPolicy
{
    /// <summary>跳过新触发。</summary>
    Skip,
    /// <summary>排队，最多三次。</summary>
    Queue,
    /// <summary>并发运行。</summary>
    Concurrent,
}

/// <summary>表示任务动作。</summary>
/// <param name="Kind">动作类型。</param>
/// <param name="Argument">动作参数。</param>
/// <param name="ContinueOnFailure">失败后是否继续。</param>
public sealed record ScheduleAction(ScheduleActionKind Kind, string? Argument, bool ContinueOnFailure) : ValueObject;

/// <summary>定义计划触发器。</summary>
public abstract record ScheduleTrigger : ValueObject
{
    /// <summary>获取触发器类型。</summary>
    public abstract TriggerKind Kind { get; }

    /// <summary>计算给定 UTC 时刻之后的下一次触发。</summary>
    public abstract DateTimeOffset? NextAfter(DateTimeOffset afterUtc, TimeZoneInfo nodeTimeZone);
}

/// <summary>表示固定间隔触发器。</summary>
public sealed record IntervalTrigger : ScheduleTrigger
{
    /// <summary>初始化固定间隔触发器。</summary>
    public IntervalTrigger(TimeSpan interval)
    {
        if (interval < TimeSpan.FromSeconds(10))
        {
            throw new ArgumentOutOfRangeException(nameof(interval), "The minimum interval is 10 seconds.");
        }

        Interval = interval;
    }

    /// <summary>获取间隔。</summary>
    public TimeSpan Interval { get; }

    /// <inheritdoc />
    public override TriggerKind Kind => TriggerKind.Interval;

    /// <inheritdoc />
    public override DateTimeOffset? NextAfter(DateTimeOffset afterUtc, TimeZoneInfo nodeTimeZone)
    {
        ArgumentNullException.ThrowIfNull(nodeTimeZone);
        return Guard.AsUtc(afterUtc) + Interval;
    }
}

/// <summary>表示按节点本地星期和时刻触发的日历触发器。</summary>
public sealed record CalendarTrigger : ScheduleTrigger
{
    private readonly HashSet<DayOfWeek> _days;

    /// <summary>初始化日历触发器。</summary>
    public CalendarTrigger(IEnumerable<DayOfWeek> days, TimeOnly localTime)
    {
        ArgumentNullException.ThrowIfNull(days);
        _days = [.. days];
        if (_days.Count == 0)
        {
            throw new ArgumentException("At least one day of week is required.", nameof(days));
        }

        LocalTime = localTime;
    }

    /// <summary>获取星期集合。</summary>
    public IReadOnlySet<DayOfWeek> Days => new HashSet<DayOfWeek>(_days);

    /// <summary>获取节点本地时刻。</summary>
    public TimeOnly LocalTime { get; }

    /// <inheritdoc />
    public override TriggerKind Kind => TriggerKind.Calendar;

    /// <inheritdoc />
    public override DateTimeOffset? NextAfter(DateTimeOffset afterUtc, TimeZoneInfo nodeTimeZone)
    {
        ArgumentNullException.ThrowIfNull(nodeTimeZone);
        DateTimeOffset utc = Guard.AsUtc(afterUtc);
        DateTimeOffset local = TimeZoneInfo.ConvertTime(utc, nodeTimeZone);

        for (int dayOffset = 0; dayOffset <= 7; dayOffset++)
        {
            DateOnly date = DateOnly.FromDateTime(local.Date).AddDays(dayOffset);
            if (!_days.Contains(date.DayOfWeek))
            {
                continue;
            }

            var candidate = date.ToDateTime(LocalTime, DateTimeKind.Unspecified);
            while (nodeTimeZone.IsInvalidTime(candidate))
            {
                candidate = candidate.AddMinutes(1);
            }

            DateTimeOffset candidateUtc = new(TimeZoneInfo.ConvertTimeToUtc(candidate, nodeTimeZone), TimeSpan.Zero);
            if (candidateUtc > utc)
            {
                return candidateUtc;
            }
        }

        throw new InvalidOperationException("A calendar trigger must produce a result within seven days.");
    }
}

/// <summary>表示指定时刻的一次性触发器。</summary>
public sealed record SpecifiedTrigger : ScheduleTrigger
{
    /// <summary>初始化指定时刻触发器。</summary>
    public SpecifiedTrigger(DateTimeOffset scheduledAt)
    {
        ScheduledAt = Guard.AsUtc(scheduledAt);
    }

    /// <summary>获取指定 UTC 时刻。</summary>
    public DateTimeOffset ScheduledAt { get; }

    /// <inheritdoc />
    public override TriggerKind Kind => TriggerKind.Specified;

    /// <inheritdoc />
    public override DateTimeOffset? NextAfter(DateTimeOffset afterUtc, TimeZoneInfo nodeTimeZone)
    {
        ArgumentNullException.ThrowIfNull(nodeTimeZone);
        return ScheduledAt > Guard.AsUtc(afterUtc) ? ScheduledAt : null;
    }
}

/// <summary>表示实例计划任务。</summary>
public sealed class ScheduledTask : Entity
{
    private readonly ReadOnlyCollection<ScheduleAction> _actions;

    /// <summary>初始化计划任务。</summary>
    public ScheduledTask(
        Guid id,
        Guid instanceId,
        string name,
        ScheduleTrigger trigger,
        IEnumerable<ScheduleAction> actions,
        int remainingRuns,
        OverlapPolicy overlapPolicy,
        bool enabled)
        : base(id)
    {
        InstanceId = Guard.AgainstEmpty(instanceId, nameof(instanceId));
        Name = ValidateName(name);
        Trigger = trigger ?? throw new ArgumentNullException(nameof(trigger));
        ArgumentNullException.ThrowIfNull(actions);
        ScheduleAction[] actionArray = [.. actions];
        if (actionArray.Length is < 1 or > 10)
        {
            throw new ArgumentOutOfRangeException(nameof(actions), "A scheduled task must contain between 1 and 10 actions.");
        }

        if (remainingRuns is not -1 and < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(remainingRuns), "Remaining runs must be -1 or at least 1.");
        }

        _actions = Array.AsReadOnly(actionArray);
        RemainingRuns = remainingRuns;
        OverlapPolicy = overlapPolicy;
        Enabled = enabled;
    }

    /// <summary>获取实例标识。</summary>
    public Guid InstanceId { get; }

    /// <summary>获取名称。</summary>
    public string Name { get; private set; }

    /// <summary>获取触发器。</summary>
    public ScheduleTrigger Trigger { get; private set; }

    /// <summary>获取有序动作。</summary>
    public IReadOnlyList<ScheduleAction> Actions => _actions;

    /// <summary>获取剩余次数，-1 表示不限。</summary>
    public int RemainingRuns { get; private set; }

    /// <summary>获取重叠策略。</summary>
    public OverlapPolicy OverlapPolicy { get; private set; }

    /// <summary>获取是否启用。</summary>
    public bool Enabled { get; private set; }

    /// <summary>记录一次定时触发并维护剩余次数。</summary>
    public void MarkScheduledRunTriggered()
    {
        if (!Enabled)
        {
            throw new DomainException("conflict", "A disabled scheduled task cannot be triggered.");
        }

        if (RemainingRuns > 0)
        {
            RemainingRuns--;
            if (RemainingRuns == 0)
            {
                Enabled = false;
            }
        }
    }

    /// <summary>重命名任务。</summary>
    public void Rename(string name)
    {
        Name = ValidateName(name);
    }

    private static string ValidateName(string name)
    {
        string value = Guard.AgainstControlCharacters(
            Guard.AgainstNullOrWhiteSpace(name, nameof(name)),
            nameof(name));
        return value.Length > 64
            ? throw new ArgumentOutOfRangeException(nameof(name), "The task name cannot exceed 64 characters.")
            : value;
    }
}

/// <summary>维护单实例任务名称和数量约束。</summary>
public sealed class ScheduledTaskSet
{
    private readonly List<ScheduledTask> _tasks = [];

    /// <summary>初始化任务集合。</summary>
    public ScheduledTaskSet(Guid instanceId, int maximumTasks = 32)
    {
        InstanceId = Guard.AgainstEmpty(instanceId, nameof(instanceId));
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumTasks, 1);

        MaximumTasks = maximumTasks;
    }

    /// <summary>获取实例标识。</summary>
    public Guid InstanceId { get; }

    /// <summary>获取数量上限。</summary>
    public int MaximumTasks { get; }

    /// <summary>获取任务只读快照。</summary>
    public IReadOnlyList<ScheduledTask> Tasks => _tasks.AsReadOnly();

    /// <summary>添加任务。</summary>
    public void Add(ScheduledTask task)
    {
        ArgumentNullException.ThrowIfNull(task);
        if (task.InstanceId != InstanceId)
        {
            throw new ArgumentException("The task belongs to another instance.", nameof(task));
        }

        if (_tasks.Count >= MaximumTasks)
        {
            throw new DomainException("schedule-limit-reached", "The per-instance scheduled task limit has been reached.");
        }

        if (_tasks.Any(existing => string.Equals(existing.Name, task.Name, StringComparison.OrdinalIgnoreCase)))
        {
            throw new DomainException("schedule-name-taken", "The scheduled task name is already registered for this instance.");
        }

        _tasks.Add(task);
    }
}

/// <summary>表示计划任务运行历史。</summary>
/// <param name="Id">运行标识。</param>
/// <param name="TaskId">任务标识。</param>
/// <param name="StartedAt">开始时间。</param>
/// <param name="CompletedAt">完成时间。</param>
/// <param name="Succeeded">是否成功。</param>
/// <param name="FailureCode">失败错误码。</param>
public sealed record TaskRun(Guid Id, Guid TaskId, DateTimeOffset StartedAt, DateTimeOffset? CompletedAt, bool Succeeded, string? FailureCode);
