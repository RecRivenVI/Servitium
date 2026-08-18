using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using Servitium.Agent.ControlPlane;
using Servitium.Agent.Instances;
using Servitium.Agent.Paths;
using Servitium.Agent.Storage;
using Servitium.Contracts.Agent;
using Servitium.Domain.Common;
using Servitium.Infrastructure.Distribution;

namespace Servitium.Agent.Distribution;

/// <summary>在 Agent 侧采集实例内容并按 HMCL 更新语义同步 Release。</summary>
internal sealed class AgentHmclDistributionService(
    InstanceRegistry instances,
    IWindowsPathPolicy pathPolicy,
    AgentStorageOptions options,
    InboundFileTransferStore inbound,
    OutboundFileTransferManager outbound)
{
    private const int MaximumFiles = 20_000;
    private const long MaximumBytes = 64L * 1024 * 1024 * 1024;
    private readonly string _collectionRoot = Path.Combine(options.StagingRoot, "hmcl-collect");
    private readonly string _syncRoot = Path.Combine(options.StagingRoot, "hmcl-sync");
    private readonly string _stateRoot = Path.Combine(options.DataRoot, "distribution-sync");

    internal async Task<AgentCollectedDirectoryResult> CollectAsync(
        AgentCollectDirectoryForReleaseCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (!Guid.TryParseExact(command.StreamId, "N", out _))
        {
            throw new DomainException("validation-failed", "The content collection stream identifier is invalid.");
        }

        LocalInstanceSnapshot instance = await RequireStoppedAsync(command.InstanceId, cancellationToken).ConfigureAwait(false);
        _ = Directory.CreateDirectory(_collectionRoot);
        string archivePath = Path.Combine(_collectionRoot, command.StreamId + ".zip");
        Dictionary<string, string> excluded = new(StringComparer.Ordinal);
        List<(string FullPath, string RelativePath, long Size)> files = [];
        long total = 0;
        foreach (string path in EnumerateRegularFiles(instance.WorkingDirectory, cancellationToken))
        {
            string relative = Path.GetRelativePath(instance.WorkingDirectory, path).Replace(Path.DirectorySeparatorChar, '/');
            string? reason = HmclArchiveProcessor.GetExclusionReason(relative);
            if (reason is not null)
            {
                excluded[relative] = reason;
                continue;
            }

            HmclArchiveProcessor.ValidateReleasePath(relative);
            long size = new FileInfo(path).Length;
            checked
            {
                total += size;
            }

            if (files.Count >= MaximumFiles || total > MaximumBytes)
            {
                throw new DomainException("archive-limit-exceeded", "The instance content exceeds the HMCL collection limits.");
            }

            files.Add((path, relative, size));
        }

        if (files.Count == 0)
        {
            throw new DomainException("hmcl-release-validation-failed", "No distributable files remain after filtering.");
        }

        try
        {
            FileStream destination = new(archivePath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 256 * 1024, FileOptions.Asynchronous | FileOptions.WriteThrough);
            await using (destination.ConfigureAwait(false))
            using (ZipArchive archive = new(destination, ZipArchiveMode.Create, leaveOpen: true))
            {
                foreach ((string fullPath, string relativePath, long expectedSize) in files.OrderBy(file => file.RelativePath, StringComparer.Ordinal))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    ZipArchiveEntry entry = archive.CreateEntry(relativePath, CompressionLevel.NoCompression);
                    FileStream source = new(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read, 256 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
                    await using (source.ConfigureAwait(false))
                    {
                        Stream entryStream = await entry.OpenAsync(cancellationToken).ConfigureAwait(false);
                        await using (entryStream.ConfigureAwait(false))
                        {
                            await source.CopyToAsync(entryStream, 256 * 1024, cancellationToken).ConfigureAwait(false);
                        }

                        if (source.Length != expectedSize)
                        {
                            throw new DomainException("conflict", "An instance file changed while it was being collected.");
                        }
                    }
                }
            }

            long archiveBytes = new FileInfo(archivePath).Length;
            await outbound.StartContentCollectAsync(command.StreamId, archivePath, cancellationToken).ConfigureAwait(false);
            return new AgentCollectedDirectoryResult(command.InstanceId, command.StreamId, files.Count, archiveBytes, excluded);
        }
        catch
        {
            if (File.Exists(archivePath))
            {
                File.Delete(archivePath);
            }

            throw;
        }
    }

    internal async Task<AgentSyncFromReleaseResult> SyncAsync(AgentSyncFromReleaseCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (!Guid.TryParseExact(command.StreamId, "N", out _))
        {
            throw new DomainException("validation-failed", "The content sync command is invalid.");
        }

        string archivePath = inbound.Consume(command.StreamId);
        return await SyncArchiveAsync(command, archivePath, cancellationToken).ConfigureAwait(false);
    }

    internal async Task<AgentSyncFromReleaseResult> SyncArchiveAsync(AgentSyncFromReleaseCommand command, string archivePath, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(archivePath);
        string extractionRoot = Path.Combine(_syncRoot, command.StreamId);
        try
        {
            if (!Guid.TryParseExact(command.StreamId, "N", out _) || command.ReleaseId == Guid.Empty || command.Paths.Count > MaximumFiles)
            {
                throw new DomainException("validation-failed", "The content sync command is invalid.");
            }

            LocalInstanceSnapshot instance = await RequireStoppedAsync(command.InstanceId, cancellationToken).ConfigureAwait(false);
            HashSet<string> newPaths = new(StringComparer.OrdinalIgnoreCase);
            foreach (string path in command.Paths)
            {
                HmclArchiveProcessor.ValidateReleasePath(path);
                if (!newPaths.Add(path))
                {
                    throw new DomainException("hmcl-release-validation-failed", "The content sync paths collide ignoring case.");
                }
            }

            HmclArchivePlan plan = HmclDistributionStore.InspectContentArchive(archivePath);
            HashSet<string> archivePaths = new(plan.Files.Select(file => file.ReleasePath), StringComparer.OrdinalIgnoreCase);
            if (!archivePaths.SetEquals(newPaths) || plan.ExcludedPaths.Count != 0)
            {
                throw new DomainException("hmcl-release-validation-failed", "The content sync archive does not match the declared Release.");
            }

            await ExtractPlannedAsync(archivePath, extractionRoot, plan.Files, cancellationToken).ConfigureAwait(false);
            SyncState previous = await ReadStateAsync(command.InstanceId, cancellationToken).ConfigureAwait(false);
            int added = 0;
            int replaced = 0;
            foreach (string relative in newPaths.Order(StringComparer.Ordinal))
            {
                string target = pathPolicy.ResolveRelativePath(instance.WorkingDirectory, relative);
                string source = Path.Combine(extractionRoot, relative.Replace('/', Path.DirectorySeparatorChar));
                bool existed = File.Exists(target);
                if (Directory.Exists(target) || (existed && (File.GetAttributes(target) & FileAttributes.ReparsePoint) != 0))
                {
                    throw new DomainException("path-reparse-point", "A managed target is not a regular file.");
                }

                string? parent = Path.GetDirectoryName(target);
                if (!string.IsNullOrEmpty(parent))
                {
                    _ = Directory.CreateDirectory(parent);
                    WindowsPathPolicy.EnsureNoReparsePoint(parent);
                }

                string temporary = target + ".servitium-sync-" + Guid.NewGuid().ToString("N");
                try
                {
                    File.Copy(source, temporary, overwrite: false);
                    File.Move(temporary, target, overwrite: true);
                }
                finally
                {
                    if (File.Exists(temporary))
                    {
                        File.Delete(temporary);
                    }
                }

                if (existed)
                {
                    replaced++;
                }
                else
                {
                    added++;
                }
            }

            int deleted = 0;
            foreach (string relative in previous.Paths.Except(newPaths, StringComparer.OrdinalIgnoreCase))
            {
                HmclArchiveProcessor.ValidateReleasePath(relative);
                string target = pathPolicy.ResolveRelativePath(instance.WorkingDirectory, relative);
                if (File.Exists(target))
                {
                    if ((File.GetAttributes(target) & FileAttributes.ReparsePoint) != 0)
                    {
                        throw new DomainException("path-reparse-point", "A managed target became a reparse point.");
                    }

                    File.Delete(target);
                    deleted++;
                }
            }

            await WriteStateAsync(command.InstanceId, new SyncState(command.ReleaseId, [.. newPaths.Order(StringComparer.Ordinal)]), cancellationToken).ConfigureAwait(false);
            return new AgentSyncFromReleaseResult(command.InstanceId, command.ReleaseId, added, replaced, deleted);
        }
        finally
        {
            if (File.Exists(archivePath))
            {
                File.Delete(archivePath);
            }

            if (Directory.Exists(extractionRoot))
            {
                DeleteStagingTree(extractionRoot);
            }
        }
    }

    private async Task<LocalInstanceSnapshot> RequireStoppedAsync(Guid instanceId, CancellationToken cancellationToken)
    {
        LocalInstanceSnapshot instance = await instances.FindAsync(instanceId, cancellationToken).ConfigureAwait(false)
            ?? throw new DomainException("not-found", "The instance does not exist.");
        if (instance.Status != "Stopped")
        {
            throw new DomainException("instance-not-stopped", "HMCL content operations require a stopped instance.");
        }

        WindowsPathPolicy.EnsureNoReparsePoint(instance.WorkingDirectory);
        return instance;
    }

    private static IEnumerable<string> EnumerateRegularFiles(string root, CancellationToken cancellationToken)
    {
        Stack<string> pending = new();
        pending.Push(root);
        while (pending.TryPop(out string? directory))
        {
            cancellationToken.ThrowIfCancellationRequested();
            WindowsPathPolicy.EnsureNoReparsePoint(directory);
            foreach (string child in Directory.EnumerateDirectories(directory, "*", SearchOption.TopDirectoryOnly))
            {
                if ((File.GetAttributes(child) & FileAttributes.ReparsePoint) != 0)
                {
                    throw new DomainException("path-reparse-point", "The instance directory contains a reparse point.");
                }

                pending.Push(child);
            }

            foreach (string file in Directory.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly))
            {
                yield return (File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0
                    ? throw new DomainException("path-reparse-point", "The instance directory contains a reparse point.")
                    : file;
            }
        }
    }

    private static async Task ExtractPlannedAsync(string archivePath, string targetRoot, IReadOnlyList<HmclPlannedFile> files, CancellationToken cancellationToken)
    {
        _ = Directory.CreateDirectory(targetRoot);
        WindowsPathPolicy.EnsureNoReparsePoint(targetRoot);
        using FileStream input = new(archivePath, FileMode.Open, FileAccess.Read, FileShare.Read, 256 * 1024, FileOptions.Asynchronous | FileOptions.RandomAccess);
        using ZipArchive archive = new(input, ZipArchiveMode.Read);
        var entries = archive.Entries.ToDictionary(entry => entry.FullName.Replace('\\', '/'), StringComparer.OrdinalIgnoreCase);
        foreach (HmclPlannedFile file in files)
        {
            ZipArchiveEntry entry = entries[file.EntryName];
            string target = Path.GetFullPath(Path.Combine(targetRoot, file.ReleasePath.Replace('/', Path.DirectorySeparatorChar)));
            if (!target.StartsWith(Path.TrimEndingDirectorySeparator(targetRoot) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                throw new DomainException("archive-unsafe-path", "The sync archive entry escapes the staging root.");
            }

            _ = Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            Stream source = await entry.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using (source.ConfigureAwait(false))
            {
                FileStream destination = new(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, 256 * 1024, FileOptions.Asynchronous | FileOptions.WriteThrough);
                await using (destination.ConfigureAwait(false))
                {
                    await source.CopyToAsync(destination, 256 * 1024, cancellationToken).ConfigureAwait(false);
                    await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
                }
            }
        }
    }

    private async Task<SyncState> ReadStateAsync(Guid instanceId, CancellationToken cancellationToken)
    {
        string path = StatePath(instanceId);
        if (!File.Exists(path))
        {
            return new SyncState(null, []);
        }

        using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        return await JsonSerializer.DeserializeAsync<SyncState>(stream, cancellationToken: cancellationToken).ConfigureAwait(false)
            ?? throw new DomainException("validation-failed", "The instance distribution sync state is invalid.");
    }

    private async Task WriteStateAsync(Guid instanceId, SyncState state, CancellationToken cancellationToken)
    {
        _ = Directory.CreateDirectory(_stateRoot);
        string path = StatePath(instanceId);
        string temporary = path + ".new-" + Guid.NewGuid().ToString("N");
        try
        {
            FileStream stream = new(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.Asynchronous | FileOptions.WriteThrough);
            await using (stream.ConfigureAwait(false))
            {
                await JsonSerializer.SerializeAsync(stream, state, cancellationToken: cancellationToken).ConfigureAwait(false);
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
    }

    private string StatePath(Guid instanceId)
    {
        return Path.Combine(_stateRoot, instanceId.ToString("N") + ".json");
    }

    private static void DeleteStagingTree(string root)
    {
        List<string> directories = [];
        Stack<string> pending = new();
        pending.Push(root);
        while (pending.TryPop(out string? directory))
        {
            WindowsPathPolicy.EnsureNoReparsePoint(directory);
            directories.Add(directory);
            foreach (string file in Directory.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly))
            {
                if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0)
                {
                    throw new DomainException("path-reparse-point", "The staging directory contains a reparse point.");
                }

                File.Delete(file);
            }

            foreach (string child in Directory.EnumerateDirectories(directory, "*", SearchOption.TopDirectoryOnly))
            {
                if ((File.GetAttributes(child) & FileAttributes.ReparsePoint) != 0)
                {
                    throw new DomainException("path-reparse-point", "The staging directory contains a reparse point.");
                }

                pending.Push(child);
            }
        }

        foreach (string directory in directories.OrderByDescending(path => path.Length))
        {
            Directory.Delete(directory, recursive: false);
        }
    }

    private sealed record SyncState(Guid? ReleaseId, IReadOnlyList<string> Paths);
}
