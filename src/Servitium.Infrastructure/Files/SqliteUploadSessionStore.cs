using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.EntityFrameworkCore;

using Servitium.Application.Files;
using Servitium.Domain.Common;
using Servitium.Infrastructure.Persistence;

namespace Servitium.Infrastructure.Files;

internal sealed class SqliteUploadSessionStore(
    IDbContextFactory<ServitiumDbContext> contextFactory,
    SqliteWriteCoordinator writeCoordinator) : IUploadSessionStore
{
    public Task AddAsync(StoredUploadSession session, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        return writeCoordinator.ExecuteAsync(
            async () =>
            {
                using ServitiumDbContext context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
                _ = context.UploadSessions.Add(ToRow(session));
                _ = await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            },
            cancellationToken);
    }

    public async Task<StoredUploadSession?> FindAsync(Guid id, CancellationToken cancellationToken)
    {
        using ServitiumDbContext context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        UploadSessionRow? row = await context.UploadSessions.AsNoTracking().SingleOrDefaultAsync(item => item.Id == id, cancellationToken).ConfigureAwait(false);
        return row is null ? null : FromRow(row);
    }

    public Task<StoredUploadSession> MarkPartReceivedAsync(Guid id, int index, CancellationToken cancellationToken)
    {
        return writeCoordinator.ExecuteAsync(
            async () =>
            {
                using ServitiumDbContext context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
                UploadSessionRow row = await context.UploadSessions.SingleOrDefaultAsync(item => item.Id == id, cancellationToken).ConfigureAwait(false)
                    ?? throw new DomainException("upload-session-expired", "The upload session does not exist.");
                SortedSet<int> parts = [.. JsonSerializer.Deserialize<int[]>(row.ReceivedPartsJson) ?? []];
                _ = parts.Add(index);
                row.ReceivedPartsJson = JsonSerializer.Serialize(parts);
                _ = await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                return FromRow(row);
            },
            cancellationToken);
    }

    public Task DeleteAsync(Guid id, CancellationToken cancellationToken)
    {
        return writeCoordinator.ExecuteAsync(
            async () =>
            {
                using ServitiumDbContext context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
                _ = await context.UploadSessions.Where(item => item.Id == id).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
            },
            cancellationToken);
    }

    public async Task<IReadOnlyList<StoredUploadSession>> ListExpiredAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        using ServitiumDbContext context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        long cutoff = now.ToUnixTimeMilliseconds();
        UploadSessionRow[] rows = await context.UploadSessions.AsNoTracking()
            .Where(item => item.ExpiresAtUnixMilliseconds <= cutoff)
            .ToArrayAsync(cancellationToken)
            .ConfigureAwait(false);
        return [.. rows.Select(FromRow)];
    }

    private static UploadSessionRow ToRow(StoredUploadSession session)
    {
        return new()
        {
            Id = session.Id,
            InstanceId = session.InstanceId,
            NodeId = session.NodeId,
            FileName = session.FileName,
            DestinationPath = session.DestinationPath,
            TotalBytes = session.TotalBytes,
            PartSizeBytes = session.PartSizeBytes,
            PartCount = session.PartCount,
            ReceivedPartsJson = JsonSerializer.Serialize(session.ReceivedParts),
            Overwrite = session.Overwrite,
            CreatedAt = session.CreatedAt,
            ExpiresAtUnixMilliseconds = session.ExpiresAt.ToUnixTimeMilliseconds(),
        };
    }

    private static StoredUploadSession FromRow(UploadSessionRow row)
    {
        return new(
        row.Id,
        row.InstanceId,
        row.NodeId,
        row.FileName,
        row.DestinationPath,
        row.TotalBytes,
        row.PartSizeBytes,
        row.PartCount,
        JsonSerializer.Deserialize<int[]>(row.ReceivedPartsJson) ?? [],
        row.Overwrite,
        row.CreatedAt,
        DateTimeOffset.FromUnixTimeMilliseconds(row.ExpiresAtUnixMilliseconds));
    }
}
