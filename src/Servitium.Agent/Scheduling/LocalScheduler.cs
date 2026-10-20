using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;

using Servitium.Agent.Instances;
using Servitium.Agent.Lifecycle;
using Servitium.Agent.Hosting;
using Servitium.Agent.Storage;
using Servitium.Application.Abstractions;
using Servitium.Contracts.Scheduling;
using Servitium.Contracts.Events;
using Servitium.Domain.Primitives;
using Servitium.Infrastructure.Persistence;

namespace Servitium.Agent.Scheduling;

internal sealed class LocalScheduler(
    IDbContextFactory<AgentDbContext> contextFactory,
    SqliteWriteCoordinator writeCoordinator,
    InstanceRegistry instances,
    InstanceLifecycleService lifecycle,
    IClock clock,
    IGuidFactory guidFactory,
    AgentNodeConfigurationStore? configuration = null) : BackgroundService
{
    private readonly ConcurrentDictionary<Guid, ExecutionState> _executionStates = new();

    internal async Task<IReadOnlyList<ScheduledTaskDetailsDto>> ListAsync(Guid instanceId, CancellationToken cancellationToken)
    {
        _ = await RequireInstanceAsync(instanceId, cancellationToken).ConfigureAwait(false);
        using AgentDbContext context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        LocalScheduledTaskRow[] rows = await context.ScheduledTasks.AsNoTracking()
            .Where(row => row.InstanceId == instanceId)
            .OrderBy(row => row.Name)
            .ToArrayAsync(cancellationToken)
            .ConfigureAwait(false);
        return [.. rows.Select(ToDetails)];
    }

    internal async Task<IReadOnlyList<ScheduledTaskDetailsDto>> ListAllAsync(CancellationToken cancellationToken)
    {
        using AgentDbContext context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        LocalScheduledTaskRow[] rows = await context.ScheduledTasks.AsNoTracking()
            .OrderBy(row => row.InstanceId)
            .ThenBy(row => row.Name)
            .ToArrayAsync(cancellationToken)
            .ConfigureAwait(false);
        return [.. rows.Select(ToDetails)];
    }

    internal async Task<ScheduledTaskDetailsDto> GetAsync(Guid instanceId, Guid taskId, CancellationToken cancellationToken)
    {
        _ = await RequireInstanceAsync(instanceId, cancellationToken).ConfigureAwait(false);
        using AgentDbContext context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        LocalScheduledTaskRow row = await context.ScheduledTasks.AsNoTracking()
            .SingleOrDefaultAsync(candidate => candidate.Id == taskId && candidate.InstanceId == instanceId, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new DomainException("not-found", "The scheduled task does not exist.");
        return ToDetails(row);
    }

    internal async Task<ScheduledTaskDetailsDto> UpsertAsync(
        Guid instanceId,
        Guid taskId,
        UpsertScheduledTaskRequest request,
        CancellationToken cancellationToken)
    {
        _ = await RequireInstanceAsync(instanceId, cancellationToken).ConfigureAwait(false);
        Validate(request);
        DateTimeOffset now = clock.UtcNow;
        return await writeCoordinator.ExecuteAsync(
            async () =>
            {
                using AgentDbContext context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
                LocalScheduledTaskRow? row = await context.ScheduledTasks.SingleOrDefaultAsync(candidate => candidate.Id == taskId, cancellationToken).ConfigureAwait(false);
                if (row is null)
                {
                    int count = await context.ScheduledTasks.CountAsync(candidate => candidate.InstanceId == instanceId, cancellationToken).ConfigureAwait(false);
                    int maximumSchedules = configuration?.Current.MaximumSchedulesPerInstance ?? 32;
                    if (count >= maximumSchedules)
                    {
                        throw new DomainException("quota-exceeded", "The instance already has the maximum number of scheduled tasks.");
                    }

                    row = new LocalScheduledTaskRow
                    {
                        Id = taskId,
                        InstanceId = instanceId,
                        CreatedAt = now,
                        Revision = 1,
                    };
                    _ = context.ScheduledTasks.Add(row);
                }
                else
                {
                    if (row.InstanceId != instanceId || request.Revision is null || row.Revision != request.Revision.Value)
                    {
                        throw new DomainException("concurrency-conflict", "The scheduled task changed after it was read.");
                    }

                    row.Revision++;
                }

                bool duplicate = await context.ScheduledTasks.AnyAsync(
                    candidate => candidate.InstanceId == instanceId && candidate.Id != taskId && candidate.Name == request.Name.Trim(),
                    cancellationToken).ConfigureAwait(false);
                if (duplicate)
                {
                    throw new DomainException("conflict", "A scheduled task with the same name already exists.");
                }

                row.Name = request.Name.Trim();
                row.DefinitionJson = JsonSerializer.Serialize(request);
                row.Enabled = request.Enabled;
                row.RemainingRuns = request.RemainingRuns;
                row.OverlapPolicy = request.OverlapPolicy;
                row.NextRunAt = request.Enabled ? TriggerCalculator.Next(request.Trigger, now, TimeZoneInfo.Local) : null;
                row.UpdatedAt = now;
                _ = await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                return ToDetails(row);
            },
            cancellationToken).ConfigureAwait(false);
    }

    internal Task DeleteAsync(Guid instanceId, Guid taskId, CancellationToken cancellationToken)
    {
        return writeCoordinator.ExecuteAsync(
            async () =>
            {
                using AgentDbContext context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
                int deleted = await context.ScheduledTasks.Where(row => row.Id == taskId && row.InstanceId == instanceId)
                    .ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
                if (deleted == 0)
                {
                    throw new DomainException("not-found", "The scheduled task does not exist.");
                }

                if (_executionStates.TryGetValue(taskId, out ExecutionState? state))
                {
                    lock (state)
                    {
                        state.Queued = 0;
                        state.Deleted = true;
                    }
                }
            },
            cancellationToken);
    }

    internal Task<ScheduledTaskDetailsDto> SetEnabledAsync(Guid instanceId, Guid taskId, bool enabled, CancellationToken cancellationToken)
    {
        return writeCoordinator.ExecuteAsync(
            async () =>
            {
                using AgentDbContext context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
                LocalScheduledTaskRow row = await context.ScheduledTasks.SingleOrDefaultAsync(
                    candidate => candidate.Id == taskId && candidate.InstanceId == instanceId,
                    cancellationToken).ConfigureAwait(false)
                    ?? throw new DomainException("not-found", "The scheduled task does not exist.");
                UpsertScheduledTaskRequest definition = Deserialize(row);
                row.Enabled = enabled;
                row.NextRunAt = enabled ? TriggerCalculator.Next(definition.Trigger, clock.UtcNow, TimeZoneInfo.Local) : null;
                row.UpdatedAt = clock.UtcNow;
                row.Revision++;
                row.DefinitionJson = JsonSerializer.Serialize(definition with { Enabled = enabled, Revision = row.Revision });
                _ = await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                return ToDetails(row);
            },
            cancellationToken);
    }

    internal async Task<ScheduledTaskRunDto> RunNowAsync(Guid instanceId, Guid taskId, CancellationToken cancellationToken)
    {
        _ = await GetAsync(instanceId, taskId, cancellationToken).ConfigureAwait(false);
        return await ExecuteAndRecordAsync(taskId, manual: true, cancellationToken).ConfigureAwait(false);
    }

    internal async Task<IReadOnlyList<ScheduledTaskRunDto>> ListRunsAsync(Guid instanceId, Guid taskId, CancellationToken cancellationToken)
    {
        _ = await GetAsync(instanceId, taskId, cancellationToken).ConfigureAwait(false);
        using AgentDbContext context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        LocalScheduledTaskRunRow[] rows = await context.ScheduledTaskRuns.AsNoTracking()
            .Where(row => row.TaskId == taskId)
            .OrderByDescending(row => row.Sequence)
            .Take(100)
            .ToArrayAsync(cancellationToken)
            .ConfigureAwait(false);
        return [.. rows.Select(ToRun)];
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using PeriodicTimer timer = new(TimeSpan.FromSeconds(1));
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            LocalScheduledTaskRow[] due;
            using (AgentDbContext context = await contextFactory.CreateDbContextAsync(stoppingToken).ConfigureAwait(false))
            {
                DateTimeOffset now = clock.UtcNow;
                LocalScheduledTaskRow[] enabled = await context.ScheduledTasks.AsNoTracking()
                    .Where(row => row.Enabled && row.NextRunAt != null)
                    .ToArrayAsync(stoppingToken)
                    .ConfigureAwait(false);
                due = [.. enabled
                    .Where(row => row.NextRunAt <= now)
                    .OrderBy(row => row.NextRunAt)
                    .Take(32)];
            }

            foreach (LocalScheduledTaskRow task in due)
            {
                await ClaimNextAsync(task.Id, stoppingToken).ConfigureAwait(false);
                ScheduleExecution(task.Id, task.OverlapPolicy, stoppingToken);
            }
        }
    }

    internal void ScheduleExecution(Guid taskId, string overlapPolicy, CancellationToken cancellationToken)
    {
        ExecutionState state = _executionStates.GetOrAdd(taskId, _ => new ExecutionState());
        lock (state)
        {
            if (string.Equals(overlapPolicy, "concurrent", StringComparison.OrdinalIgnoreCase))
            {
                state.Running++;
                _ = RunScheduledAsync(taskId, state, cancellationToken);
                return;
            }

            if (state.Running == 0)
            {
                state.Running = 1;
                _ = RunScheduledAsync(taskId, state, cancellationToken);
                return;
            }

            if (string.Equals(overlapPolicy, "queue", StringComparison.OrdinalIgnoreCase))
            {
                if (state.Queued == 3)
                {
                    _ = RecordSkippedAsync(taskId, "overlap-queue-dropped", cancellationToken);
                }
                else
                {
                    state.Queued++;
                }
            }
            else
            {
                _ = RecordSkippedAsync(taskId, "overlap-skipped", cancellationToken);
            }
        }
    }

    private async Task RunScheduledAsync(Guid taskId, ExecutionState state, CancellationToken cancellationToken)
    {
        try
        {
            _ = await ExecuteAndRecordAsync(taskId, manual: false, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (DomainException)
        {
        }
        finally
        {
            bool runQueued = false;
            lock (state)
            {
                state.Running--;
                if (!state.Deleted && state.Queued > 0)
                {
                    state.Queued--;
                    state.Running++;
                    runQueued = true;
                }
            }

            if (runQueued)
            {
                _ = RunScheduledAsync(taskId, state, cancellationToken);
            }
            else if (state.Running == 0 && state.Queued == 0)
            {
                _ = _executionStates.TryRemove(new KeyValuePair<Guid, ExecutionState>(taskId, state));
            }
        }
    }

    private async Task<ScheduledTaskRunDto> ExecuteAndRecordAsync(Guid taskId, bool manual, CancellationToken cancellationToken)
    {
        LocalScheduledTaskRow row;
        using (AgentDbContext context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false))
        {
            row = await context.ScheduledTasks.AsNoTracking().SingleOrDefaultAsync(candidate => candidate.Id == taskId, cancellationToken).ConfigureAwait(false)
                ?? throw new DomainException("not-found", "The scheduled task no longer exists.");
        }

        UpsertScheduledTaskRequest definition = Deserialize(row);
        DateTimeOffset startedAt = clock.UtcNow;
        List<string> actionResults = [];
        string outcome = manual ? "manual-succeeded" : "scheduled-succeeded";
        bool succeeded = true;
        foreach (ScheduleActionDto action in definition.Actions)
        {
            try
            {
                await ExecuteActionAsync(row.InstanceId, action, cancellationToken).ConfigureAwait(false);
                actionResults.Add(action.Kind + ":succeeded");
            }
            catch (DomainException exception)
            {
                actionResults.Add(action.Kind + ":" + exception.Code);
                succeeded = false;
                outcome = exception.Code;
                if (!action.ContinueOnFailure)
                {
                    break;
                }
            }
        }

        LocalScheduledTaskRunRow run = new()
        {
            Id = guidFactory.Create(),
            TaskId = taskId,
            StartedAt = startedAt,
            CompletedAt = clock.UtcNow,
            Succeeded = succeeded,
            OutcomeCode = outcome,
            ActionResultsJson = JsonSerializer.Serialize(actionResults),
        };
        await PersistRunAsync(run, cancellationToken).ConfigureAwait(false);
        return ToRun(run);
    }

    private async Task ExecuteActionAsync(Guid instanceId, ScheduleActionDto action, CancellationToken cancellationToken)
    {
        switch (action.Kind.ToUpperInvariant())
        {
            case "DELAY":
                if (!int.TryParse(action.Argument, out int seconds) || seconds is < 1 or > 86400)
                {
                    throw new DomainException("validation-failed", "The scheduled delay is invalid.");
                }

                await Task.Delay(TimeSpan.FromSeconds(seconds), cancellationToken).ConfigureAwait(false);
                break;
            case "SEND-CONSOLE-COMMAND":
                _ = await lifecycle.SendCommandAsync(instanceId, action.Argument ?? string.Empty, cancellationToken).ConfigureAwait(false);
                break;
            case "START":
                _ = await lifecycle.StartInstanceAsync(instanceId, cancellationToken).ConfigureAwait(false);
                break;
            case "STOP":
                LocalInstanceSnapshot instance = await RequireInstanceAsync(instanceId, cancellationToken).ConfigureAwait(false);
                _ = await lifecycle.StopInstanceAsync(instanceId, instance.RuntimeSettings.StopCommand, instance.RuntimeSettings.StopTimeoutSeconds, cancellationToken).ConfigureAwait(false);
                break;
            case "RESTART":
                _ = await lifecycle.RestartInstanceAsync(instanceId, cancellationToken).ConfigureAwait(false);
                break;
            case "FORCE-STOP":
                _ = await lifecycle.ForceStopInstanceAsync(instanceId, cancellationToken).ConfigureAwait(false);
                break;
            default:
                throw new DomainException("validation-failed", "The scheduled action type is invalid.");
        }
    }

    private Task ClaimNextAsync(Guid taskId, CancellationToken cancellationToken)
    {
        return writeCoordinator.ExecuteAsync(
            async () =>
            {
                using AgentDbContext context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
                LocalScheduledTaskRow? row = await context.ScheduledTasks.SingleOrDefaultAsync(candidate => candidate.Id == taskId, cancellationToken).ConfigureAwait(false);
                if (row is null || !row.Enabled || row.NextRunAt > clock.UtcNow)
                {
                    return;
                }

                UpsertScheduledTaskRequest definition = Deserialize(row);
                if (row.RemainingRuns > 0)
                {
                    row.RemainingRuns--;
                }

                bool exhausted = row.RemainingRuns == 0 || string.Equals(definition.Trigger.Kind, "specified", StringComparison.OrdinalIgnoreCase);
                row.Enabled = !exhausted;
                DateTimeOffset previousTrigger = row.NextRunAt ?? clock.UtcNow;
                DateTimeOffset basis = string.Equals(definition.Trigger.Kind, "interval", StringComparison.OrdinalIgnoreCase)
                    ? previousTrigger
                    : clock.UtcNow;
                row.NextRunAt = exhausted ? null : TriggerCalculator.Next(definition.Trigger, basis, TimeZoneInfo.Local);
                row.UpdatedAt = clock.UtcNow;
                row.Revision++;
                _ = await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            },
            cancellationToken);
    }

    private Task PersistRunAsync(LocalScheduledTaskRunRow run, CancellationToken cancellationToken)
    {
        return writeCoordinator.ExecuteAsync(
            async () =>
            {
                using AgentDbContext context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
                Guid instanceId = await context.ScheduledTasks.AsNoTracking()
                    .Where(row => row.Id == run.TaskId)
                    .Select(row => row.InstanceId)
                    .SingleOrDefaultAsync(cancellationToken)
                    .ConfigureAwait(false);
                if (instanceId == Guid.Empty)
                {
                    throw new DomainException("not-found", "The scheduled task no longer exists.");
                }

                _ = context.ScheduledTaskRuns.Add(run);
                _ = await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                long[] excess = await context.ScheduledTaskRuns.AsNoTracking()
                    .Where(row => row.TaskId == run.TaskId)
                    .OrderByDescending(row => row.Sequence)
                    .Skip(100)
                    .Select(row => row.Sequence)
                    .ToArrayAsync(cancellationToken)
                    .ConfigureAwait(false);
                if (excess.Length > 0)
                {
                    _ = await context.ScheduledTaskRuns.Where(row => excess.Contains(row.Sequence)).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
                }

                _ = context.OutboxEvents.Add(new LocalOutboxEventRow
                {
                    Type = EventTypes.ScheduleRunCompleted,
                    Scope = "instance:" + instanceId.ToString("D"),
                    PayloadJson = JsonSerializer.Serialize(new ScheduleRunCompletedPayload(instanceId, run.TaskId, run.Id, run.Succeeded, run.Succeeded ? null : run.OutcomeCode)),
                    Critical = false,
                    OccurredAt = run.CompletedAt ?? clock.UtcNow,
                });
                _ = await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            },
            cancellationToken);
    }

    private async Task RecordSkippedAsync(Guid taskId, string outcomeCode, CancellationToken cancellationToken)
    {
        LocalScheduledTaskRunRow run = new()
        {
            Id = guidFactory.Create(),
            TaskId = taskId,
            StartedAt = clock.UtcNow,
            CompletedAt = clock.UtcNow,
            Succeeded = false,
            OutcomeCode = outcomeCode,
            ActionResultsJson = "[]",
        };
        await PersistRunAsync(run, cancellationToken).ConfigureAwait(false);
    }

    private async Task<LocalInstanceSnapshot> RequireInstanceAsync(Guid instanceId, CancellationToken cancellationToken)
    {
        return await instances.FindAsync(instanceId, cancellationToken).ConfigureAwait(false)
            ?? throw new DomainException("not-found", "The instance registration does not exist.");
    }

    private static ScheduledTaskDetailsDto ToDetails(LocalScheduledTaskRow row)
    {
        UpsertScheduledTaskRequest definition = Deserialize(row);
        return new(row.Id, row.InstanceId, row.Name, definition.Trigger, definition.Actions, row.RemainingRuns, row.OverlapPolicy, row.Enabled, row.Revision, row.NextRunAt);
    }

    private static ScheduledTaskRunDto ToRun(LocalScheduledTaskRunRow row)
    {
        return new(row.Id, row.TaskId, row.StartedAt, row.CompletedAt, row.Succeeded, row.OutcomeCode, JsonSerializer.Deserialize<string[]>(row.ActionResultsJson) ?? []);
    }

    private static UpsertScheduledTaskRequest Deserialize(LocalScheduledTaskRow row)
    {
        return JsonSerializer.Deserialize<UpsertScheduledTaskRequest>(row.DefinitionJson)
            ?? throw new InvalidOperationException("The persisted scheduled task definition is invalid.");
    }

    private void Validate(UpsertScheduledTaskRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Name) || request.Name.Trim().Length > 64 || request.Name.Any(char.IsControl) ||
            request.Actions.Count is < 1 or > 10 || request.RemainingRuns is 0 or < -1 ||
            request.OverlapPolicy is not ("skip" or "queue" or "concurrent"))
        {
            throw new DomainException("validation-failed", "The scheduled task definition is invalid.");
        }

        _ = TriggerCalculator.Next(request.Trigger, clock.UtcNow, TimeZoneInfo.Local);
        foreach (ScheduleActionDto action in request.Actions)
        {
            if (action.Kind is not ("delay" or "send-console-command" or "start" or "stop" or "restart" or "force-stop"))
            {
                throw new DomainException("validation-failed", "The scheduled task contains an invalid action.");
            }

            if (action.Kind == "delay" &&
                (!int.TryParse(action.Argument, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int seconds) || seconds is < 1 or > 86400))
            {
                throw new DomainException("validation-failed", "The scheduled delay is invalid.");
            }

            if (action.Kind == "send-console-command" &&
                (string.IsNullOrWhiteSpace(action.Argument) || action.Argument.Length > 4096 || action.Argument.Any(character => character is '\r' or '\n' || char.IsControl(character))))
            {
                throw new DomainException("validation-failed", "The scheduled console command is invalid.");
            }

            if (action.Kind is not ("delay" or "send-console-command") && !string.IsNullOrEmpty(action.Argument))
            {
                throw new DomainException("validation-failed", "The scheduled lifecycle action cannot have an argument.");
            }
        }
    }

    private sealed class ExecutionState
    {
        internal int Running { get; set; }
        internal int Queued { get; set; }
        internal bool Deleted { get; set; }
    }
}

internal static class TriggerCalculator
{
    internal static DateTimeOffset? Next(ScheduleTriggerDto trigger, DateTimeOffset afterUtc, TimeZoneInfo timeZone)
    {
        ArgumentNullException.ThrowIfNull(trigger);
        ArgumentNullException.ThrowIfNull(timeZone);
        return trigger.Kind.ToUpperInvariant() switch
        {
            "INTERVAL" => trigger.IntervalSeconds is >= 10 and <= 31_536_000
                ? afterUtc.AddSeconds(trigger.IntervalSeconds.Value)
                : throw new DomainException("validation-failed", "The interval trigger is invalid."),
            "SPECIFIED" => trigger.SpecifiedAt is { } specified && specified.ToUniversalTime() > afterUtc
                ? specified.ToUniversalTime()
                : null,
            "CALENDAR" => NextCalendar(trigger, afterUtc, timeZone),
            _ => throw new DomainException("validation-failed", "The trigger kind is invalid."),
        };
    }

    private static DateTimeOffset NextCalendar(ScheduleTriggerDto trigger, DateTimeOffset afterUtc, TimeZoneInfo timeZone)
    {
        if (trigger.LocalTime is null || trigger.DaysOfWeek is null || trigger.DaysOfWeek.Count == 0 || trigger.DaysOfWeek.Any(day => day is < 0 or > 6))
        {
            throw new DomainException("validation-failed", "The calendar trigger is invalid.");
        }

        DateTime localNow = TimeZoneInfo.ConvertTime(afterUtc, timeZone).DateTime;
        HashSet<DayOfWeek> days = [.. trigger.DaysOfWeek.Select(day => (DayOfWeek)day)];
        for (int offset = 0; offset <= 14; offset++)
        {
            DateTime date = localNow.Date.AddDays(offset);
            if (!days.Contains(date.DayOfWeek))
            {
                continue;
            }

            DateTime localCandidate = date.Add(trigger.LocalTime.Value.ToTimeSpan());
            if (localCandidate <= localNow || timeZone.IsInvalidTime(localCandidate))
            {
                continue;
            }

            if (timeZone.IsAmbiguousTime(localCandidate))
            {
                TimeSpan offsetValue = timeZone.GetAmbiguousTimeOffsets(localCandidate).Max();
                return new DateTimeOffset(localCandidate, offsetValue).ToUniversalTime();
            }

            return TimeZoneInfo.ConvertTimeToUtc(localCandidate, timeZone);
        }

        throw new DomainException("validation-failed", "No calendar trigger could be calculated.");
    }
}
