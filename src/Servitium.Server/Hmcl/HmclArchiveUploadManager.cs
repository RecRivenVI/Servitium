using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Servitium.Application.Abstractions;
using Servitium.Contracts.Distribution;
using Servitium.Domain.Primitives;
using Servitium.Infrastructure.Distribution;
using Servitium.Server.Hosting;

namespace Servitium.Server.Hmcl;

/// <summary>持久、可续传且逐块校验的 HMCL Archive 上传管理器。</summary>
internal sealed class HmclArchiveUploadManager(
    HmclDistributionStore store,
    ServerHostOptions options,
    IClock clock,
    IGuidFactory guids)
{
    private const long MaximumArchiveBytes = 20L * 1024 * 1024 * 1024;
    private readonly string _root = Path.Combine(options.DataRoot, "staging", "hmcl-archive-sessions");

    internal async Task<HmclArchiveDto> CreateAsync(Guid userId, CreateHmclArchiveRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateFileName(request.FileName);
        if (request.TotalBytes is <= 0 or > MaximumArchiveBytes || request.PartSizeBytes is < 256 * 1024 or > 8 * 1024 * 1024)
        {
            throw new DomainException("validation-failed", "The HMCL archive size or part size is invalid.");
        }

        int partCount = checked((int)((request.TotalBytes + request.PartSizeBytes - 1) / request.PartSizeBytes));
        if (partCount > 10_000)
        {
            throw new DomainException("file-too-large", "The HMCL archive requires too many upload parts.");
        }

        _ = Directory.CreateDirectory(_root);
        DriveInfo drive = new(Path.GetPathRoot(_root) ?? throw new DomainException("insufficient-disk-space", "The staging volume is unavailable."));
        if (drive.AvailableFreeSpace < request.TotalBytes)
        {
            throw new DomainException("insufficient-disk-space", "The staging volume does not have enough free space.");
        }

        DateTimeOffset now = clock.UtcNow;
        StoredHmclArchive archive = new(
            guids.Create(), request.FileName, request.TotalBytes, request.PartSizeBytes, partCount,
            [], null, "uploading", userId, now, now.AddHours(24));
        string directory = SessionDirectory(archive.Id);
        _ = Directory.CreateDirectory(directory);
        try
        {
            return ToDto(await store.CreateArchiveAsync(archive, cancellationToken).ConfigureAwait(false));
        }
        catch
        {
            Directory.Delete(directory);
            throw;
        }
    }

    internal async Task<HmclArchiveDto> GetAsync(Guid id, Guid userId, CancellationToken cancellationToken)
    {
        return ToDto(await RequireAsync(id, userId, cancellationToken).ConfigureAwait(false));
    }

    internal async Task<HmclArchiveDto> SavePartAsync(
        Guid id,
        Guid userId,
        int index,
        string sha256,
        Stream source,
        CancellationToken cancellationToken)
    {
        StoredHmclArchive archive = await RequireAsync(id, userId, cancellationToken).ConfigureAwait(false);
        ValidateHash(sha256);
        if (archive.Status != "uploading" || index < 0 || index >= archive.PartCount)
        {
            throw new DomainException("validation-failed", "The archive part index is invalid.");
        }

        long expected = index == archive.PartCount - 1
            ? archive.TotalBytes - ((long)index * archive.PartSizeBytes)
            : archive.PartSizeBytes;
        string target = PartPath(id, index);
        if (File.Exists(target))
        {
            string existing = await HashFileAsync(target, cancellationToken).ConfigureAwait(false);
            return !FixedHashEquals(existing, sha256)
                ? throw new DomainException("conflict", "The archive part already exists with different content.")
                : ToDto(await store.MarkArchivePartAsync(id, index, cancellationToken).ConfigureAwait(false));
        }

        string temporary = target + ".new-" + Guid.NewGuid().ToString("N");
        try
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            FileStream destination = new(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 256 * 1024, FileOptions.Asynchronous | FileOptions.WriteThrough);
            await using (destination.ConfigureAwait(false))
            {
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
                        throw new DomainException("file-too-large", "The archive part exceeds its declared size.");
                    }

                    hash.AppendData(buffer, 0, read);
                    await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                }

                if (written != expected || !FixedHashEquals(Convert.ToHexString(hash.GetHashAndReset()), sha256))
                {
                    throw new DomainException("checksum-mismatch", "The archive part size or checksum is invalid.");
                }

                await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            File.Move(temporary, target, overwrite: false);
            return ToDto(await store.MarkArchivePartAsync(id, index, cancellationToken).ConfigureAwait(false));
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    internal async Task<HmclArchiveDto> CompleteAsync(Guid id, Guid userId, string sha256, CancellationToken cancellationToken)
    {
        StoredHmclArchive archive = await RequireAsync(id, userId, cancellationToken).ConfigureAwait(false);
        ValidateHash(sha256);
        if (archive.Status == "ready")
        {
            return !FixedHashEquals(archive.Sha256!, sha256)
                ? throw new DomainException("conflict", "The completed archive has a different checksum.")
                : ToDto(archive);
        }

        if (archive.ReceivedParts.Count != archive.PartCount)
        {
            throw new DomainException("upload-incomplete", "Not all archive parts have been received.");
        }

        string assembled = AssembledPath(id);
        string temporary = assembled + ".new";
        try
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            FileStream destination = new(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 256 * 1024, FileOptions.Asynchronous | FileOptions.WriteThrough);
            await using (destination.ConfigureAwait(false))
            {
                long total = 0;
                for (int index = 0; index < archive.PartCount; index++)
                {
                    string part = PartPath(id, index);
                    if (!File.Exists(part))
                    {
                        throw new DomainException("upload-incomplete", "An archive part is missing from staging.");
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

                if (total != archive.TotalBytes || !FixedHashEquals(Convert.ToHexString(hash.GetHashAndReset()), sha256))
                {
                    throw new DomainException("checksum-mismatch", "The assembled archive size or checksum is invalid.");
                }

                await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            File.Move(temporary, assembled, overwrite: true);
            _ = HmclDistributionStore.InspectContentArchive(assembled);
            return ToDto(await store.CompleteArchiveAsync(id, sha256.ToUpperInvariant(), cancellationToken).ConfigureAwait(false));
        }
        catch
        {
            if (File.Exists(assembled))
            {
                File.Delete(assembled);
            }

            throw;
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "CA3003:Review code for file path injection vulnerabilities", Justification = "The caller supplies only a parsed Guid; AssembledPath formats that Guid below a fixed staging root and never incorporates request text.")]
    internal async Task<string> GetReadyPathAsync(Guid id, Guid userId, CancellationToken cancellationToken)
    {
        StoredHmclArchive archive = await RequireAsync(id, userId, cancellationToken).ConfigureAwait(false);
        string path = AssembledPath(id);
        return archive.Status != "ready" || !File.Exists(path) || new FileInfo(path).Length != archive.TotalBytes
            ? throw new DomainException("upload-incomplete", "The HMCL archive is not ready.")
            : path;
    }

    internal async Task DeleteAsync(Guid id, Guid userId, CancellationToken cancellationToken)
    {
        _ = await RequireAsync(id, userId, cancellationToken).ConfigureAwait(false);
        DeleteSessionDirectory(id);
        await store.DeleteArchiveAsync(id, cancellationToken).ConfigureAwait(false);
    }

    internal async Task CleanupExpiredAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<StoredHmclArchive> expired = await store.ListExpiredArchivesAsync(clock.UtcNow, cancellationToken).ConfigureAwait(false);
        foreach (StoredHmclArchive archive in expired)
        {
            DeleteSessionDirectory(archive.Id);
            await store.DeleteArchiveAsync(archive.Id, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<StoredHmclArchive> RequireAsync(Guid id, Guid userId, CancellationToken cancellationToken)
    {
        StoredHmclArchive archive = await store.GetArchiveAsync(id, cancellationToken).ConfigureAwait(false)
            ?? throw new DomainException("upload-session-expired", "The HMCL archive upload does not exist.");
        return archive.CreatedBy != userId || archive.ExpiresAt <= clock.UtcNow
            ? throw new DomainException("upload-session-expired", "The HMCL archive upload is expired or belongs to another user.")
            : archive;
    }

    private string SessionDirectory(Guid id)
    {
        return Path.Combine(_root, id.ToString("N"));
    }

    private string PartPath(Guid id, int index)
    {
        return Path.Combine(SessionDirectory(id), index.ToString("D6", CultureInfo.InvariantCulture) + ".part");
    }

    private string AssembledPath(Guid id)
    {
        return Path.Combine(SessionDirectory(id), "archive.zip");
    }

    private void DeleteSessionDirectory(Guid id)
    {
        string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(_root));
        string directory = Path.GetFullPath(SessionDirectory(id));
        if (!directory.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Refusing to clean an HMCL archive path outside the staging root.");
        }

        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static void ValidateFileName(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 255 || value != Path.GetFileName(value) ||
            value.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || !value.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            throw new DomainException("validation-failed", "The HMCL archive file name is invalid.");
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
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));
    }

    private static bool FixedHashEquals(string left, string right)
    {
        return left.Length == right.Length && CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(left.ToUpperInvariant()), Encoding.ASCII.GetBytes(right.ToUpperInvariant()));
    }

    private static HmclArchiveDto ToDto(StoredHmclArchive archive)
    {
        return new(
            archive.Id, archive.FileName, archive.TotalBytes, archive.PartSizeBytes, archive.PartCount,
            archive.ReceivedParts, archive.Status, archive.ExpiresAt);
    }
}
