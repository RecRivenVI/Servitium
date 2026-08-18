using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.EntityFrameworkCore;

using Servitium.Application.Abstractions;
using Servitium.Domain.Auditing;
using Servitium.Infrastructure.Persistence;

namespace Servitium.Infrastructure.Auditing;

/// <summary>SQLite 只追加操作日志与安全审计存储。</summary>
public sealed class SqliteLogStore(
    IDbContextFactory<ServitiumDbContext> contextFactory,
    SqliteWriteCoordinator writeCoordinator) : IActivityLogger, IAuditLogger, ILogReader
{
    /// <inheritdoc />
    public Task AppendAsync(ActivityLogEntry entry, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return writeCoordinator.ExecuteAsync(
            async () =>
            {
                using ServitiumDbContext context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
                _ = context.ActivityLogs.Add(new ActivityLogRow
                {
                    Id = entry.Id,
                    ActorId = entry.ActorId,
                    ActorNameSnapshot = entry.ActorNameSnapshot,
                    Action = entry.Action,
                    Resource = entry.Resource,
                    OccurredAt = entry.OccurredAt.ToUniversalTime(),
                    OccurredAtUnixMilliseconds = entry.OccurredAt.ToUnixTimeMilliseconds(),
                    PropertiesJson = JsonSerializer.Serialize(entry.Properties),
                    OutcomeCode = entry.OutcomeCode,
                    SourceAddress = entry.SourceAddress,
                });
                _ = await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            },
            cancellationToken);
    }

    /// <inheritdoc />
    public Task AppendAsync(AuditLogEntry entry, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return writeCoordinator.ExecuteAsync(
            async () =>
            {
                using ServitiumDbContext context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
                _ = context.AuditLogs.Add(new AuditLogRow
                {
                    Id = entry.Id,
                    ActorId = entry.ActorId,
                    ActorNameSnapshot = entry.ActorNameSnapshot,
                    Category = entry.Category.ToString(),
                    Action = entry.Action,
                    Resource = entry.Resource,
                    OccurredAt = entry.OccurredAt.ToUniversalTime(),
                    OccurredAtUnixMilliseconds = entry.OccurredAt.ToUnixTimeMilliseconds(),
                    OutcomeCode = entry.OutcomeCode,
                    PropertiesJson = JsonSerializer.Serialize(entry.Properties),
                });
                _ = await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            },
            cancellationToken);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ActivityLogEntry>> ListActivitiesAsync(int limit, CancellationToken cancellationToken)
    {
        int boundedLimit = Math.Clamp(limit, 1, 1000);
        using ServitiumDbContext context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        ActivityLogRow[] rows = await context.ActivityLogs.AsNoTracking()
            .OrderByDescending(row => row.Sequence)
            .Take(boundedLimit)
            .ToArrayAsync(cancellationToken)
            .ConfigureAwait(false);
        return [.. rows.Select(row => new ActivityLogEntry(
            row.Id,
            row.ActorId,
            row.ActorNameSnapshot,
            row.Action,
            row.Resource,
            row.OccurredAt,
            JsonSerializer.Deserialize<Dictionary<string, string>>(row.PropertiesJson) ?? new Dictionary<string, string>(StringComparer.Ordinal),
            row.OutcomeCode,
            row.SourceAddress))];
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<AuditLogEntry>> ListAuditAsync(int limit, CancellationToken cancellationToken)
    {
        int boundedLimit = Math.Clamp(limit, 1, 1000);
        using ServitiumDbContext context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        AuditLogRow[] rows = await context.AuditLogs.AsNoTracking()
            .OrderByDescending(row => row.Sequence)
            .Take(boundedLimit)
            .ToArrayAsync(cancellationToken)
            .ConfigureAwait(false);
        return [.. rows.Select(row => new AuditLogEntry(
            row.Id,
            row.ActorId,
            row.ActorNameSnapshot,
            Enum.Parse<AuditCategory>(row.Category, ignoreCase: false),
            row.Action,
            row.Resource,
            row.OccurredAt,
            row.OutcomeCode,
            JsonSerializer.Deserialize<Dictionary<string, string>>(row.PropertiesJson) ?? new Dictionary<string, string>(StringComparer.Ordinal)))];
    }

    /// <summary>按稳定过滤条件和序号游标查询操作日志。</summary>
    public async Task<ActivityLogPage> QueryActivitiesAsync(LogQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        int limit = Math.Clamp(query.Limit, 1, 1000);
        using ServitiumDbContext context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        IQueryable<ActivityLogRow> rows = context.ActivityLogs.AsNoTracking();
        if (query.Cursor is { } cursor)
        {
            rows = rows.Where(row => row.Sequence < cursor);
        }
        if (!string.IsNullOrWhiteSpace(query.Actor))
        {
            rows = rows.Where(row => row.ActorNameSnapshot.Contains(query.Actor));
        }
        if (!string.IsNullOrWhiteSpace(query.Action))
        {
            rows = rows.Where(row => row.Action.Contains(query.Action));
        }
        if (!string.IsNullOrWhiteSpace(query.Resource))
        {
            rows = rows.Where(row => row.Resource.Contains(query.Resource));
        }
        if (!string.IsNullOrWhiteSpace(query.Outcome))
        {
            rows = rows.Where(row => row.OutcomeCode == query.Outcome);
        }
        if (query.From is { } from)
        {
            long fromUnix = from.ToUnixTimeMilliseconds();
            rows = rows.Where(row => row.OccurredAtUnixMilliseconds >= fromUnix);
        }
        if (query.To is { } to)
        {
            long toUnix = to.ToUnixTimeMilliseconds();
            rows = rows.Where(row => row.OccurredAtUnixMilliseconds <= toUnix);
        }

        ActivityLogRow[] page = await rows.OrderByDescending(row => row.Sequence).Take(limit + 1).ToArrayAsync(cancellationToken).ConfigureAwait(false);
        bool more = page.Length > limit;
        ActivityLogRow[] items = [.. page.Take(limit)];
        return new ActivityLogPage([.. items.Select(row => new SequencedActivityLog(row.Sequence, ToActivity(row)))], more ? items[^1].Sequence : null);
    }

    /// <summary>按稳定过滤条件和序号游标查询安全审计。</summary>
    public async Task<AuditLogPage> QueryAuditAsync(AuditLogQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        int limit = Math.Clamp(query.Limit, 1, 1000);
        using ServitiumDbContext context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        IQueryable<AuditLogRow> rows = context.AuditLogs.AsNoTracking();
        if (query.Cursor is { } cursor)
        {
            rows = rows.Where(row => row.Sequence < cursor);
        }
        if (!string.IsNullOrWhiteSpace(query.Actor))
        {
            rows = rows.Where(row => row.ActorNameSnapshot.Contains(query.Actor));
        }
        if (!string.IsNullOrWhiteSpace(query.Action))
        {
            rows = rows.Where(row => row.Action.Contains(query.Action));
        }
        if (!string.IsNullOrWhiteSpace(query.Resource))
        {
            rows = rows.Where(row => row.Resource.Contains(query.Resource));
        }
        if (!string.IsNullOrWhiteSpace(query.Outcome))
        {
            rows = rows.Where(row => row.OutcomeCode == query.Outcome);
        }
        if (!string.IsNullOrWhiteSpace(query.Category))
        {
            rows = rows.Where(row => row.Category == query.Category);
        }
        if (query.From is { } from)
        {
            long fromUnix = from.ToUnixTimeMilliseconds();
            rows = rows.Where(row => row.OccurredAtUnixMilliseconds >= fromUnix);
        }
        if (query.To is { } to)
        {
            long toUnix = to.ToUnixTimeMilliseconds();
            rows = rows.Where(row => row.OccurredAtUnixMilliseconds <= toUnix);
        }

        AuditLogRow[] page = await rows.OrderByDescending(row => row.Sequence).Take(limit + 1).ToArrayAsync(cancellationToken).ConfigureAwait(false);
        bool more = page.Length > limit;
        AuditLogRow[] items = [.. page.Take(limit)];
        return new AuditLogPage([.. items.Select(row => new SequencedAuditLog(row.Sequence, ToAudit(row)))], more ? items[^1].Sequence : null);
    }

    private static ActivityLogEntry ToActivity(ActivityLogRow row)
    {
        return new ActivityLogEntry(
            row.Id,
            row.ActorId,
            row.ActorNameSnapshot,
            row.Action,
            row.Resource,
            row.OccurredAt,
            JsonSerializer.Deserialize<Dictionary<string, string>>(row.PropertiesJson) ?? new Dictionary<string, string>(StringComparer.Ordinal),
            row.OutcomeCode,
            row.SourceAddress);
    }

    private static AuditLogEntry ToAudit(AuditLogRow row)
    {
        return new AuditLogEntry(
            row.Id,
            row.ActorId,
            row.ActorNameSnapshot,
            Enum.Parse<AuditCategory>(row.Category, ignoreCase: false),
            row.Action,
            row.Resource,
            row.OccurredAt,
            row.OutcomeCode,
            JsonSerializer.Deserialize<Dictionary<string, string>>(row.PropertiesJson) ?? new Dictionary<string, string>(StringComparer.Ordinal));
    }
}

/// <summary>操作日志查询。</summary>
public sealed record LogQuery(string? Actor, string? Action, string? Resource, string? Outcome, DateTimeOffset? From, DateTimeOffset? To, long? Cursor, int Limit);

/// <summary>安全审计查询。</summary>
public sealed record AuditLogQuery(string? Actor, string? Category, string? Action, string? Resource, string? Outcome, DateTimeOffset? From, DateTimeOffset? To, long? Cursor, int Limit);

/// <summary>带内部序号的操作日志。</summary>
public sealed record SequencedActivityLog(long Sequence, ActivityLogEntry Entry);

/// <summary>带内部序号的安全审计。</summary>
public sealed record SequencedAuditLog(long Sequence, AuditLogEntry Entry);

/// <summary>操作日志查询页。</summary>
public sealed record ActivityLogPage(IReadOnlyList<SequencedActivityLog> Items, long? NextCursor);

/// <summary>安全审计查询页。</summary>
public sealed record AuditLogPage(IReadOnlyList<SequencedAuditLog> Items, long? NextCursor);
