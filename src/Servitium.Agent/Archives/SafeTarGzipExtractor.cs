using System;
using System.Collections.Generic;
using System.Formats.Tar;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Servitium.Agent.Paths;
using Servitium.Domain.Primitives;

namespace Servitium.Agent.Archives;

/// <summary>先完整检查再提取的 gzip 压缩 TAR 归档处理器。</summary>
internal sealed class SafeTarGzipExtractor(ArchiveSafetyLimits limits)
{
    internal async Task<SafeArchivePlan> InspectAsync(string archivePath, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(archivePath);
        FileInfo compressed = new(archivePath);
        if (!compressed.Exists || compressed.Length == 0)
        {
            throw new DomainException("archive-unsafe", "The TAR gzip archive is missing or empty.");
        }

        List<SafeArchiveEntry> entries = [];
        HashSet<string> paths = new(StringComparer.OrdinalIgnoreCase);
        long total = 0;
        int entryCount = 0;
        FileStream file = new(archivePath, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using (file.ConfigureAwait(false))
        {
            GZipStream gzip = new(file, CompressionMode.Decompress, leaveOpen: false);
            await using (gzip.ConfigureAwait(false))
            {
                using TarReader reader = new(gzip, leaveOpen: false);
                while (await reader.GetNextEntryAsync(copyData: false, cancellationToken).ConfigureAwait(false) is { } entry)
                {
                    if (++entryCount > limits.MaximumEntries)
                    {
                        throw new DomainException("archive-too-large", "The archive contains too many entries.");
                    }

                    bool directory = entry.EntryType == TarEntryType.Directory;
                    if (!directory && entry.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile))
                    {
                        throw new DomainException("archive-unsafe", "Archive links and special-file entries are not allowed.");
                    }

                    string relative = NormalizeEntryPath(entry.Name);
                    if (!paths.Add(relative))
                    {
                        throw new DomainException("archive-unsafe", "The archive contains colliding entry paths.");
                    }

                    int depth = relative.Count(character => character == '/') + 1;
                    if (depth > limits.MaximumDepth)
                    {
                        throw new DomainException("archive-unsafe", "The archive exceeds the directory depth limit.");
                    }

                    long expanded = directory ? 0 : entry.Length;
                    try
                    {
                        total = checked(total + expanded);
                    }
                    catch (OverflowException)
                    {
                        throw new DomainException("archive-too-large", "The archive expanded size overflows the supported range.");
                    }

                    if (total > limits.MaximumExpandedBytes)
                    {
                        throw new DomainException("archive-too-large", "The archive exceeds the expanded size limit.");
                    }

                    entries.Add(new(relative, expanded, directory));
                }
            }
        }

        return total > 0 && (double)total / compressed.Length > limits.MaximumCompressionRatio
            ? throw new DomainException("archive-unsafe", "The archive exceeds the compression ratio limit.")
            : new(entries, total);
    }

    internal async Task<SafeArchivePlan> ExtractToNewDirectoryAsync(string archivePath, string targetDirectory, CancellationToken cancellationToken)
    {
        SafeArchivePlan plan = await InspectAsync(archivePath, cancellationToken).ConfigureAwait(false);
        if (Directory.Exists(targetDirectory) || File.Exists(targetDirectory))
        {
            throw new DomainException("conflict", "The extraction target already exists.");
        }

        _ = Directory.CreateDirectory(targetDirectory);
        try
        {
            WindowsPathPolicy.EnsureNoReparsePoint(targetDirectory);
            FileStream file = new(archivePath, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            await using (file.ConfigureAwait(false))
            {
                GZipStream gzip = new(file, CompressionMode.Decompress, leaveOpen: false);
                await using (gzip.ConfigureAwait(false))
                {
                    using TarReader reader = new(gzip, leaveOpen: false);
                    int index = 0;
                    while (await reader.GetNextEntryAsync(copyData: false, cancellationToken).ConfigureAwait(false) is { } entry)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (index >= plan.Entries.Count)
                        {
                            throw new DomainException("archive-unsafe", "The archive changed after inspection.");
                        }

                        SafeArchiveEntry safe = plan.Entries[index++];
                        string target = ResolveTarget(targetDirectory, safe.RelativePath);
                        if (safe.IsDirectory)
                        {
                            _ = Directory.CreateDirectory(target);
                            continue;
                        }

                        string parent = Path.GetDirectoryName(target)!;
                        _ = Directory.CreateDirectory(parent);
                        WindowsPathPolicy.EnsureNoReparsePoint(parent);
                        if (entry.DataStream is null)
                        {
                            throw new DomainException("archive-unsafe", "A regular TAR entry has no content stream.");
                        }

                        FileStream destination = new(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.Asynchronous | FileOptions.WriteThrough);
                        await using (destination.ConfigureAwait(false))
                        {
                            await entry.DataStream.CopyToAsync(destination, 64 * 1024, cancellationToken).ConfigureAwait(false);
                            await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
                            if (destination.Length != safe.ExpandedLength)
                            {
                                throw new DomainException("archive-unsafe", "A TAR entry length changed after inspection.");
                            }
                        }
                    }

                    if (index != plan.Entries.Count)
                    {
                        throw new DomainException("archive-unsafe", "The archive changed after inspection.");
                    }
                }
            }

            return plan;
        }
        catch
        {
            SafeZipExtractor.DeleteCreatedTree(targetDirectory);
            throw;
        }
    }

    private static string NormalizeEntryPath(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Any(char.IsControl) || value.StartsWith('/') || value.StartsWith('\\') ||
            Path.IsPathFullyQualified(value) || value.Contains(':', StringComparison.Ordinal))
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

    private static string ResolveTarget(string root, string relativePath)
    {
        string fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        string target = Path.GetFullPath(Path.Combine(fullRoot, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        return !target.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            ? throw new DomainException("archive-unsafe", "The archive entry escapes the extraction root.")
            : target;
    }
}
