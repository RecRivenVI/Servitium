using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Servitium.Agent.Paths;
using Servitium.Domain.Primitives;

namespace Servitium.Agent.Archives;

/// <summary>ZIP 归档安全限制。</summary>
internal sealed record ArchiveSafetyLimits(int MaximumEntries, long MaximumExpandedBytes, double MaximumCompressionRatio, int MaximumDepth)
{
    /// <summary>产品默认限制。</summary>
    public static ArchiveSafetyLimits Default { get; } = new(100_000, 8L * 1024 * 1024 * 1024, 200, 64);
}

/// <summary>已经完整预检的 ZIP 条目。</summary>
internal sealed record SafeArchiveEntry(string RelativePath, long ExpandedLength, bool IsDirectory);

/// <summary>ZIP 预检结果。</summary>
internal sealed record SafeArchivePlan(IReadOnlyList<SafeArchiveEntry> Entries, long TotalExpandedBytes);

/// <summary>先完整检查再解包的安全 ZIP 提取器。</summary>
internal sealed class SafeZipExtractor(ArchiveSafetyLimits limits)
{
    /// <summary>检查全部条目且不写入文件。</summary>
    public SafeArchivePlan Inspect(string archivePath, Encoding? entryNameEncoding = null)
    {
        ArgumentNullException.ThrowIfNull(archivePath);
        using FileStream stream = new(archivePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        using ZipArchive archive = new(stream, ZipArchiveMode.Read, leaveOpen: false, entryNameEncoding);
        if (archive.Entries.Count > limits.MaximumEntries)
        {
            throw new DomainException("archive-too-large", "The archive contains too many entries.");
        }

        List<SafeArchiveEntry> entries = new(archive.Entries.Count);
        HashSet<string> paths = new(StringComparer.OrdinalIgnoreCase);
        long total = 0;
        foreach (ZipArchiveEntry entry in archive.Entries)
        {
            string relativePath = NormalizeEntryPath(entry.FullName);
            bool directory = entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\');
            RejectLink(entry, directory);
            if (!paths.Add(relativePath))
            {
                throw new DomainException("archive-unsafe", "The archive contains colliding entry paths.");
            }

            int depth = relativePath.Count(character => character == '/') + 1;
            if (depth > limits.MaximumDepth)
            {
                throw new DomainException("archive-unsafe", "The archive exceeds the directory depth limit.");
            }

            checked
            {
                total += entry.Length;
            }

            if (total > limits.MaximumExpandedBytes)
            {
                throw new DomainException("archive-too-large", "The archive exceeds the expanded size limit.");
            }

            if (entry.Length > 0 && (entry.CompressedLength == 0 || (double)entry.Length / entry.CompressedLength > limits.MaximumCompressionRatio))
            {
                throw new DomainException("archive-unsafe", "An archive entry exceeds the compression ratio limit.");
            }

            entries.Add(new(relativePath, entry.Length, directory));
        }

        return new(entries, total);
    }

    /// <summary>把归档解包到由本次操作独占的新目录，失败时完整回滚该目录。</summary>
    public async Task<SafeArchivePlan> ExtractToNewDirectoryAsync(
        string archivePath,
        string targetDirectory,
        CancellationToken cancellationToken,
        Encoding? entryNameEncoding = null,
        Func<int, int, CancellationToken, Task>? progress = null)
    {
        SafeArchivePlan plan = Inspect(archivePath, entryNameEncoding);
        if (Directory.Exists(targetDirectory) || File.Exists(targetDirectory))
        {
            throw new DomainException("conflict", "The extraction target already exists.");
        }

        _ = Directory.CreateDirectory(targetDirectory);
        try
        {
            WindowsPathPolicy.EnsureNoReparsePoint(targetDirectory);
            FileStream archiveStream = new(archivePath, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            await using (archiveStream.ConfigureAwait(false))
            using (ZipArchive archive = new(archiveStream, ZipArchiveMode.Read, leaveOpen: true, entryNameEncoding))
            {
                for (int index = 0; index < archive.Entries.Count; index++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    ZipArchiveEntry entry = archive.Entries[index];
                    SafeArchiveEntry safeEntry = plan.Entries[index];
                    string target = ResolveTarget(targetDirectory, safeEntry.RelativePath);
                    if (safeEntry.IsDirectory)
                    {
                        _ = Directory.CreateDirectory(target);
                        if (progress is not null)
                        {
                            await progress(index + 1, archive.Entries.Count, cancellationToken).ConfigureAwait(false);
                        }
                        continue;
                    }

                    string? parent = Path.GetDirectoryName(target);
                    if (!string.IsNullOrEmpty(parent))
                    {
                        _ = Directory.CreateDirectory(parent);
                        WindowsPathPolicy.EnsureNoReparsePoint(parent);
                    }

                    Stream source = await entry.OpenAsync(cancellationToken).ConfigureAwait(false);
                    await using (source.ConfigureAwait(false))
                    {
                        FileStream destination = new(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.Asynchronous | FileOptions.WriteThrough);
                        await using (destination.ConfigureAwait(false))
                        {
                            await source.CopyToAsync(destination, 64 * 1024, cancellationToken).ConfigureAwait(false);
                            await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
                        }
                    }

                    if (progress is not null)
                    {
                        await progress(index + 1, archive.Entries.Count, cancellationToken).ConfigureAwait(false);
                    }
                }
            }

            if (plan.Entries.Count == 0 && progress is not null)
            {
                await progress(0, 0, cancellationToken).ConfigureAwait(false);
            }

            return plan;
        }
        catch
        {
            DeleteCreatedTree(targetDirectory);
            throw;
        }
    }

    private static string NormalizeEntryPath(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Any(char.IsControl) ||
            value.StartsWith('/') || value.StartsWith('\\') || Path.IsPathFullyQualified(value) || value.Contains(':', StringComparison.Ordinal))
        {
            throw new DomainException("archive-unsafe", "The archive contains an absolute or device path.");
        }

        string[] segments = value.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0 || segments.Any(segment => segment is "." or ".."))
        {
            throw new DomainException("archive-unsafe", "The archive contains a path traversal entry.");
        }

        foreach (string segment in segments)
        {
            WindowsPathPolicy.ValidateFileName(segment);
        }

        return string.Join('/', segments);
    }

    private static void RejectLink(ZipArchiveEntry entry, bool directory)
    {
        int unixFileType = (entry.ExternalAttributes >> 16) & 0xf000;
        if (unixFileType != 0 && unixFileType != 0x8000 && !(directory && unixFileType == 0x4000))
        {
            throw new DomainException("archive-unsafe", "Archive link and special-file entries are not allowed.");
        }
    }

    private static string ResolveTarget(string root, string relativePath)
    {
        string fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        string target = Path.GetFullPath(Path.Combine(fullRoot, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        return !target.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            ? throw new DomainException("archive-unsafe", "The archive entry escapes the extraction root.")
            : target;
    }

    internal static void DeleteCreatedTree(string root)
    {
        if (!Directory.Exists(root))
        {
            return;
        }

        DeleteDirectoryContents(root);
        Directory.Delete(root);
    }

    private static void DeleteDirectoryContents(string directory)
    {
        WindowsPathPolicy.EnsureNoReparsePoint(directory);
        foreach (string file in Directory.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly))
        {
            if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0)
            {
                throw new DomainException("path-reparse-point", "Rollback encountered an unexpected reparse point.");
            }

            File.Delete(file);
        }

        foreach (string child in Directory.EnumerateDirectories(directory, "*", SearchOption.TopDirectoryOnly))
        {
            if ((File.GetAttributes(child) & FileAttributes.ReparsePoint) != 0)
            {
                throw new DomainException("path-reparse-point", "Rollback encountered an unexpected reparse point.");
            }

            DeleteDirectoryContents(child);
            Directory.Delete(child);
        }
    }
}
