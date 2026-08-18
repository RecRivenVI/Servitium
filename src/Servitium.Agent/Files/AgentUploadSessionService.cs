using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;

using Servitium.Agent.ControlPlane;
using Servitium.Agent.Storage;
using Servitium.Application.Abstractions;
using Servitium.Contracts.Agent;
using Servitium.Contracts.Files;
using Servitium.Domain.Common;
using Servitium.Infrastructure.Persistence;

namespace Servitium.Agent.Files;

internal sealed class AgentUploadSessionService(
    IDbContextFactory<AgentDbContext> contextFactory,
    SqliteWriteCoordinator writeCoordinator,
    AgentStorageOptions options,
    InboundFileTransferStore transfers,
    InstanceFileService files,
    IClock clock) : BackgroundService
{
    private const long MaximumUploadBytes = 8L * 1024 * 1024 * 1024;
    private readonly string _root = Path.Combine(options.StagingRoot, "file-uploads");

    internal async Task StorePartAsync(AgentStoreFileUploadPartCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ValidatePart(command);
        string inbound = transfers.Consume(command.UploadStreamId ?? string.Empty);
        try
        {
            FileInfo input = new(inbound);
            long expected = ExpectedPartSize(command.PartIndex, command.PartCount, command.PartSizeBytes, command.TotalBytes);
            if (input.Length != expected)
            {
                throw new DomainException("validation-failed", "The uploaded part size does not match the session.");
            }

            string actualHash = await HashFileAsync(inbound, cancellationToken).ConfigureAwait(false);
            if (!FixedHashEquals(actualHash, command.Sha256))
            {
                throw new DomainException("checksum-mismatch", "The uploaded part checksum does not match.");
            }

            await writeCoordinator.ExecuteAsync(async () =>
            {
                using AgentDbContext context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
                LocalUploadPartRow? existing = await context.UploadParts.SingleOrDefaultAsync(
                    row => row.UploadId == command.UploadId && row.PartIndex == command.PartIndex,
                    cancellationToken).ConfigureAwait(false);
                if (existing is not null)
                {
                    if (existing.InstanceId != command.InstanceId || existing.Size != expected || !FixedHashEquals(existing.Sha256, command.Sha256))
                    {
                        throw new DomainException("conflict", "The upload part already exists with different metadata.");
                    }

                    if (!File.Exists(existing.StoragePath) || !FixedHashEquals(await HashFileAsync(existing.StoragePath, cancellationToken).ConfigureAwait(false), command.Sha256))
                    {
                        throw new DomainException("checksum-mismatch", "The persisted upload part is unavailable or corrupt.");
                    }

                    return;
                }

                string directory = UploadDirectory(command.UploadId);
                _ = Directory.CreateDirectory(directory);
                string target = PartPath(command.UploadId, command.PartIndex);
                if (File.Exists(target))
                {
                    if (!FixedHashEquals(await HashFileAsync(target, cancellationToken).ConfigureAwait(false), command.Sha256))
                    {
                        throw new DomainException("conflict", "The staged upload part has different content.");
                    }
                }
                else
                {
                    File.Move(inbound, target, overwrite: false);
                }

                _ = context.UploadParts.Add(new LocalUploadPartRow
                {
                    UploadId = command.UploadId,
                    PartIndex = command.PartIndex,
                    InstanceId = command.InstanceId,
                    StoragePath = target,
                    Size = expected,
                    Sha256 = command.Sha256.ToUpperInvariant(),
                    ExpiresAtUnixMilliseconds = command.ExpiresAt.ToUnixTimeMilliseconds(),
                });
                _ = await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            }, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (File.Exists(inbound))
            {
                File.Delete(inbound);
            }
        }
    }

    internal async Task<FileEntryDto> CompleteAsync(AgentCompleteChunkedFileUploadCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ValidateCompletion(command);
        using AgentDbContext context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        LocalUploadPartRow[] parts = await context.UploadParts.AsNoTracking()
            .Where(row => row.UploadId == command.UploadId && row.InstanceId == command.InstanceId)
            .OrderBy(row => row.PartIndex)
            .ToArrayAsync(cancellationToken).ConfigureAwait(false);
        if (parts.Length != command.PartCount || !parts.Select(row => row.PartIndex).SequenceEqual(Enumerable.Range(0, command.PartCount)))
        {
            throw new DomainException("upload-incomplete", "Not all upload parts are available on the Agent.");
        }

        string directory = UploadDirectory(command.UploadId);
        _ = Directory.CreateDirectory(directory);
        string assembled = Path.Combine(directory, "assembled.upload");
        string temporary = assembled + ".new-" + Guid.NewGuid().ToString("N");
        try
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            FileStream destination = new(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 256 * 1024, FileOptions.Asynchronous | FileOptions.WriteThrough);
            await using (destination.ConfigureAwait(false))
            {
                long total = 0;
                byte[] buffer = new byte[256 * 1024];
                foreach (LocalUploadPartRow part in parts)
                {
                    EnsureManagedPath(part.StoragePath);
                    if (!File.Exists(part.StoragePath) || new FileInfo(part.StoragePath).Length != part.Size)
                    {
                        throw new DomainException("upload-incomplete", "A persisted upload part is missing.");
                    }

                    FileStream input = new(part.StoragePath, FileMode.Open, FileAccess.Read, FileShare.Read, 256 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
                    await using (input.ConfigureAwait(false))
                    {
                        while (true)
                        {
                            int read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                            if (read == 0)
                            {
                                break;
                            }

                            total += read;
                            hash.AppendData(buffer, 0, read);
                            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                        }
                    }
                }

                if (total != command.TotalBytes || !FixedHashEquals(Convert.ToHexString(hash.GetHashAndReset()), command.Sha256))
                {
                    throw new DomainException("checksum-mismatch", "The assembled upload does not match its declared size or checksum.");
                }

                await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            File.Move(temporary, assembled, overwrite: true);
            FileEntryDto completed = await files.CompleteUploadAsync(
                command.InstanceId,
                assembled,
                command.DestinationPath,
                command.Overwrite,
                cancellationToken).ConfigureAwait(false);
            await DeleteAsync(command.InstanceId, command.UploadId, cancellationToken).ConfigureAwait(false);
            return completed;
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    internal async Task DeleteAsync(Guid instanceId, Guid uploadId, CancellationToken cancellationToken)
    {
        await writeCoordinator.ExecuteAsync(async () =>
        {
            using AgentDbContext context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            LocalUploadPartRow[] rows = await context.UploadParts
                .Where(row => row.UploadId == uploadId && row.InstanceId == instanceId)
                .ToArrayAsync(cancellationToken).ConfigureAwait(false);
            context.UploadParts.RemoveRange(rows);
            _ = await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            DeleteUploadDirectory(uploadId);
        }, cancellationToken).ConfigureAwait(false);
    }

    internal async Task CleanupExpiredAsync(CancellationToken cancellationToken)
    {
        long now = clock.UtcNow.ToUnixTimeMilliseconds();
        using AgentDbContext context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        Guid[] expired = await context.UploadParts.AsNoTracking()
            .Where(row => row.ExpiresAtUnixMilliseconds <= now)
            .Select(row => row.UploadId)
            .Distinct()
            .ToArrayAsync(cancellationToken).ConfigureAwait(false);
        foreach (Guid uploadId in expired)
        {
            await writeCoordinator.ExecuteAsync(async () =>
            {
                using AgentDbContext writeContext = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
                LocalUploadPartRow[] rows = await writeContext.UploadParts.Where(row => row.UploadId == uploadId).ToArrayAsync(cancellationToken).ConfigureAwait(false);
                writeContext.UploadParts.RemoveRange(rows);
                _ = await writeContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                DeleteUploadDirectory(uploadId);
            }, cancellationToken).ConfigureAwait(false);
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await CleanupExpiredAsync(stoppingToken).ConfigureAwait(false);
        using PeriodicTimer timer = new(TimeSpan.FromHours(1));
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            await CleanupExpiredAsync(stoppingToken).ConfigureAwait(false);
        }
    }

    private string UploadDirectory(Guid uploadId)
    {
        return Path.Combine(_root, uploadId.ToString("N"));
    }

    private string PartPath(Guid uploadId, int partIndex)
    {
        return Path.Combine(
        UploadDirectory(uploadId),
        partIndex.ToString("D6", CultureInfo.InvariantCulture) + ".part");
    }

    private void DeleteUploadDirectory(Guid uploadId)
    {
        string directory = UploadDirectory(uploadId);
        EnsureManagedPath(directory);
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private void EnsureManagedPath(string path)
    {
        string fullPath = Path.GetFullPath(path);
        string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(_root));
        if (!fullPath.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Refusing to access an upload path outside the Agent staging root.");
        }
    }

    private static void ValidatePart(AgentStoreFileUploadPartCommand command)
    {
        if (command.UploadId == Guid.Empty || command.InstanceId == Guid.Empty || command.PartCount is < 1 or > 10_000 ||
            command.PartIndex < 0 || command.PartIndex >= command.PartCount || command.PartSizeBytes is < 256 * 1024 or > 8 * 1024 * 1024 ||
            command.TotalBytes is < 0 or > MaximumUploadBytes || command.ExpiresAt <= DateTimeOffset.UnixEpoch)
        {
            throw new DomainException("validation-failed", "The upload part metadata is invalid.");
        }

        ValidateHash(command.Sha256);
        _ = ExpectedPartSize(command.PartIndex, command.PartCount, command.PartSizeBytes, command.TotalBytes);
    }

    private static void ValidateCompletion(AgentCompleteChunkedFileUploadCommand command)
    {
        if (command.UploadId == Guid.Empty || command.InstanceId == Guid.Empty || command.PartCount is < 1 or > 10_000 ||
            command.TotalBytes is < 0 or > MaximumUploadBytes || string.IsNullOrWhiteSpace(command.DestinationPath))
        {
            throw new DomainException("validation-failed", "The upload completion metadata is invalid.");
        }

        ValidateHash(command.Sha256);
    }

    private static long ExpectedPartSize(int index, int partCount, long partSize, long totalBytes)
    {
        long expectedPartCount = totalBytes == 0 ? 1 : checked((totalBytes + partSize - 1) / partSize);
        return expectedPartCount != partCount
            ? throw new DomainException("validation-failed", "The upload part count does not match the declared size.")
            : index == partCount - 1 ? totalBytes - (index * partSize) : partSize;
    }

    private static void ValidateHash(string value)
    {
        if (value.Length != 64 || value.Any(character => !char.IsAsciiHexDigit(character)))
        {
            throw new DomainException("validation-failed", "The SHA-256 checksum is invalid.");
        }
    }

    private static async Task<string> HashFileAsync(string path, CancellationToken cancellationToken)
    {
        FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read, 256 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using (stream.ConfigureAwait(false))
        {
            return Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));
        }
    }

    private static bool FixedHashEquals(string left, string right)
    {
        return left.Length == right.Length && CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(left),
            Encoding.ASCII.GetBytes(right.ToUpperInvariant()));
    }
}
