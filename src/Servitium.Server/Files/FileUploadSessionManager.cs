using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.Extensions.Hosting;

using Servitium.Application.Abstractions;
using Servitium.Application.Files;
using Servitium.Contracts.Files;
using Servitium.Domain.Primitives;
using Servitium.Server.Hosting;

namespace Servitium.Server.Files;

internal sealed class FileUploadSessionManager(
    IUploadSessionStore store,
    ServerHostOptions options,
    IClock clock)
{
    private const long MaximumUploadBytes = 8L * 1024 * 1024 * 1024;
    private readonly string _root = Path.Combine(options.DataRoot, "staging", "file-uploads");

    internal async Task<UploadSessionDto> CreateAsync(
        Guid instanceId,
        Guid nodeId,
        CreateUploadSessionRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateFileName(request.FileName);
        ValidateRelativePath(request.DestinationPath);
        if (request.TotalBytes is < 0 or > MaximumUploadBytes || request.PartSizeBytes is < 256 * 1024 or > 8 * 1024 * 1024)
        {
            throw new DomainException("validation-failed", "The upload size or part size is invalid.");
        }

        int partCount = request.TotalBytes == 0 ? 1 : checked((int)((request.TotalBytes + request.PartSizeBytes - 1) / request.PartSizeBytes));
        if (partCount > 10_000)
        {
            throw new DomainException("file-too-large", "The upload requires too many parts.");
        }

        _ = Directory.CreateDirectory(_root);
        DriveInfo drive = new(Path.GetPathRoot(_root) ?? throw new DomainException("instance-path-invalid", "The upload staging volume is unavailable."));
        if (drive.AvailableFreeSpace < request.TotalBytes)
        {
            throw new DomainException("insufficient-disk-space", "The control plane staging volume does not have enough free space.");
        }

        DateTimeOffset now = clock.UtcNow;
        StoredUploadSession session = new(
            Guid.NewGuid(),
            instanceId,
            nodeId,
            request.FileName,
            request.DestinationPath,
            request.TotalBytes,
            request.PartSizeBytes,
            partCount,
            [],
            request.Overwrite,
            now,
            now.AddHours(24));
        string directory = SessionDirectory(session.Id);
        _ = Directory.CreateDirectory(directory);
        try
        {
            await store.AddAsync(session, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            Directory.Delete(directory, recursive: true);
            throw;
        }

        return ToDto(session);
    }

    internal async Task<UploadSessionDto> GetAsync(Guid id, Guid instanceId, Guid nodeId, CancellationToken cancellationToken)
    {
        return ToDto(await RequireAsync(id, instanceId, nodeId, cancellationToken).ConfigureAwait(false));
    }

    internal async Task<(StoredUploadSession Session, string Path)> GetReceivedPartAsync(
        Guid id,
        Guid instanceId,
        Guid nodeId,
        int index,
        CancellationToken cancellationToken)
    {
        StoredUploadSession session = await RequireAsync(id, instanceId, nodeId, cancellationToken).ConfigureAwait(false);
        string path = PartPath(id, index);
        return !session.ReceivedParts.Contains(index) || !File.Exists(path)
            ? throw new DomainException("upload-incomplete", "The requested upload part is not available.")
            : ((StoredUploadSession Session, string Path))(session, path);
    }

    internal async Task<UploadSessionDto> SavePartAsync(
        Guid id,
        Guid instanceId,
        Guid nodeId,
        int index,
        string sha256,
        Stream source,
        CancellationToken cancellationToken)
    {
        StoredUploadSession session = await RequireAsync(id, instanceId, nodeId, cancellationToken).ConfigureAwait(false);
        ValidateHash(sha256);
        if (index < 0 || index >= session.PartCount)
        {
            throw new DomainException("validation-failed", "The upload part index is invalid.");
        }

        long expected = index == session.PartCount - 1
            ? session.TotalBytes - ((long)index * session.PartSizeBytes)
            : session.PartSizeBytes;
        string target = PartPath(id, index);
        if (File.Exists(target))
        {
            string existingHash = await HashFileAsync(target, cancellationToken).ConfigureAwait(false);
            return !FixedHashEquals(existingHash, sha256)
                ? throw new DomainException("conflict", "The upload part already exists with different content.")
                : ToDto(await store.MarkPartReceivedAsync(id, index, cancellationToken).ConfigureAwait(false));
        }

        string temporary = target + ".new-" + Guid.NewGuid().ToString("N");
        try
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            using FileStream destination = new(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 256 * 1024, FileOptions.Asynchronous | FileOptions.WriteThrough);
            byte[] buffer = new byte[256 * 1024];
            long written = 0;
            while (true)
            {
                int read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                written += read;
                if (written > expected)
                {
                    throw new DomainException("file-too-large", "The upload part exceeds its declared size.");
                }

                hash.AppendData(buffer, 0, read);
                await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            }

            if (written != expected)
            {
                throw new DomainException("validation-failed", "The upload part size does not match the session.");
            }

            string actualHash = Convert.ToHexString(hash.GetHashAndReset());
            if (!FixedHashEquals(actualHash, sha256))
            {
                throw new DomainException("checksum-mismatch", "The upload part checksum does not match.");
            }

            await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
            await destination.DisposeAsync().ConfigureAwait(false);
            try
            {
                File.Move(temporary, target, overwrite: false);
            }
            catch (IOException) when (File.Exists(target))
            {
                // 同一分块可能由断线重试并发到达；仅在已落盘内容完全一致时接受幂等重试。
                string existingHash = await HashFileAsync(target, cancellationToken).ConfigureAwait(false);
                if (!FixedHashEquals(existingHash, sha256))
                {
                    throw new DomainException("conflict", "The upload part already exists with different content.");
                }
            }
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }

        return ToDto(await store.MarkPartReceivedAsync(id, index, cancellationToken).ConfigureAwait(false));
    }

    internal async Task<(StoredUploadSession Session, string Path)> AssembleAsync(
        Guid id,
        Guid instanceId,
        Guid nodeId,
        string sha256,
        CancellationToken cancellationToken)
    {
        StoredUploadSession session = await RequireAsync(id, instanceId, nodeId, cancellationToken).ConfigureAwait(false);
        ValidateHash(sha256);
        if (session.ReceivedParts.Count != session.PartCount || !Enumerable.Range(0, session.PartCount).SequenceEqual(session.ReceivedParts.Order()))
        {
            throw new DomainException("upload-incomplete", "Not all upload parts have been received.");
        }

        string assembled = Path.Combine(SessionDirectory(id), "assembled.upload");
        string temporary = assembled + ".new";
        try
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            using FileStream destination = new(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 256 * 1024, FileOptions.Asynchronous | FileOptions.WriteThrough);
            long total = 0;
            for (int index = 0; index < session.PartCount; index++)
            {
                string part = PartPath(id, index);
                if (!File.Exists(part))
                {
                    throw new DomainException("upload-incomplete", "An upload part is missing from staging.");
                }

                using FileStream input = new(part, FileMode.Open, FileAccess.Read, FileShare.Read, 256 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
                byte[] buffer = new byte[256 * 1024];
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

            if (total != session.TotalBytes || !FixedHashEquals(Convert.ToHexString(hash.GetHashAndReset()), sha256))
            {
                throw new DomainException("checksum-mismatch", "The assembled upload does not match its declared size or checksum.");
            }

            await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
            await destination.DisposeAsync().ConfigureAwait(false);
            File.Move(temporary, assembled, overwrite: true);
            return (session, assembled);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    internal async Task CancelAsync(Guid id, Guid instanceId, Guid nodeId, CancellationToken cancellationToken)
    {
        _ = await RequireAsync(id, instanceId, nodeId, cancellationToken).ConfigureAwait(false);
        DeleteSessionDirectory(id);
        await store.DeleteAsync(id, cancellationToken).ConfigureAwait(false);
    }

    internal async Task CleanupExpiredAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<StoredUploadSession> expired = await store.ListExpiredAsync(clock.UtcNow, cancellationToken).ConfigureAwait(false);
        foreach (StoredUploadSession session in expired)
        {
            DeleteSessionDirectory(session.Id);
            await store.DeleteAsync(session.Id, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<StoredUploadSession> RequireAsync(Guid id, Guid instanceId, Guid nodeId, CancellationToken cancellationToken)
    {
        StoredUploadSession session = await store.FindAsync(id, cancellationToken).ConfigureAwait(false)
            ?? throw new DomainException("upload-session-expired", "The upload session does not exist.");
        return session.InstanceId != instanceId || session.NodeId != nodeId || session.ExpiresAt <= clock.UtcNow
            ? throw new DomainException("upload-session-expired", "The upload session is expired or belongs to another resource.")
            : session;
    }

    private string SessionDirectory(Guid id)
    {
        return Path.Combine(_root, id.ToString("N"));
    }

    private string PartPath(Guid id, int index)
    {
        return Path.Combine(SessionDirectory(id), index.ToString("D6", CultureInfo.InvariantCulture) + ".part");
    }

    private void DeleteSessionDirectory(Guid id)
    {
        string directory = Path.GetFullPath(SessionDirectory(id));
        string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(_root));
        if (!directory.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Refusing to clean an upload path outside the staging root.");
        }

        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static void ValidateFileName(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 255 || value != Path.GetFileName(value) || value.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            throw new DomainException("validation-failed", "The upload file name is invalid.");
        }
    }

    private static void ValidateRelativePath(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 1024 || Path.IsPathFullyQualified(value) || value.Any(char.IsControl) ||
            value.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries).Any(segment => segment is "." or ".."))
        {
            throw new DomainException("path-escapes-root", "The upload destination must be a safe relative path.");
        }
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
        using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read, 256 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        byte[] hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexString(hash);
    }

    private static bool FixedHashEquals(string left, string right)
    {
        return left.Length == right.Length && CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(left), Encoding.ASCII.GetBytes(right.ToUpperInvariant()));
    }

    private static UploadSessionDto ToDto(StoredUploadSession session)
    {
        return new(
        session.Id,
        session.FileName,
        session.DestinationPath,
        session.TotalBytes,
        session.PartSizeBytes,
        session.PartCount,
        session.ReceivedParts,
        session.ExpiresAt,
        session.Overwrite);
    }
}

internal sealed class UploadSessionCleanupService(FileUploadSessionManager uploads) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await uploads.CleanupExpiredAsync(stoppingToken).ConfigureAwait(false);
        using PeriodicTimer timer = new(TimeSpan.FromHours(1));
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            await uploads.CleanupExpiredAsync(stoppingToken).ConfigureAwait(false);
        }
    }
}
