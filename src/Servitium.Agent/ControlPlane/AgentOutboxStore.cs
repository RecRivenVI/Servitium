using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.EntityFrameworkCore;

using Servitium.Agent.Storage;
using Servitium.Application.Abstractions;
using Servitium.Infrastructure.Persistence;

namespace Servitium.Agent.ControlPlane;

/// <summary>持久化 Agent 自治事件，并在 Server 确认后删除。</summary>
internal sealed class AgentOutboxStore(
    IDbContextFactory<AgentDbContext> contextFactory,
    SqliteWriteCoordinator writeCoordinator,
    IClock clock)
{
    private readonly ConcurrentDictionary<long, TaskCompletionSource> _acknowledgements = new();

    internal Task EnqueueAsync<TPayload>(
        string type,
        string scope,
        TPayload payload,
        bool critical,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(type);
        ArgumentException.ThrowIfNullOrWhiteSpace(scope);
        return writeCoordinator.ExecuteAsync(
            async () =>
            {
                using AgentDbContext context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
                _ = context.OutboxEvents.Add(new LocalOutboxEventRow
                {
                    Type = type,
                    Scope = scope,
                    PayloadJson = JsonSerializer.Serialize(payload),
                    Critical = critical,
                    OccurredAt = clock.UtcNow,
                });
                _ = await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            },
            cancellationToken);
    }

    internal async Task<IReadOnlyList<AgentOutboxItem>> ReadAsync(int limit, CancellationToken cancellationToken)
    {
        int bounded = Math.Clamp(limit, 1, 100);
        using AgentDbContext context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        LocalOutboxEventRow[] rows = await context.OutboxEvents.AsNoTracking()
            .OrderBy(row => row.Sequence)
            .Take(bounded)
            .ToArrayAsync(cancellationToken)
            .ConfigureAwait(false);
        return [.. rows.Select(row => new AgentOutboxItem(row.Sequence, row.Type, row.Scope, row.PayloadJson, row.Critical, row.OccurredAt))];
    }

    internal async Task WaitForAcknowledgementAsync(long sequence, CancellationToken cancellationToken)
    {
        TaskCompletionSource source = _acknowledgements.GetOrAdd(sequence, _ => new(TaskCreationOptions.RunContinuationsAsynchronously));
        try
        {
            await source.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            _ = _acknowledgements.TryRemove(new KeyValuePair<long, TaskCompletionSource>(sequence, source));
            throw;
        }
    }

    internal async Task AcknowledgeAsync(long sequence, CancellationToken cancellationToken)
    {
        if (sequence <= 0)
        {
            return;
        }

        await writeCoordinator.ExecuteAsync(
            async () =>
            {
                using AgentDbContext context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
                _ = await context.OutboxEvents.Where(row => row.Sequence <= sequence).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
            },
            cancellationToken).ConfigureAwait(false);

        foreach ((long key, TaskCompletionSource source) in _acknowledgements.Where(item => item.Key <= sequence))
        {
            if (_acknowledgements.TryRemove(new KeyValuePair<long, TaskCompletionSource>(key, source)))
            {
                _ = source.TrySetResult();
            }
        }
    }
}

internal sealed record AgentOutboxItem(
    long Sequence,
    string Type,
    string Scope,
    string PayloadJson,
    bool Critical,
    DateTimeOffset OccurredAt);
