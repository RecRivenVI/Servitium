using System;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.EntityFrameworkCore;

using Servitium.Application.Abstractions;

namespace Servitium.Infrastructure.Persistence;

/// <summary>保存首次请求结果，并在 24 小时窗口内维持同一幂等键的不可变响应。</summary>
internal sealed class SqliteIdempotencyStore(
    IDbContextFactory<ServitiumDbContext> contextFactory,
    SqliteWriteCoordinator writeQueue,
    IClock clock) : IIdempotencyStore
{
    public async Task<IdempotencyRecord?> FindAsync(string key, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        using ServitiumDbContext context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        IdempotencyRow? row = await context.IdempotencyRecords.AsNoTracking()
            .SingleOrDefaultAsync(item => item.Key == key, cancellationToken).ConfigureAwait(false);
        return row is null || row.ExpiresAt <= clock.UtcNow
            ? null
            : new IdempotencyRecord(row.Key, row.RequestHash, row.Response, row.ExpiresAt);
    }

    public Task StoreAsync(IdempotencyRecord record, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(record);
        return writeQueue.ExecuteAsync(
            async () =>
            {
                using ServitiumDbContext context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
                IdempotencyRow? existing = await context.IdempotencyRecords
                    .SingleOrDefaultAsync(item => item.Key == record.Key, cancellationToken).ConfigureAwait(false);
                if (existing is not null && existing.ExpiresAt > clock.UtcNow)
                {
                    return;
                }

                if (existing is null)
                {
                    _ = context.IdempotencyRecords.Add(new IdempotencyRow
                    {
                        Key = record.Key,
                        RequestHash = record.RequestHash,
                        Response = record.Response.ToArray(),
                        ExpiresAt = record.ExpiresAt,
                    });
                }
                else
                {
                    existing.RequestHash = record.RequestHash;
                    existing.Response = record.Response.ToArray();
                    existing.ExpiresAt = record.ExpiresAt;
                }

                _ = await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            },
            cancellationToken);
    }
}
