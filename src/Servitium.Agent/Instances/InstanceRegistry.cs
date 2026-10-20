using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

using Servitium.Agent.Archives;
using Servitium.Agent.Paths;
using Servitium.Agent.Storage;
using Servitium.Contracts.Instances;
using Servitium.Domain.Primitives;
using Servitium.Infrastructure.Persistence;

namespace Servitium.Agent.Instances;

internal sealed record LocalInstanceSnapshot(
    Guid Id,
    string Name,
    string WorkingDirectory,
    bool OwnedByServitium,
    string Status,
    int? ProcessId,
    DateTimeOffset? StartedAt,
    int? LastExitCode,
    StructuredLaunchRequest Launch,
    LocalInstanceRuntimeSettings RuntimeSettings,
    IReadOnlyList<string> Tags,
    string? Notes,
    DateTimeOffset CreatedAt,
    long Revision,
    string? OwnedBaseDirectory = null,
    int? PrimaryPort = null);

internal sealed record LocalInstanceRuntimeSettings(
    string StopCommand,
    int StopTimeoutSeconds,
    bool AutoStart,
    bool AutoRestart,
    int MaximumRestartAttempts,
    RconSettingsRequest Rcon,
    QuerySettingsRequest Query,
    int AutoStartDelaySeconds = 0,
    int RestartIntervalSeconds = 5,
    int RestartResetSeconds = 600,
    string? RunCredentialAlias = null,
    TerminalSettingsRequest? Terminal = null,
    UpdateActionRequest? UpdateAction = null,
    IReadOnlyList<ShortcutCommandRequest>? ShortcutCommands = null)
{
    internal static LocalInstanceRuntimeSettings Default { get; } = new(
        "stop",
        60,
        false,
        false,
        3,
        new(false, 25575, null),
        new(false, "127.0.0.1", 25565),
        Terminal: new(),
        UpdateAction: new(),
        ShortcutCommands: []);
}

internal sealed class InstanceRegistry(
    IDbContextFactory<AgentDbContext> contextFactory,
    SqliteWriteCoordinator writeCoordinator,
    IWindowsPathPolicy pathPolicy,
    SafeZipExtractor zipExtractor,
    AgentStorageOptions options,
    Application.Abstractions.ISecretProtector secretProtector,
    Hosting.AgentNodeConfigurationStore? nodeConfiguration = null)
{
    internal async Task<IReadOnlyList<LocalInstanceSnapshot>> ListAsync(CancellationToken cancellationToken)
    {
        using AgentDbContext context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        LocalInstanceRow[] rows = await context.Instances.AsNoTracking().OrderBy(row => row.Name).ToArrayAsync(cancellationToken).ConfigureAwait(false);
        return [.. rows.Select(ToSnapshot)];
    }

    internal async Task<LocalInstanceSnapshot?> FindAsync(Guid id, CancellationToken cancellationToken)
    {
        using AgentDbContext context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        LocalInstanceRow? row = await context.Instances.AsNoTracking().SingleOrDefaultAsync(candidate => candidate.Id == id, cancellationToken).ConfigureAwait(false);
        return row is null ? null : ToSnapshot(row);
    }

    internal Task<LocalInstanceSnapshot> CreateEmptyAsync(
        Guid id,
        string name,
        StructuredLaunchRequest launch,
        IReadOnlyList<string> tags,
        string? notes,
        DateTimeOffset now,
        CancellationToken cancellationToken,
        UpdateInstanceRequest? initialConfiguration = null)
    {
        string ownedBase = GetManagedInstanceRoot();
        string target = Path.Combine(ownedBase, id.ToString("N"));
        _ = pathPolicy.ValidateOwnedWorkingDirectory(target, ownedBase);
        return CreateOwnedAsync(id, name, target, ownedBase, launch, tags, notes, now, null, initialConfiguration, cancellationToken);
    }

    internal async Task<LocalInstanceSnapshot> AttachExternalAsync(
        Guid id,
        string name,
        string absolutePath,
        StructuredLaunchRequest launch,
        DateTimeOffset now,
        CancellationToken cancellationToken,
        UpdateInstanceRequest? initialConfiguration = null)
    {
        IReadOnlyList<LocalInstanceSnapshot> existing = await ListAsync(cancellationToken).ConfigureAwait(false);
        string normalized = pathPolicy.ValidateExternalWorkingDirectory(
            absolutePath,
            [.. existing.Select(instance => instance.WorkingDirectory)],
            GetProtectedRoots());
        return await AddRowAsync(id, name, normalized, false, launch, [], null, now, null, initialConfiguration, cancellationToken).ConfigureAwait(false);
    }

    internal Task<LocalInstanceSnapshot> ImportServerArchiveAsync(
        Guid id,
        string name,
        string archivePath,
        StructuredLaunchRequest launch,
        DateTimeOffset now,
        CancellationToken cancellationToken,
        UpdateInstanceRequest? initialConfiguration = null)
    {
        string normalizedArchive = Path.GetFullPath(archivePath);
        string normalizedStaging = Path.TrimEndingDirectorySeparator(Path.GetFullPath(options.StagingRoot));
        if (!normalizedArchive.StartsWith(normalizedStaging + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            throw new DomainException("archive-unsafe", "The uploaded archive must reside in the Agent staging directory.");
        }

        string ownedBase = GetManagedInstanceRoot();
        string target = Path.Combine(ownedBase, id.ToString("N"));
        _ = pathPolicy.ValidateOwnedWorkingDirectory(target, ownedBase);
        return CreateOwnedAsync(id, name, target, ownedBase, launch, [], null, now, normalizedArchive, initialConfiguration, cancellationToken);
    }

    internal Task UnregisterAsync(Guid id, CancellationToken cancellationToken)
    {
        return writeCoordinator.ExecuteAsync(
            async () =>
            {
                using AgentDbContext context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
                LocalInstanceRow? instance = await context.Instances.SingleOrDefaultAsync(row => row.Id == id, cancellationToken).ConfigureAwait(false) ?? throw new DomainException("not-found", "The instance registration does not exist.");
                if (instance.Status is not ("Stopped" or "Crashed"))
                {
                    throw new DomainException("instance-not-stopped", "A running instance cannot be unregistered.");
                }

                using IDbContextTransaction transaction = await context.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
                Guid[] taskIds = await context.ScheduledTasks.Where(row => row.InstanceId == id).Select(row => row.Id).ToArrayAsync(cancellationToken).ConfigureAwait(false);
                if (taskIds.Length > 0)
                {
                    _ = await context.ScheduledTaskRuns.Where(row => taskIds.Contains(row.TaskId)).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
                    _ = await context.ScheduledTasks.Where(row => taskIds.Contains(row.Id)).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
                }

                _ = context.Instances.Remove(instance);
                _ = await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            },
            cancellationToken);
    }

    internal Task<LocalInstanceSnapshot> UpdateRuntimeAsync(
        Guid id,
        string status,
        int? processId,
        DateTimeOffset? startedAt,
        int? lastExitCode,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        return writeCoordinator.ExecuteAsync(
            async () =>
            {
                using AgentDbContext context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
                LocalInstanceRow row = await context.Instances.SingleOrDefaultAsync(candidate => candidate.Id == id, cancellationToken).ConfigureAwait(false)
                    ?? throw new DomainException("not-found", "The instance registration does not exist.");
                row.Status = status;
                row.ProcessId = processId;
                row.StartedAt = startedAt?.ToUniversalTime();
                row.LastExitCode = lastExitCode;
                row.UpdatedAt = now.ToUniversalTime();
                row.Revision++;
                _ = await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                return ToSnapshot(row);
            },
            cancellationToken);
    }

    internal Task<LocalInstanceSnapshot> UpdateAsync(
        Guid id,
        UpdateInstanceRequest request,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateSettings(request);
        return writeCoordinator.ExecuteAsync(
            async () =>
            {
                using AgentDbContext context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
                LocalInstanceRow row = await context.Instances.SingleOrDefaultAsync(candidate => candidate.Id == id, cancellationToken).ConfigureAwait(false)
                    ?? throw new DomainException("not-found", "The instance registration does not exist.");
                if (row.Status is not ("Stopped" or "Crashed"))
                {
                    throw new DomainException("instance-not-stopped", "Dangerous instance settings can only be changed while stopped.");
                }

                bool duplicate = await context.Instances.AnyAsync(candidate => candidate.Id != id && candidate.Name == request.Name.Trim(), cancellationToken).ConfigureAwait(false);
                if (duplicate)
                {
                    throw new DomainException("conflict", "An instance with the same name already exists.");
                }

                row.Name = request.Name.Trim();
                if (!string.IsNullOrWhiteSpace(request.WorkingDirectory))
                {
                    string requested = NormalizeRequestedPath(request.WorkingDirectory);
                    if (!string.Equals(row.WorkingDirectory, requested, StringComparison.OrdinalIgnoreCase))
                    {
                        string[] registered = await context.Instances.AsNoTracking()
                            .Where(candidate => candidate.Id != id)
                            .Select(candidate => candidate.WorkingDirectory)
                            .ToArrayAsync(cancellationToken).ConfigureAwait(false);
                        row.WorkingDirectory = pathPolicy.ValidateExternalWorkingDirectory(requested, registered, GetProtectedRoots());
                        row.OwnedByServitium = false;
                        row.OwnedBaseDirectory = null;
                    }
                }

                row.TagsJson = JsonSerializer.Serialize(request.Tags.Distinct(StringComparer.OrdinalIgnoreCase).Take(32).ToArray());
                row.Notes = request.Notes;
                row.PrimaryPort = request.PrimaryPort;
                row.LaunchJson = JsonSerializer.Serialize(request.Launch);
                LocalInstanceRuntimeSettings existingSettings = DeserializeRuntimeSettings(row.RuntimeSettingsJson);
                RconSettingsRequest rcon = request.Rcon.Password is null
                    ? request.Rcon with { Password = existingSettings.Rcon.Password }
                    : ProtectRconSettings(request.Rcon);
                row.RuntimeSettingsJson = JsonSerializer.Serialize(new LocalInstanceRuntimeSettings(
                    request.StopCommand,
                    request.StopTimeoutSeconds,
                    request.AutoStart,
                    request.AutoRestart,
                    request.MaximumRestartAttempts,
                    rcon,
                    request.Query,
                    request.AutoStartDelaySeconds,
                    request.RestartIntervalSeconds,
                    request.RestartResetSeconds,
                    request.RunCredentialAlias?.Trim(),
                    request.Terminal ?? new(),
                    request.UpdateAction ?? new(),
                    request.ShortcutCommands ?? []));
                row.UpdatedAt = now.ToUniversalTime();
                row.Revision++;
                _ = await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                return ToSnapshot(row);
            },
            cancellationToken);
    }

    internal async Task DeleteOwnedWorkingDirectoryAsync(Guid id, CancellationToken cancellationToken)
    {
        LocalInstanceSnapshot instance = await FindAsync(id, cancellationToken).ConfigureAwait(false)
            ?? throw new DomainException("not-found", "The instance registration does not exist.");
        if (!instance.OwnedByServitium)
        {
            throw new DomainException("instance-not-owned", "External working directories never have a recursive delete path.");
        }

        if (!string.Equals(instance.Status, "Stopped", StringComparison.Ordinal))
        {
            throw new DomainException("instance-not-stopped", "The instance must be stopped before deleting its working directory.");
        }

        string ownedBase = instance.OwnedBaseDirectory ?? options.ManagedInstanceRoot;
        string validated = pathPolicy.ValidateOwnedWorkingDirectory(instance.WorkingDirectory, ownedBase);
        OwnedDirectoryDeleter.Delete(validated);
        await UnregisterAsync(id, cancellationToken).ConfigureAwait(false);
    }

    private async Task<LocalInstanceSnapshot> CreateOwnedAsync(
        Guid id,
        string name,
        string target,
        string ownedBaseDirectory,
        StructuredLaunchRequest launch,
        IReadOnlyList<string> tags,
        string? notes,
        DateTimeOffset now,
        string? archivePath,
        UpdateInstanceRequest? initialConfiguration,
        CancellationToken cancellationToken)
    {
        try
        {
            if (archivePath is null)
            {
                _ = Directory.CreateDirectory(target);
            }
            else
            {
                _ = await zipExtractor.ExtractToNewDirectoryAsync(archivePath, target, cancellationToken).ConfigureAwait(false);
            }

            return await AddRowAsync(id, name, target, true, launch, tags, notes, now, ownedBaseDirectory, initialConfiguration, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            if (Directory.Exists(target))
            {
                OwnedDirectoryDeleter.Delete(target);
            }

            throw;
        }
    }

    private Task<LocalInstanceSnapshot> AddRowAsync(
        Guid id,
        string name,
        string workingDirectory,
        bool owned,
        StructuredLaunchRequest launch,
        IReadOnlyList<string> tags,
        string? notes,
        DateTimeOffset now,
        string? ownedBaseDirectory = null,
        UpdateInstanceRequest? initialConfiguration = null,
        CancellationToken cancellationToken = default)
    {
        if (initialConfiguration is not null)
        {
            ValidateSettings(initialConfiguration);
        }

        return writeCoordinator.ExecuteAsync(
            async () =>
            {
                using AgentDbContext context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
                string effectiveName = (initialConfiguration?.Name ?? name).Trim();
                if (await context.Instances.AnyAsync(row => row.Id == id || row.Name == effectiveName || row.WorkingDirectory == workingDirectory, cancellationToken).ConfigureAwait(false))
                {
                    throw new DomainException("conflict", "The instance identifier, name, or working directory is already registered.");
                }

                LocalInstanceRow row = new()
                {
                    Id = id,
                    Name = effectiveName,
                    WorkingDirectory = Path.GetFullPath(workingDirectory),
                    OwnedByServitium = owned,
                    OwnedBaseDirectory = ownedBaseDirectory,
                    Status = "Stopped",
                    LaunchJson = JsonSerializer.Serialize(initialConfiguration?.Launch ?? launch),
                    RuntimeSettingsJson = JsonSerializer.Serialize(initialConfiguration is null
                        ? LocalInstanceRuntimeSettings.Default
                        : new LocalInstanceRuntimeSettings(
                            initialConfiguration.StopCommand,
                            initialConfiguration.StopTimeoutSeconds,
                            initialConfiguration.AutoStart,
                            initialConfiguration.AutoRestart,
                            initialConfiguration.MaximumRestartAttempts,
                            ProtectRconSettings(initialConfiguration.Rcon),
                            initialConfiguration.Query,
                            initialConfiguration.AutoStartDelaySeconds,
                            initialConfiguration.RestartIntervalSeconds,
                            initialConfiguration.RestartResetSeconds,
                            initialConfiguration.RunCredentialAlias?.Trim(),
                            initialConfiguration.Terminal ?? new(),
                            initialConfiguration.UpdateAction ?? new(),
                            initialConfiguration.ShortcutCommands ?? [])),
                    TagsJson = JsonSerializer.Serialize(initialConfiguration?.Tags ?? tags),
                    Notes = initialConfiguration?.Notes ?? notes,
                    PrimaryPort = initialConfiguration?.PrimaryPort,
                    CreatedAt = now.ToUniversalTime(),
                    UpdatedAt = now.ToUniversalTime(),
                    Revision = 1,
                };
                _ = context.Instances.Add(row);
                _ = await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                return ToSnapshot(row);
            },
            cancellationToken);
    }

    private static LocalInstanceSnapshot ToSnapshot(LocalInstanceRow row)
    {
        StructuredLaunchRequest launch = JsonSerializer.Deserialize<StructuredLaunchRequest>(row.LaunchJson)
            ?? throw new InvalidOperationException("The persisted launch configuration is invalid.");
        LocalInstanceRuntimeSettings settings = DeserializeRuntimeSettings(row.RuntimeSettingsJson);
        string[] tags = JsonSerializer.Deserialize<string[]>(row.TagsJson) ?? [];
        return new(row.Id, row.Name, row.WorkingDirectory, row.OwnedByServitium, row.Status, row.ProcessId, row.StartedAt, row.LastExitCode, launch, settings, tags, row.Notes, row.CreatedAt, row.Revision, row.OwnedBaseDirectory, row.PrimaryPort);
    }

    private static void ValidateSettings(UpdateInstanceRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Name) || request.Name.Trim().Length > 64 ||
            string.IsNullOrWhiteSpace(request.StopCommand) || request.StopCommand.Any(char.IsControl) ||
            request.StopTimeoutSeconds is < 1 or > 600 ||
            request.MaximumRestartAttempts is < -1 or > 100 ||
            request.AutoStartDelaySeconds is < 0 or > 600 ||
            request.RestartIntervalSeconds is < 0 or > 600 ||
            request.RestartResetSeconds is < 60 or > 86400 ||
            request.Rcon.Port is < 1 or > 65535 ||
            request.Query.Port is < 1 or > 65535 ||
            request.PrimaryPort is < 1 or > 65535 ||
            string.IsNullOrWhiteSpace(request.Query.Host) ||
            request.RunCredentialAlias is { Length: > 64 } ||
            !ValidateTerminal(request.Terminal ?? new()) ||
            !ValidateUpdateAction(request.UpdateAction ?? new()) ||
            !ValidateShortcuts(request.ShortcutCommands ?? []))
        {
            throw new DomainException("validation-failed", "The instance settings are invalid.");
        }
    }

    private static LocalInstanceRuntimeSettings DeserializeRuntimeSettings(string json)
    {
        LocalInstanceRuntimeSettings? settings = JsonSerializer.Deserialize<LocalInstanceRuntimeSettings>(json);
        return settings is null
            ? LocalInstanceRuntimeSettings.Default
            : settings with
            {
                Terminal = settings.Terminal ?? new(),
                UpdateAction = settings.UpdateAction ?? new(),
                ShortcutCommands = settings.ShortcutCommands ?? [],
            };
    }

    private static bool ValidateTerminal(TerminalSettingsRequest terminal)
    {
        string[] encodings = ["UTF-8", "GBK", "Big5", "Shift_JIS", "system-ansi"];
        return terminal.Columns is >= 20 and <= 500 && terminal.Rows is >= 5 and <= 200 &&
               encodings.Contains(terminal.InputEncoding, StringComparer.OrdinalIgnoreCase) &&
               encodings.Contains(terminal.OutputEncoding, StringComparer.OrdinalIgnoreCase) &&
               terminal.NewLine is "LF" or "CRLF";
    }

    private static bool ValidateUpdateAction(UpdateActionRequest update)
    {
        return update.Kind switch
        {
            "none" => update.SourceRelativePath is null && update.TargetRelativePath is null && update.ChannelId is null,
            "jar-replace" => !string.IsNullOrWhiteSpace(update.SourceRelativePath) && !string.IsNullOrWhiteSpace(update.TargetRelativePath) && update.ChannelId is null,
            "hmcl-channel-sync" => update.ChannelId is not null && update.SourceRelativePath is null && update.TargetRelativePath is null,
            _ => false,
        };
    }

    private static bool ValidateShortcuts(IReadOnlyList<ShortcutCommandRequest> shortcuts)
    {
        return shortcuts.Count <= 20 && shortcuts.All(item =>
            !string.IsNullOrWhiteSpace(item.Name) && item.Name.Trim().Length <= 32 &&
            !string.IsNullOrWhiteSpace(item.Command) && item.Command.Length <= 1024 &&
            !item.Command.Any(character => character is '\r' or '\n' || char.IsControl(character)));
    }

    private RconSettingsRequest ProtectRconSettings(RconSettingsRequest settings)
    {
        if (string.IsNullOrEmpty(settings.Password))
        {
            return settings;
        }

        byte[] protectedBytes = secretProtector.Protect(System.Text.Encoding.UTF8.GetBytes(settings.Password));
        return settings with { Password = "protected:" + Convert.ToBase64String(protectedBytes) };
    }

    private static string[] GetProtectedRoots()
    {
        return [.. new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        }.Where(path => !string.IsNullOrWhiteSpace(path))];
    }

    private string GetManagedInstanceRoot()
    {
        return nodeConfiguration?.Current.DefaultInstancePath is { Length: > 0 } configured
            ? Path.GetFullPath(configured)
            : options.ManagedInstanceRoot;
    }

    private static string NormalizeRequestedPath(string path)
    {
        try
        {
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new DomainException("instance-path-invalid", "The working directory path is invalid.");
        }
    }
}

internal static class OwnedDirectoryDeleter
{
    internal static void Delete(string root)
    {
        if (!Directory.Exists(root))
        {
            return;
        }

        DeleteContents(root);
        Directory.Delete(root);
    }

    private static void DeleteContents(string directory)
    {
        WindowsPathPolicy.EnsureNoReparsePoint(directory);
        foreach (string file in Directory.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly))
        {
            if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0)
            {
                throw new DomainException("path-reparse-point", "The managed directory contains a reparse point.");
            }

            File.SetAttributes(file, FileAttributes.Normal);
            File.Delete(file);
        }

        foreach (string child in Directory.EnumerateDirectories(directory, "*", SearchOption.TopDirectoryOnly))
        {
            if ((File.GetAttributes(child) & FileAttributes.ReparsePoint) != 0)
            {
                throw new DomainException("path-reparse-point", "The managed directory contains a reparse point.");
            }

            DeleteContents(child);
            Directory.Delete(child);
        }
    }
}
