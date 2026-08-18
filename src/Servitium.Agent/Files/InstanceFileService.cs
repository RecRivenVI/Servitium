using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using Servitium.Agent.Instances;
using Servitium.Agent.Archives;
using Servitium.Agent.Paths;
using Servitium.Application.Abstractions;
using Servitium.Contracts.Agent;
using Servitium.Contracts.Files;
using Servitium.Domain.Common;

namespace Servitium.Agent.Files;

internal sealed class InstanceFileService(
    InstanceRegistry instances,
    IWindowsPathPolicy pathPolicy,
    SafeZipExtractor zipExtractor,
    IOutboundHttpClient outboundHttp,
    AgentFileTaskCoordinator? tasks = null,
    Hosting.AgentNodeConfigurationStore? nodeConfiguration = null,
    AgentTransferRateLimiters? rateLimiters = null)
{
    private const long MaximumTextBytes = 8L * 1024 * 1024;

    internal async Task<IReadOnlyList<FileEntryDto>> ListAsync(Guid instanceId, string relativePath, CancellationToken cancellationToken)
    {
        FileEntryPageDto page = await ListAsync(instanceId, relativePath, null, 100, null, "name", false, cancellationToken).ConfigureAwait(false);
        return page.Items;
    }

    internal async Task<FileEntryPageDto> ListAsync(
        Guid instanceId,
        string relativePath,
        string? cursor,
        int pageSize,
        string? search,
        string sort,
        bool descending,
        CancellationToken cancellationToken)
    {
        if (pageSize is < 1 or > 200 || search is { Length: > 128 } || cursor is { Length: > 2048 } ||
            sort is not ("name" or "size" or "modified"))
        {
            throw new DomainException("validation-failed", "The directory query is invalid.");
        }

        LocalInstanceSnapshot instance = await RequireInstanceAsync(instanceId, cancellationToken).ConfigureAwait(false);
        string directory = Resolve(instance.WorkingDirectory, relativePath, allowRoot: true);
        EnsureExistingDirectory(directory);
        IEnumerable<FileEntryDto> entries = Directory.EnumerateFileSystemEntries(directory, "*", SearchOption.TopDirectoryOnly)
            .Select(path => ToEntry(instance.WorkingDirectory, path));
        if (!string.IsNullOrWhiteSpace(search))
        {
            entries = entries.Where(item => Path.GetFileName(item.Path).Contains(search, StringComparison.OrdinalIgnoreCase));
        }

        IEnumerable<FileEntryDto> ordered = sort switch
        {
            "size" => descending
                ? entries.OrderByDescending(item => item.Size).ThenByDescending(item => item.Path, StringComparer.OrdinalIgnoreCase)
                : entries.OrderBy(item => item.Size).ThenBy(item => item.Path, StringComparer.OrdinalIgnoreCase),
            "modified" => descending
                ? entries.OrderByDescending(item => item.ModifiedAt).ThenByDescending(item => item.Path, StringComparer.OrdinalIgnoreCase)
                : entries.OrderBy(item => item.ModifiedAt).ThenBy(item => item.Path, StringComparer.OrdinalIgnoreCase),
            _ => descending
                ? entries.OrderByDescending(item => item.Path, StringComparer.OrdinalIgnoreCase)
                : entries.OrderBy(item => item.Path, StringComparer.OrdinalIgnoreCase),
        };
        FileListCursor? position = DecodeCursor(cursor, relativePath, search, sort, descending);
        if (position is not null)
        {
            ordered = ordered.Where(item => IsAfter(item, position, sort, descending));
        }

        List<FileEntryDto> page = [.. ordered.Take(pageSize + 1)];
        string? nextCursor = page.Count > pageSize
            ? EncodeCursor(page[pageSize - 1], relativePath, search, sort, descending)
            : null;
        if (page.Count > pageSize)
        {
            page.RemoveAt(pageSize);
        }

        return new(page, nextCursor);
    }

    internal async Task<FileSystemStatusDto> GetStatusAsync(Guid instanceId, CancellationToken cancellationToken)
    {
        LocalInstanceSnapshot instance = await RequireInstanceAsync(instanceId, cancellationToken).ConfigureAwait(false);
        string root = Path.GetPathRoot(instance.WorkingDirectory)
            ?? throw new DomainException("instance-path-invalid", "The instance volume could not be resolved.");
        DriveInfo drive = new(root);
        return new(
            drive.TotalSize,
            drive.AvailableFreeSpace,
            tasks?.ActiveFileTasks ?? 0,
            nodeConfiguration?.Current.FileTaskConcurrency ?? 4,
            tasks?.ActiveArchiveTasks ?? 0,
            nodeConfiguration?.Current.CompressionTaskConcurrency ?? 2);
    }

    internal async Task<FileDownloadInfoDto> GetDownloadInfoAsync(Guid instanceId, string relativePath, CancellationToken cancellationToken)
    {
        LocalInstanceSnapshot instance = await RequireInstanceAsync(instanceId, cancellationToken).ConfigureAwait(false);
        string path = Resolve(instance.WorkingDirectory, relativePath, allowRoot: false);
        EnsureExistingFile(path);
        FileInfo info = new(path);
        return new(relativePath, info.Length, info.LastWriteTimeUtc);
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope", Justification = "The returned FileDownloadSource transfers stream ownership to the outbound transfer manager.")]
    internal async Task<FileDownloadSource> OpenDownloadAsync(
        Guid instanceId,
        string relativePath,
        long offset,
        long length,
        CancellationToken cancellationToken)
    {
        FileDownloadInfoDto info = await GetDownloadInfoAsync(instanceId, relativePath, cancellationToken).ConfigureAwait(false);
        if (offset < 0 || length < 0 || offset > info.Length || length > info.Length - offset)
        {
            throw new DomainException("range-not-satisfiable", "The requested file range is invalid.");
        }

        LocalInstanceSnapshot instance = await RequireInstanceAsync(instanceId, cancellationToken).ConfigureAwait(false);
        string path = Resolve(instance.WorkingDirectory, relativePath, allowRoot: false);
        FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 256 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan)
        {
            Position = offset
        };
        return new(stream, length);
    }

    internal async Task<FileEntryDto> CreateDirectoryAsync(Guid instanceId, string relativePath, CancellationToken cancellationToken)
    {
        LocalInstanceSnapshot instance = await RequireStoppedInstanceAsync(instanceId, cancellationToken).ConfigureAwait(false);
        string path = Resolve(instance.WorkingDirectory, relativePath, allowRoot: false);
        EnsureDestinationParent(path);
        DirectoryInfo directory = Directory.CreateDirectory(path);
        return ToEntry(instance.WorkingDirectory, directory.FullName);
    }

    internal async Task<FileEntryDto> CreateFileAsync(Guid instanceId, string relativePath, CancellationToken cancellationToken)
    {
        LocalInstanceSnapshot instance = await RequireStoppedInstanceAsync(instanceId, cancellationToken).ConfigureAwait(false);
        string path = Resolve(instance.WorkingDirectory, relativePath, allowRoot: false);
        EnsureDestinationParent(path);
        using (new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
        }

        return ToEntry(instance.WorkingDirectory, path);
    }

    internal async Task<FileEntryDto> RenameAsync(
        Guid instanceId,
        string relativePath,
        string newName,
        CancellationToken cancellationToken)
    {
        LocalInstanceSnapshot instance = await RequireStoppedInstanceAsync(instanceId, cancellationToken).ConfigureAwait(false);
        WindowsPathPolicy.ValidateFileName(newName);
        string source = Resolve(instance.WorkingDirectory, relativePath, allowRoot: false);
        bool isFile = File.Exists(source);
        if (isFile)
        {
            EnsureExistingFile(source);
        }
        else
        {
            EnsureExistingDirectory(source);
        }

        string parent = Path.GetDirectoryName(source)!;
        string destination = Resolve(
            instance.WorkingDirectory,
            Path.GetRelativePath(instance.WorkingDirectory, Path.Combine(parent, newName)),
            allowRoot: false);
        if (string.Equals(source, destination, StringComparison.Ordinal))
        {
            return ToEntry(instance.WorkingDirectory, source);
        }

        bool caseOnly = string.Equals(source, destination, StringComparison.OrdinalIgnoreCase);
        if (!caseOnly && (File.Exists(destination) || Directory.Exists(destination)))
        {
            throw new DomainException("conflict", "The rename destination already exists.");
        }

        string temporary = Path.Combine(parent, ".servitium-rename-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            if (caseOnly)
            {
                MovePath(source, temporary, isFile);
                MovePath(temporary, destination, isFile);
            }
            else
            {
                MovePath(source, destination, isFile);
            }
        }
        catch (IOException exception)
        {
            if (caseOnly && (File.Exists(temporary) || Directory.Exists(temporary)) && !File.Exists(source) && !Directory.Exists(source))
            {
                MovePath(temporary, source, isFile);
            }

            throw new DomainException("file-locked", exception.Message);
        }

        return ToEntry(instance.WorkingDirectory, destination);
    }

    internal async Task<FileEntryDto> CopyAsync(
        Guid instanceId,
        string sourcePath,
        string destinationPath,
        CancellationToken cancellationToken)
    {
        return await CopyAsync(instanceId, sourcePath, destinationPath, "reject", cancellationToken).ConfigureAwait(false);
    }

    internal async Task<FileEntryDto> CopyAsync(
        Guid instanceId,
        string sourcePath,
        string destinationPath,
        string collisionPolicy,
        CancellationToken cancellationToken)
    {
        return await CopyAsync(instanceId, sourcePath, destinationPath, collisionPolicy, null, cancellationToken).ConfigureAwait(false);
    }

    internal async Task<FileEntryDto> CopyAsync(
        Guid instanceId,
        string sourcePath,
        string destinationPath,
        string collisionPolicy,
        Func<double, CancellationToken, Task>? progress,
        CancellationToken cancellationToken)
    {
        LocalInstanceSnapshot instance = await RequireStoppedInstanceAsync(instanceId, cancellationToken).ConfigureAwait(false);
        string source = Resolve(instance.WorkingDirectory, sourcePath, allowRoot: false);
        string destination = Resolve(instance.WorkingDirectory, destinationPath, allowRoot: false);
        EnsureDistinctTransferPaths(source, destination);
        EnsureDestinationParent(destination);
        if (File.Exists(source))
        {
            EnsureExistingFile(source);
            (destination, bool process, bool overwrite) = ResolveCollision(destination, false, collisionPolicy);
            if (!process)
            {
                return ToEntry(instance.WorkingDirectory, destination);
            }

            EnsureDiskSpace(destination, new FileInfo(source).Length);
            await CopyFileAsync(source, destination, overwrite, cancellationToken).ConfigureAwait(false);
            if (progress is not null)
            {
                await progress(1d, cancellationToken).ConfigureAwait(false);
            }
        }
        else
        {
            EnsureExistingDirectory(source);
            (destination, bool process, bool overwrite) = ResolveCollision(destination, true, collisionPolicy);
            if (!process)
            {
                return ToEntry(instance.WorkingDirectory, destination);
            }

            EnsureDiskSpace(destination, CalculateDirectorySize(source, cancellationToken));
            if (overwrite)
            {
                await ReplaceDirectoryFromCopyAsync(source, destination, progress, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await CopyDirectoryAsync(source, destination, progress, cancellationToken).ConfigureAwait(false);
            }
        }

        return ToEntry(instance.WorkingDirectory, destination);
    }

    internal async Task<FileEntryDto> MoveAsync(
        Guid instanceId,
        string sourcePath,
        string destinationPath,
        CancellationToken cancellationToken)
    {
        return await MoveAsync(instanceId, sourcePath, destinationPath, "reject", cancellationToken).ConfigureAwait(false);
    }

    internal async Task<FileEntryDto> MoveAsync(
        Guid instanceId,
        string sourcePath,
        string destinationPath,
        string collisionPolicy,
        CancellationToken cancellationToken)
    {
        return await MoveAsync(instanceId, sourcePath, destinationPath, collisionPolicy, null, cancellationToken).ConfigureAwait(false);
    }

    internal async Task<FileEntryDto> MoveAsync(
        Guid instanceId,
        string sourcePath,
        string destinationPath,
        string collisionPolicy,
        Func<double, CancellationToken, Task>? progress,
        CancellationToken cancellationToken)
    {
        LocalInstanceSnapshot instance = await RequireStoppedInstanceAsync(instanceId, cancellationToken).ConfigureAwait(false);
        string source = Resolve(instance.WorkingDirectory, sourcePath, allowRoot: false);
        string destination = Resolve(instance.WorkingDirectory, destinationPath, allowRoot: false);
        EnsureDistinctTransferPaths(source, destination);
        EnsureDestinationParent(destination);
        if (File.Exists(source))
        {
            EnsureExistingFile(source);
            (destination, bool process, bool overwrite) = ResolveCollision(destination, false, collisionPolicy);
            if (!process)
            {
                return ToEntry(instance.WorkingDirectory, destination);
            }

            try
            {
                File.Move(source, destination, overwrite);
            }
            catch (IOException exception) when ((exception.HResult & 0xffff) == 17)
            {
                EnsureDiskSpace(destination, new FileInfo(source).Length);
                await CopyFileAsync(source, destination, overwrite, cancellationToken).ConfigureAwait(false);
                File.Delete(source);
            }

            if (progress is not null)
            {
                await progress(1d, cancellationToken).ConfigureAwait(false);
            }
        }
        else
        {
            EnsureExistingDirectory(source);
            (destination, bool process, bool overwrite) = ResolveCollision(destination, true, collisionPolicy);
            if (!process)
            {
                return ToEntry(instance.WorkingDirectory, destination);
            }

            if (overwrite)
            {
                EnsureDiskSpace(destination, CalculateDirectorySize(source, cancellationToken));
                await ReplaceDirectoryFromCopyAsync(source, destination, progress, cancellationToken).ConfigureAwait(false);
                DeleteDirectory(source, cancellationToken);
            }
            else
            {
                try
                {
                    Directory.Move(source, destination);
                }
                catch (IOException exception) when ((exception.HResult & 0xffff) == 17)
                {
                    EnsureDiskSpace(destination, CalculateDirectorySize(source, cancellationToken));
                    await CopyDirectoryAsync(source, destination, progress, cancellationToken).ConfigureAwait(false);
                    DeleteDirectory(source, cancellationToken);
                }

                if (progress is not null && Directory.Exists(destination))
                {
                    await progress(1d, cancellationToken).ConfigureAwait(false);
                }
            }
        }

        return ToEntry(instance.WorkingDirectory, destination);
    }

    internal async Task DeleteAsync(Guid instanceId, string relativePath, CancellationToken cancellationToken)
    {
        await DeleteAsync(instanceId, relativePath, null, cancellationToken).ConfigureAwait(false);
    }

    internal async Task DeleteAsync(
        Guid instanceId,
        string relativePath,
        Func<double, CancellationToken, Task>? progress,
        CancellationToken cancellationToken)
    {
        LocalInstanceSnapshot instance = await RequireStoppedInstanceAsync(instanceId, cancellationToken).ConfigureAwait(false);
        string path = Resolve(instance.WorkingDirectory, relativePath, allowRoot: false);
        if (File.Exists(path))
        {
            EnsureExistingFile(path);
            File.Delete(path);
            if (progress is not null)
            {
                await progress(1d, cancellationToken).ConfigureAwait(false);
            }
            return;
        }

        EnsureExistingDirectory(path);
        string[] files = [.. EnumerateSafeFiles(path)];
        int directoryCount = Directory.EnumerateDirectories(path, "*", SearchOption.AllDirectories).Count();
        int totalEntries = checked(files.Length + directoryCount + 1);
        int completedEntries = 0;
        await DeleteDirectoryWithProgressAsync(
            path,
            async token =>
            {
                completedEntries++;
                if (progress is not null)
                {
                    await progress((double)completedEntries / totalEntries, token).ConfigureAwait(false);
                }
            },
            cancellationToken).ConfigureAwait(false);
    }

    internal async Task DeleteManyAsync(Guid instanceId, IReadOnlyList<string> relativePaths, CancellationToken cancellationToken)
    {
        await DeleteManyAsync(instanceId, relativePaths, null, cancellationToken).ConfigureAwait(false);
    }

    internal async Task DeleteManyAsync(
        Guid instanceId,
        IReadOnlyList<string> relativePaths,
        Func<double, CancellationToken, Task>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(relativePaths);
        if (relativePaths.Count is < 1 or > 500)
        {
            throw new DomainException("validation-failed", "A batch delete must contain between 1 and 500 paths.");
        }

        string[] paths = [.. relativePaths.Distinct(StringComparer.OrdinalIgnoreCase)];
        for (int index = 0; index < paths.Length; index++)
        {
            int completed = index;
            await DeleteAsync(
                instanceId,
                paths[index],
                progress is null
                    ? null
                    : (value, token) => progress((completed + value) / paths.Length, token),
                cancellationToken).ConfigureAwait(false);
        }
    }

    internal async Task<FileEntryDto> CompleteUploadAsync(
        Guid instanceId,
        string stagingPath,
        string destinationPath,
        bool overwrite,
        CancellationToken cancellationToken)
    {
        LocalInstanceSnapshot instance = await RequireStoppedInstanceAsync(instanceId, cancellationToken).ConfigureAwait(false);
        string destination = Resolve(instance.WorkingDirectory, destinationPath, allowRoot: false);
        EnsureDestinationParent(destination);
        EnsureDiskSpace(destination, new FileInfo(stagingPath).Length);
        if (!overwrite && File.Exists(destination))
        {
            throw new DomainException("conflict", "The upload destination already exists.");
        }

        string temporary = destination + ".servitium-" + Guid.NewGuid().ToString("N") + ".upload";
        try
        {
            FileStream source = new(stagingPath, FileMode.Open, FileAccess.Read, FileShare.Read, 256 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            await using (source.ConfigureAwait(false))
            {
                FileStream target = new(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 256 * 1024, FileOptions.Asynchronous | FileOptions.WriteThrough);
                await using (target.ConfigureAwait(false))
                {
                    await source.CopyToAsync(target, 256 * 1024, cancellationToken).ConfigureAwait(false);
                    await target.FlushAsync(cancellationToken).ConfigureAwait(false);
                }
            }

            File.Move(temporary, destination, overwrite);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }

        return ToEntry(instance.WorkingDirectory, destination);
    }

    internal async Task<FileEntryDto> CompressAsync(
        Guid instanceId,
        IReadOnlyList<string> relativePaths,
        string destinationPath,
        string entryNameEncoding,
        CancellationToken cancellationToken)
    {
        return await CompressAsync(instanceId, relativePaths, destinationPath, entryNameEncoding, null, cancellationToken).ConfigureAwait(false);
    }

    internal async Task<FileEntryDto> CompressAsync(
        Guid instanceId,
        IReadOnlyList<string> relativePaths,
        string destinationPath,
        string entryNameEncoding,
        Func<double, CancellationToken, Task>? progress,
        CancellationToken cancellationToken)
    {
        LocalInstanceSnapshot instance = await RequireStoppedInstanceAsync(instanceId, cancellationToken).ConfigureAwait(false);
        if (relativePaths.Count is < 1 or > 500)
        {
            throw new DomainException("validation-failed", "A compression task must contain between 1 and 500 paths.");
        }

        string destination = Resolve(instance.WorkingDirectory, destinationPath, allowRoot: false);
        if (!destination.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) || File.Exists(destination))
        {
            throw new DomainException("validation-failed", "The archive destination must be a new ZIP file.");
        }

        EnsureDestinationParent(destination);
        long inputBytes = 0;
        List<(string Path, string EntryName)> archiveFiles = [];
        foreach (string relative in relativePaths.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            string source = Resolve(instance.WorkingDirectory, relative, allowRoot: false);
            if (File.Exists(source))
            {
                EnsureExistingFile(source);
                inputBytes = checked(inputBytes + new FileInfo(source).Length);
                archiveFiles.Add((source, Path.GetFileName(source)));
            }
            else
            {
                EnsureExistingDirectory(source);
                foreach (string file in EnumerateSafeFiles(source))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    inputBytes = checked(inputBytes + new FileInfo(file).Length);
                    archiveFiles.Add((file, Path.Combine(Path.GetFileName(source), Path.GetRelativePath(source, file)).Replace('\\', '/')));
                }
            }
        }

        EnsureDiskSpace(destination, checked(inputBytes + Math.Max(1024 * 1024, inputBytes / 100)));
        string temporary = destination + ".servitium-" + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (FileStream stream = new(temporary, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 64 * 1024, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                Encoding archiveEncoding = ResolveArchiveEncoding(entryNameEncoding, allowAuto: false) ?? Encoding.UTF8;
                using (ZipArchive archive = new(stream, ZipArchiveMode.Create, true, archiveEncoding))
                {
                    for (int index = 0; index < archiveFiles.Count; index++)
                    {
                        (string source, string entryName) = archiveFiles[index];
                        await AddFileToArchiveAsync(archive, source, entryName, cancellationToken).ConfigureAwait(false);
                        if (progress is not null)
                        {
                            await progress((double)(index + 1) / archiveFiles.Count, cancellationToken).ConfigureAwait(false);
                        }
                    }
                }

                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            File.Move(temporary, destination, false);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }

        return ToEntry(instance.WorkingDirectory, destination);
    }

    internal async Task<FileEntryDto> ExtractAsync(
        Guid instanceId,
        string archivePath,
        string destinationPath,
        string entryNameEncoding,
        CancellationToken cancellationToken)
    {
        return await ExtractAsync(instanceId, archivePath, destinationPath, entryNameEncoding, null, cancellationToken).ConfigureAwait(false);
    }

    internal async Task<FileEntryDto> ExtractAsync(
        Guid instanceId,
        string archivePath,
        string destinationPath,
        string entryNameEncoding,
        Func<double, CancellationToken, Task>? progress,
        CancellationToken cancellationToken)
    {
        LocalInstanceSnapshot instance = await RequireStoppedInstanceAsync(instanceId, cancellationToken).ConfigureAwait(false);
        string source = Resolve(instance.WorkingDirectory, archivePath, allowRoot: false);
        EnsureExistingFile(source);
        string destination = Resolve(instance.WorkingDirectory, destinationPath, allowRoot: false);
        if (Directory.Exists(destination) || File.Exists(destination))
        {
            throw new DomainException("conflict", "The extraction destination must not already exist.");
        }

        Encoding? archiveEncoding = ResolveArchiveEncoding(entryNameEncoding, allowAuto: true);
        SafeArchivePlan plan = zipExtractor.Inspect(source, archiveEncoding);
        EnsureDiskSpace(destination, plan.TotalExpandedBytes);
        _ = await zipExtractor.ExtractToNewDirectoryAsync(
            source,
            destination,
            cancellationToken,
            archiveEncoding,
            progress is null
                ? null
                : (completed, total, token) => progress(total == 0 ? 1d : (double)completed / total, token)).ConfigureAwait(false);
        return ToEntry(instance.WorkingDirectory, destination);
    }

    internal async Task<FileEntryDto> DownloadFromUrlAsync(
        Guid instanceId,
        Uri uri,
        string destinationPath,
        bool overwrite,
        CancellationToken cancellationToken)
    {
        return await DownloadFromUrlAsync(instanceId, uri, destinationPath, overwrite, null, cancellationToken).ConfigureAwait(false);
    }

    internal async Task<FileEntryDto> DownloadFromUrlAsync(
        Guid instanceId,
        Uri uri,
        string destinationPath,
        bool overwrite,
        Func<double, CancellationToken, Task>? progress,
        CancellationToken cancellationToken)
    {
        LocalInstanceSnapshot instance = await RequireStoppedInstanceAsync(instanceId, cancellationToken).ConfigureAwait(false);
        if (!uri.IsAbsoluteUri)
        {
            throw new DomainException("url-not-allowed", "The URL is invalid.");
        }

        string destination = Resolve(instance.WorkingDirectory, destinationPath, allowRoot: false);
        EnsureDestinationParent(destination);
        if (!overwrite && File.Exists(destination))
        {
            throw new DomainException("conflict", "The download destination already exists.");
        }

        OutboundHttpResponse response = await outboundHttp.SendAsync(
            new(uri, HttpMethod.Get.Method, MaximumResponseBytes: 8L * 1024 * 1024 * 1024),
            cancellationToken).ConfigureAwait(false);
        await using (response.Content.ConfigureAwait(false))
        {
            if (response.StatusCode is < 200 or >= 300)
            {
                throw new DomainException("download-failed", "The remote server returned a non-success status.");
            }

            if (response.ContentLength is long contentLength)
            {
                EnsureDiskSpace(destination, contentLength);
            }

            string temporary = destination + ".servitium-" + Guid.NewGuid().ToString("N") + ".download";
            try
            {
                using FileStream stream = new(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.Asynchronous | FileOptions.WriteThrough);
                byte[] buffer = new byte[256 * 1024];
                long transferred = 0;
                while (true)
                {
                    int read = await response.Content.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                    if (read == 0)
                    {
                        break;
                    }

                    if (rateLimiters is not null)
                    {
                        await rateLimiters.Upload.AcquireAsync(read, cancellationToken).ConfigureAwait(false);
                    }

                    await stream.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                    transferred = checked(transferred + read);
                    if (progress is not null && response.ContentLength is long total && total > 0)
                    {
                        await progress(Math.Min(1d, (double)transferred / total), cancellationToken).ConfigureAwait(false);
                    }
                }

                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                EnsureDiskSpace(destination, stream.Length);
                File.Move(temporary, destination, overwrite);
                if (progress is not null)
                {
                    await progress(1d, cancellationToken).ConfigureAwait(false);
                }
            }
            finally
            {
                if (File.Exists(temporary))
                {
                    File.Delete(temporary);
                }
            }
        }

        return ToEntry(instance.WorkingDirectory, destination);
    }

    internal async Task<AgentTextFileResponse> ReadTextAsync(
        Guid instanceId,
        string relativePath,
        string? requestedEncoding,
        CancellationToken cancellationToken)
    {
        LocalInstanceSnapshot instance = await RequireInstanceAsync(instanceId, cancellationToken).ConfigureAwait(false);
        string path = Resolve(instance.WorkingDirectory, relativePath, allowRoot: false);
        EnsureExistingFile(path);
        FileInfo info = new(path);
        if (info.Length > MaximumTextBytes)
        {
            throw new DomainException("file-too-large", "The text file exceeds the configured size limit.");
        }

        byte[] bytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
        Encoding encoding;
        int preambleLength;
        if (string.IsNullOrWhiteSpace(requestedEncoding))
        {
            (encoding, preambleLength) = DetectEncoding(bytes);
        }
        else
        {
            encoding = ResolveEncoding(requestedEncoding);
            preambleLength = PreambleLength(bytes, encoding);
        }
        string content;
        try
        {
            content = encoding.GetString(bytes, preambleLength, bytes.Length - preambleLength);
        }
        catch (DecoderFallbackException)
        {
            throw new DomainException("config-parse-failed", "The text file is not valid in the detected encoding.");
        }

        string lineEnding = content.Contains("\r\n", StringComparison.Ordinal)
            ? "crlf"
            : content.Contains('\n', StringComparison.Ordinal) ? "lf" : "none";
        return new(relativePath, content, encoding.WebName, lineEnding, bytes.LongLength);
    }

    internal Task<AgentTextFileResponse> ReadTextAsync(Guid instanceId, string relativePath, CancellationToken cancellationToken)
    {
        return ReadTextAsync(instanceId, relativePath, null, cancellationToken);
    }

    internal async Task<AgentTextFileResponse> WriteTextAsync(
        Guid instanceId,
        string relativePath,
        string content,
        string encoding,
        CancellationToken cancellationToken)
    {
        LocalInstanceSnapshot instance = await RequireStoppedInstanceAsync(instanceId, cancellationToken).ConfigureAwait(false);
        string path = Resolve(instance.WorkingDirectory, relativePath, allowRoot: false);
        EnsureDestinationParent(path);
        Encoding outputEncoding = ResolveEncoding(encoding);
        byte[] contentBytes = outputEncoding.GetBytes(content ?? throw new DomainException("validation-failed", "Text content is required."));
        byte[] preamble = outputEncoding.GetPreamble();
        byte[] bytes = new byte[preamble.Length + contentBytes.Length];
        preamble.CopyTo(bytes, 0);
        contentBytes.CopyTo(bytes, preamble.Length);
        if (bytes.LongLength > MaximumTextBytes)
        {
            throw new DomainException("file-too-large", "The text file exceeds the configured size limit.");
        }

        string temporary = path + ".servitium-" + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (FileStream stream = new(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }

        return await ReadTextAsync(instanceId, relativePath, encoding, cancellationToken).ConfigureAwait(false);
    }

    private static string EncodeCursor(FileEntryDto item, string relativePath, string? search, string sort, bool descending)
    {
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(new FileListCursor(
            relativePath,
            string.IsNullOrWhiteSpace(search) ? null : search,
            sort,
            descending,
            item.Path,
            item.Size,
            item.ModifiedAt.UtcDateTime.Ticks));
        return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    private static FileListCursor? DecodeCursor(string? cursor, string relativePath, string? search, string sort, bool descending)
    {
        if (string.IsNullOrWhiteSpace(cursor))
        {
            return null;
        }

        try
        {
            string value = cursor.Replace('-', '+').Replace('_', '/');
            value = value.PadRight(value.Length + ((4 - (value.Length % 4)) % 4), '=');
            FileListCursor? decoded = JsonSerializer.Deserialize<FileListCursor>(Convert.FromBase64String(value));
            string? normalizedSearch = string.IsNullOrWhiteSpace(search) ? null : search;
            return decoded is null ||
                !string.Equals(decoded.RelativePath, relativePath, StringComparison.Ordinal) ||
                !string.Equals(decoded.Search, normalizedSearch, StringComparison.Ordinal) ||
                !string.Equals(decoded.Sort, sort, StringComparison.Ordinal) ||
                decoded.Descending != descending ||
                string.IsNullOrWhiteSpace(decoded.Path)
                ? throw new FormatException()
                : decoded;
        }
        catch (Exception exception) when (exception is FormatException or JsonException)
        {
            throw new DomainException("validation-failed", "The directory cursor is invalid.");
        }
    }

    private static bool IsAfter(FileEntryDto item, FileListCursor cursor, string sort, bool descending)
    {
        int primary = sort switch
        {
            "size" => item.Size.CompareTo(cursor.Size),
            "modified" => item.ModifiedAt.UtcDateTime.Ticks.CompareTo(cursor.ModifiedUtcTicks),
            _ => StringComparer.OrdinalIgnoreCase.Compare(item.Path, cursor.Path),
        };
        int comparison = primary != 0
            ? primary
            : StringComparer.OrdinalIgnoreCase.Compare(item.Path, cursor.Path);
        return descending ? comparison < 0 : comparison > 0;
    }

    private sealed record FileListCursor(
        string RelativePath,
        string? Search,
        string Sort,
        bool Descending,
        string Path,
        long Size,
        long ModifiedUtcTicks);

    private async Task<LocalInstanceSnapshot> RequireInstanceAsync(Guid instanceId, CancellationToken cancellationToken)
    {
        LocalInstanceSnapshot instance = await instances.FindAsync(instanceId, cancellationToken).ConfigureAwait(false)
            ?? throw new DomainException("not-found", "The instance registration does not exist.");
        WindowsPathPolicy.EnsureNoReparsePoint(instance.WorkingDirectory);
        return instance;
    }

    private async Task<LocalInstanceSnapshot> RequireStoppedInstanceAsync(Guid instanceId, CancellationToken cancellationToken)
    {
        LocalInstanceSnapshot instance = await RequireInstanceAsync(instanceId, cancellationToken).ConfigureAwait(false);
        return instance.Status is not ("Stopped" or "Crashed")
            ? throw new DomainException("instance-not-stopped", "Mutating file operations require a stopped instance.")
            : instance;
    }

    private string Resolve(string root, string relativePath, bool allowRoot)
    {
        return allowRoot && string.IsNullOrWhiteSpace(relativePath) ? root : pathPolicy.ResolveRelativePath(root, relativePath);
    }

    private static void EnsureExistingFile(string path)
    {
        if (!File.Exists(path) || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        {
            throw new DomainException("not-found", "The file does not exist or is a reparse point.");
        }

        WindowsPathPolicy.EnsureNoReparsePoint(Path.GetDirectoryName(path)!);
    }

    private static void EnsureExistingDirectory(string path)
    {
        if (!Directory.Exists(path))
        {
            throw new DomainException("not-found", "The directory does not exist.");
        }

        WindowsPathPolicy.EnsureNoReparsePoint(path);
    }

    private static void EnsureDestinationParent(string path)
    {
        string? parent = Path.GetDirectoryName(path);
        if (parent is null || !Directory.Exists(parent))
        {
            throw new DomainException("not-found", "The destination parent directory does not exist.");
        }

        WindowsPathPolicy.EnsureNoReparsePoint(parent);
    }

    private static void MovePath(string source, string destination, bool isFile)
    {
        if (isFile)
        {
            File.Move(source, destination, overwrite: false);
        }
        else
        {
            Directory.Move(source, destination);
        }
    }

    private static FileEntryDto ToEntry(string root, string path)
    {
        FileAttributes attributes = File.GetAttributes(path);
        if ((attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new DomainException("path-reparse-point", "The directory contains a reparse point.");
        }

        bool directory = (attributes & FileAttributes.Directory) != 0;
        long size = directory ? 0 : new FileInfo(path).Length;
        DateTimeOffset modifiedAt = directory
            ? new DirectoryInfo(path).LastWriteTimeUtc
            : new FileInfo(path).LastWriteTimeUtc;
        return new(Path.GetRelativePath(root, path).Replace('\\', '/'), directory, size, modifiedAt);
    }

    private static async Task CopyDirectoryAsync(
        string source,
        string destination,
        Func<double, CancellationToken, Task>? progress,
        CancellationToken cancellationToken)
    {
        if (Directory.Exists(destination) || File.Exists(destination))
        {
            throw new DomainException("conflict", "The copy destination already exists.");
        }

        _ = Directory.CreateDirectory(destination);
        try
        {
            foreach (string directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
            {
                cancellationToken.ThrowIfCancellationRequested();
                WindowsPathPolicy.EnsureNoReparsePoint(directory);
                _ = Directory.CreateDirectory(Path.Combine(destination, Path.GetRelativePath(source, directory)));
            }

            string[] files = [.. EnumerateSafeFiles(source)];
            for (int index = 0; index < files.Length; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string file = files[index];
                string target = Path.Combine(destination, Path.GetRelativePath(source, file));
                _ = Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                await CopyFileAsync(file, target, false, cancellationToken).ConfigureAwait(false);
                if (progress is not null)
                {
                    await progress((double)(index + 1) / files.Length, cancellationToken).ConfigureAwait(false);
                }
            }

            if (files.Length == 0 && progress is not null)
            {
                await progress(1d, cancellationToken).ConfigureAwait(false);
            }
        }
        catch
        {
            if (Directory.Exists(destination))
            {
                DeleteDirectory(destination, CancellationToken.None);
            }

            throw;
        }
    }

    private static async Task CopyFileAsync(string source, string destination, bool overwrite, CancellationToken cancellationToken)
    {
        string temporary = destination + ".servitium-" + Guid.NewGuid().ToString("N") + ".copy";
        try
        {
            FileStream input = new(source, FileMode.Open, FileAccess.Read, FileShare.Read, 256 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            await using (input.ConfigureAwait(false))
            {
                FileStream output = new(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 256 * 1024, FileOptions.Asynchronous | FileOptions.WriteThrough);
                await using (output.ConfigureAwait(false))
                {
                    await input.CopyToAsync(output, 256 * 1024, cancellationToken).ConfigureAwait(false);
                    await output.FlushAsync(cancellationToken).ConfigureAwait(false);
                }
            }

            File.Move(temporary, destination, overwrite);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    private static (string Destination, bool Process, bool Overwrite) ResolveCollision(string destination, bool sourceIsDirectory, string policy)
    {
        if (policy is not ("reject" or "overwrite" or "keep-both" or "skip"))
        {
            throw new DomainException("validation-failed", "The file collision policy is invalid.");
        }

        bool destinationIsFile = File.Exists(destination);
        bool destinationIsDirectory = Directory.Exists(destination);
        if (!destinationIsFile && !destinationIsDirectory)
        {
            return (destination, true, false);
        }

        return sourceIsDirectory != destinationIsDirectory
            ? throw new DomainException("conflict", "The source and destination have different types.")
            : ((string Destination, bool Process, bool Overwrite))(policy switch
            {
                "overwrite" => (destination, true, true),
                "keep-both" => (FindAvailableDestination(destination, sourceIsDirectory), true, false),
                "skip" => (destination, false, false),
                _ => throw new DomainException("conflict", "The transfer destination already exists."),
            });
    }

    private static string FindAvailableDestination(string destination, bool directory)
    {
        string parent = Path.GetDirectoryName(destination)!;
        string name = Path.GetFileName(destination);
        string extension = directory ? string.Empty : Path.GetExtension(name);
        string stem = directory ? name : Path.GetFileNameWithoutExtension(name);
        for (int index = 2; index <= 10_000; index++)
        {
            string candidate = Path.Combine(parent, $"{stem} ({index}){extension}");
            if (!File.Exists(candidate) && !Directory.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new DomainException("conflict", "No available keep-both destination name could be allocated.");
    }

    private static void EnsureDistinctTransferPaths(string source, string destination)
    {
        string normalizedSource = Path.TrimEndingDirectorySeparator(Path.GetFullPath(source));
        string normalizedDestination = Path.TrimEndingDirectorySeparator(Path.GetFullPath(destination));
        if (string.Equals(normalizedSource, normalizedDestination, StringComparison.OrdinalIgnoreCase) ||
            normalizedDestination.StartsWith(normalizedSource + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
            normalizedSource.StartsWith(normalizedDestination + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            throw new DomainException("validation-failed", "A transfer destination cannot be the source or one of its descendants.");
        }
    }

    private static async Task ReplaceDirectoryFromCopyAsync(
        string source,
        string destination,
        Func<double, CancellationToken, Task>? progress,
        CancellationToken cancellationToken)
    {
        string staging = destination + ".servitium-replacement-" + Guid.NewGuid().ToString("N");
        string backup = destination + ".servitium-backup-" + Guid.NewGuid().ToString("N");
        bool originalMoved = false;
        bool replacementInstalled = false;
        try
        {
            await CopyDirectoryAsync(source, staging, progress, cancellationToken).ConfigureAwait(false);
            Directory.Move(destination, backup);
            originalMoved = true;
            Directory.Move(staging, destination);
            replacementInstalled = true;
            DeleteDirectory(backup, cancellationToken);
        }
        catch
        {
            if (originalMoved && !replacementInstalled && !Directory.Exists(destination) && Directory.Exists(backup))
            {
                Directory.Move(backup, destination);
            }

            throw;
        }
        finally
        {
            if (Directory.Exists(staging))
            {
                DeleteDirectory(staging, CancellationToken.None);
            }
        }
    }

    private static IEnumerable<string> EnumerateSafeFiles(string root)
    {
        foreach (string directory in Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories))
        {
            WindowsPathPolicy.EnsureNoReparsePoint(directory);
        }

        foreach (string file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            yield return (File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0
                ? throw new DomainException("path-reparse-point", "The directory contains a reparse point.")
                : file;
        }
    }

    private static long CalculateDirectorySize(string root, CancellationToken cancellationToken)
    {
        long total = 0;
        foreach (string file in EnumerateSafeFiles(root))
        {
            cancellationToken.ThrowIfCancellationRequested();
            total = checked(total + new FileInfo(file).Length);
        }

        return total;
    }

    private static void DeleteDirectory(string root, CancellationToken cancellationToken)
    {
        WindowsPathPolicy.EnsureNoReparsePoint(root);
        foreach (string file in Directory.EnumerateFiles(root, "*", SearchOption.TopDirectoryOnly))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0)
            {
                throw new DomainException("path-reparse-point", "The directory contains a reparse point.");
            }

            File.SetAttributes(file, FileAttributes.Normal);
            File.Delete(file);
        }

        foreach (string directory in Directory.EnumerateDirectories(root, "*", SearchOption.TopDirectoryOnly))
        {
            cancellationToken.ThrowIfCancellationRequested();
            DeleteDirectory(directory, cancellationToken);
        }

        Directory.Delete(root);
    }

    private static async Task DeleteDirectoryWithProgressAsync(
        string root,
        Func<CancellationToken, Task> entryDeleted,
        CancellationToken cancellationToken)
    {
        WindowsPathPolicy.EnsureNoReparsePoint(root);
        foreach (string file in Directory.EnumerateFiles(root, "*", SearchOption.TopDirectoryOnly))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0)
            {
                throw new DomainException("path-reparse-point", "The directory contains a reparse point.");
            }

            File.SetAttributes(file, FileAttributes.Normal);
            File.Delete(file);
            await entryDeleted(cancellationToken).ConfigureAwait(false);
        }

        foreach (string directory in Directory.EnumerateDirectories(root, "*", SearchOption.TopDirectoryOnly))
        {
            cancellationToken.ThrowIfCancellationRequested();
            await DeleteDirectoryWithProgressAsync(directory, entryDeleted, cancellationToken).ConfigureAwait(false);
        }

        Directory.Delete(root);
        await entryDeleted(cancellationToken).ConfigureAwait(false);
    }

    private static async Task AddFileToArchiveAsync(ZipArchive archive, string path, string entryName, CancellationToken cancellationToken)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        {
            throw new DomainException("path-reparse-point", "A selected archive source is a reparse point.");
        }

        ZipArchiveEntry entry = archive.CreateEntry(entryName.Replace('\\', '/'), CompressionLevel.Optimal);
        Stream destination = await entry.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using (destination.ConfigureAwait(false))
        {
            FileStream source = new(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            await using (source.ConfigureAwait(false))
            {
                await source.CopyToAsync(destination, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static void EnsureDiskSpace(string path, long requiredBytes)
    {
        string? root = Path.GetPathRoot(path);
        if (root is null || new DriveInfo(root).AvailableFreeSpace < requiredBytes + (64L * 1024 * 1024))
        {
            throw new DomainException("disk-space-insufficient", "The destination volume does not have sufficient free space.");
        }
    }

    private static (Encoding Encoding, int PreambleLength) DetectEncoding(byte[] bytes)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        if (bytes.AsSpan().StartsWith(Encoding.UTF8.Preamble))
        {
            return (new UTF8Encoding(true, true), Encoding.UTF8.Preamble.Length);
        }

        if (bytes.AsSpan().StartsWith(Encoding.Unicode.Preamble))
        {
            return (new UnicodeEncoding(false, true, true), Encoding.Unicode.Preamble.Length);
        }

        if (bytes.AsSpan().StartsWith(Encoding.BigEndianUnicode.Preamble))
        {
            return (new UnicodeEncoding(true, true, true), Encoding.BigEndianUnicode.Preamble.Length);
        }

        try
        {
            _ = new UTF8Encoding(false, true).GetString(bytes);
            return (new UTF8Encoding(false, true), 0);
        }
        catch (DecoderFallbackException)
        {
            return (Encoding.GetEncoding(936, new EncoderExceptionFallback(), new DecoderExceptionFallback()), 0);
        }
    }

    private static Encoding ResolveEncoding(string name)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return name.ToUpperInvariant() switch
        {
            "UTF-8" => new UTF8Encoding(false, true),
            "UTF-8-BOM" => new UTF8Encoding(true, true),
            "UTF-16" or "UTF-16LE" => new UnicodeEncoding(false, true, true),
            "UTF-16BE" => new UnicodeEncoding(true, true, true),
            "GB18030" => Encoding.GetEncoding(54936, new EncoderExceptionFallback(), new DecoderExceptionFallback()),
            "GBK" or "CP936" => Encoding.GetEncoding(936, new EncoderExceptionFallback(), new DecoderExceptionFallback()),
            "BIG5" or "CP950" => Encoding.GetEncoding(950, new EncoderExceptionFallback(), new DecoderExceptionFallback()),
            "SHIFT_JIS" or "SHIFT-JIS" or "CP932" => Encoding.GetEncoding(932, new EncoderExceptionFallback(), new DecoderExceptionFallback()),
            "ANSI" => Encoding.GetEncoding(CultureInfo.CurrentCulture.TextInfo.ANSICodePage, new EncoderExceptionFallback(), new DecoderExceptionFallback()),
            _ => throw new DomainException("config-format-unsupported", "The selected text encoding is not supported."),
        };
    }

    private static int PreambleLength(byte[] bytes, Encoding encoding)
    {
        byte[] preamble = encoding.GetPreamble();
        return preamble.Length > 0 && bytes.AsSpan().StartsWith(preamble) ? preamble.Length : 0;
    }

    private static Encoding? ResolveArchiveEncoding(string value, bool allowAuto)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return value.Trim().ToUpperInvariant() switch
        {
            "AUTO" when allowAuto => null,
            "UTF-8" or "UTF8" or "AUTO" => new UTF8Encoding(false, true),
            "GBK" or "CP936" => Encoding.GetEncoding(936, new EncoderExceptionFallback(), new DecoderExceptionFallback()),
            "BIG5" or "CP950" => Encoding.GetEncoding(950, new EncoderExceptionFallback(), new DecoderExceptionFallback()),
            "SHIFT_JIS" or "SHIFT-JIS" or "CP932" => Encoding.GetEncoding(932, new EncoderExceptionFallback(), new DecoderExceptionFallback()),
            _ => throw new DomainException("config-format-unsupported", "The selected ZIP entry-name encoding is not supported."),
        };
    }
}

internal sealed record FileDownloadSource(FileStream Stream, long Length);
