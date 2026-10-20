using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Servitium.Contracts.Configurations;
using Servitium.Contracts.Distribution;
using Servitium.Contracts.Files;
using Servitium.Contracts.Identity;
using Servitium.Contracts.Instances;
using Servitium.Contracts.Java;
using Servitium.Contracts.Mods;
using Servitium.Contracts.Nodes;
using Servitium.Contracts.Operations;
using Servitium.Contracts.Scheduling;
using Servitium.Contracts.Settings;
using Servitium.Client.Http;
using Servitium.Desktop.Models;

namespace Servitium.Desktop.ViewModels;

internal sealed partial class PageActionDispatcher
{
    internal Task<PageActionResult> ExecuteAsync(
        string pageId,
        string action,
        IReadOnlyDictionary<string, string> fields,
        PageListItem? selected,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pageId);
        ArgumentException.ThrowIfNullOrWhiteSpace(action);
        ArgumentNullException.ThrowIfNull(fields);
        if (selected is not null && !_selectedBatch.Contains(selected.Value))
        {
            Select(selected.Value);
        }

        return (pageId, action) switch
        {
            ("PG-001", "complete-setup") => InitializeAsync(fields, cancellationToken),
            ("PG-002", "sign-in") => SignInAsync(fields, cancellationToken),
            ("PG-002", "external-sign-in") => ExternalSignInAsync(cancellationToken),
            ("PG-003", "verify" or "use-recovery-code") => CompleteTotpAsync(fields, cancellationToken),
            ("PG-010", "create") => Result("PG-019"),
            ("PG-010", "start" or "stop" or "restart" or "force-stop" or "unregister") => InstanceListActionAsync(action, cancellationToken),
            ("PG-011", "open") => Result("PG-012"),
            ("PG-012", "start" or "stop" or "restart" or "force-stop") => InstanceControlAsync(action, cancellationToken),
            ("PG-012", "send-command") => SendCommandAsync(fields, cancellationToken),
            ("PG-012", "sync-content" or "export-session-log" or "open-log-files" or "open-settings") => InstanceConsoleActionAsync(action, cancellationToken),
            ("PG-013", _) => FileActionAsync(action, fields, cancellationToken),
            ("PG-014", "refresh") => Refreshed(),
            ("PG-014", "open") => Result("PG-015"),
            ("PG-014", "open-file-manager" or "view-backups") => ConfigurationListActionAsync(action),
            ("PG-015", "save" or "discard" or "toggle-view") => ConfigurationEditorActionAsync(action, fields, cancellationToken),
            ("PG-015", "restore") => RestoreConfigurationAsync(cancellationToken),
            ("PG-016", "refresh") => Refreshed(),
            ("PG-016", "upload") => UploadModAsync(fields, cancellationToken),
            ("PG-016", "details" or "open-configuration") => ModNavigationActionAsync(action, cancellationToken),
            ("PG-016", "enable" or "disable" or "delete") => ModActionAsync(action, fields, cancellationToken),
            ("PG-017", _) => ScheduleActionAsync(action, fields, cancellationToken),
            ("PG-018", "save") => SaveInstanceAsync(fields, cancellationToken),
            ("PG-018", "unregister") => InstanceListActionAsync("unregister", cancellationToken),
            ("PG-018", "delete-working-directory") => DeleteWorkingDirectoryAsync(cancellationToken),
            ("PG-019", _) => InstanceCreationActionAsync(action, fields, cancellationToken),
            ("PG-020", "refresh") => Refreshed(),
            ("PG-020", "create-token") => CreateNodeTokenAsync(fields, cancellationToken),
            ("PG-020", "open") => Result("PG-021"),
            ("PG-020", "view-node-instances" or "view-java" or "reconnect" or "rotate-credential" or "remove-node") => NodeActionAsync(action, cancellationToken),
            ("PG-021", "refresh") => Refreshed(),
            ("PG-021", "save") => SaveNodeAsync(fields, cancellationToken),
            ("PG-021", "view-node-instances" or "view-java" or "reconnect" or "rotate-credential" or "revoke" or "remove-node") => NodeActionAsync(action, cancellationToken),
            ("PG-022", _) => JavaActionAsync(action, fields, cancellationToken),
            ("PG-030", _) => UserActionAsync(action, fields, cancellationToken),
            ("PG-031", "grant" or "revoke") => GrantActionAsync(action, cancellationToken),
            ("PG-032", _) => AccountActionAsync(action, fields, cancellationToken),
            ("PG-040", "refresh") => Task.FromResult(new PageActionResult("common.operation-completed")),
            ("PG-040", "customize") => CustomizeDashboardAsync(cancellationToken),
            ("PG-041", "save" or "test" or "export-diagnostics" or "backup-database" or "reset-dashboard") => SettingsActionAsync(action, fields, cancellationToken),
            ("PG-042", "export") => ExportActivityAsync(fields, cancellationToken),
            ("PG-043", "export") => ExportAuditAsync(fields, cancellationToken),
            ("PG-044", "cancel" or "open") => OperationActionAsync(action, cancellationToken),
            ("PG-060", "refresh") => Refreshed(),
            ("PG-060", "create-channel") => CreateChannelAsync(fields, cancellationToken),
            ("PG-060", "open-channel") => Result("PG-061"),
            ("PG-060", "create-release") => Result("PG-062"),
            ("PG-060", "edit-channel" or "delete-channel") => ChannelActionAsync(action, fields, cancellationToken),
            ("PG-061", "refresh") => Refreshed(),
            ("PG-061", "create-release") => Result("PG-062"),
            ("PG-061", "save" or "publish" or "rollback" or "change-visibility" or "rotate-token" or "delete-channel") => ChannelActionAsync(action, fields, cancellationToken),
            ("PG-062", "inspect" or "create-release" or "create-and-publish") => ReleaseCreationActionAsync(action, fields, cancellationToken),
            ("PG-063", "verify" or "download-manifest" or "rollback") => ReleaseActionAsync(action, cancellationToken),
            ("PG-050", "download") => PublicDownloadAsync(cancellationToken),
            ("PG-051", "go-home") => Result("PG-040"),
            _ => throw new ArgumentOutOfRangeException(nameof(action)),
        };
    }

    private static Task<PageActionResult> Refreshed()
    {
        return Task.FromResult(new PageActionResult("common.operation-completed"));
    }

    private async Task<PageActionResult> InitializeAsync(IReadOnlyDictionary<string, string> fields, CancellationToken cancellationToken)
    {
        string password = Value(fields, "password");
        if (password != Value(fields, "confirm-password"))
        {
            throw new ArgumentException(services.Localization.Get("error.validation-failed"));
        }

        await services.Session.Client.Session.InitializeAsync(
            new InitializeRequest(Value(fields, "user-name"), Value(fields, "user-name"), password, Value(fields, "language", "zh-Hans"), null),
            cancellationToken).ConfigureAwait(false);
        return new("common.operation-completed", "PG-002");
    }

    private async Task<PageActionResult> SignInAsync(IReadOnlyDictionary<string, string> fields, CancellationToken cancellationToken)
    {
        Client.Session.SignInResult result = await services.Session.SignInAsync(
            new SignInRequest(Value(fields, "user-name"), Value(fields, "password")), cancellationToken).ConfigureAwait(false);
        return result.TotpRequired
            ? new PageActionResult("state.submitting", "PG-003", false)
            : new PageActionResult("common.operation-completed", "PG-040", false);
    }

    private async Task<PageActionResult> ExternalSignInAsync(CancellationToken cancellationToken)
    {
        await services.Session.SignInWithOidcAsync(cancellationToken).ConfigureAwait(false);
        return new("common.operation-completed", "PG-040", false);
    }

    private async Task<PageActionResult> CompleteTotpAsync(IReadOnlyDictionary<string, string> fields, CancellationToken cancellationToken)
    {
        string code = Value(fields, "code");
        if (string.IsNullOrWhiteSpace(code))
        {
            code = Value(fields, "recovery-code");
        }

        await services.Session.CompleteTotpAsync(code, cancellationToken).ConfigureAwait(false);
        return new("common.operation-completed", "PG-040", false);
    }

    private async Task<PageActionResult> InstanceListActionAsync(string action, CancellationToken cancellationToken)
    {
        InstanceDto instance = RequireInstance();
        if (action == "unregister")
        {
            string impact = services.Localization.Format("danger.unregister-impact", instance.WorkingDirectory);
            bool confirmed = await services.Interaction.ConfirmAsync("action.unregister", impact).ConfigureAwait(true);
            if (!confirmed)
            {
                return new("state.cancelled", null, false);
            }

            await services.Session.Client.UnregisterInstanceAsync(instance.Id, instance.NodeId, cancellationToken).ConfigureAwait(false);
            await services.Session.RefreshSelectionsAsync(cancellationToken).ConfigureAwait(false);
            return new("common.operation-completed");
        }

        if (action == "force-stop" &&
            !await services.Interaction.ConfirmAsync(
                "action.force-stop",
                services.Localization.Get("danger.forcestop-impact")).ConfigureAwait(true))
        {
            return new("state.cancelled", null, false);
        }

        _ = action switch
        {
            "start" => await services.Session.Client.StartInstanceAsync(instance.Id, instance.NodeId, cancellationToken).ConfigureAwait(false),
            "stop" => await services.Session.Client.StopInstanceAsync(instance.Id, instance.NodeId, cancellationToken).ConfigureAwait(false),
            "restart" => await services.Session.Client.RestartInstanceAsync(instance.Id, instance.NodeId, cancellationToken).ConfigureAwait(false),
            "force-stop" => await services.Session.Client.ForceStopInstanceAsync(instance.Id, instance.NodeId, cancellationToken).ConfigureAwait(false),
            _ => throw new ArgumentOutOfRangeException(nameof(action)),
        };
        return new("common.operation-accepted");
    }

    private Task<PageActionResult> InstanceControlAsync(string action, CancellationToken cancellationToken)
    {
        return InstanceListActionAsync(action, cancellationToken);
    }

    private async Task<PageActionResult> SendCommandAsync(IReadOnlyDictionary<string, string> fields, CancellationToken cancellationToken)
    {
        InstanceDto instance = RequireInstance();
        _ = await services.Session.Client.SendInstanceCommandAsync(
            instance.Id, instance.NodeId, new SendInstanceCommandRequest(Value(fields, "command")), cancellationToken).ConfigureAwait(false);
        return new("common.operation-completed");
    }

    private async Task<PageActionResult> InstanceConsoleActionAsync(string action, CancellationToken cancellationToken)
    {
        InstanceDto instance = RequireInstance();
        if (action == "open-settings")
        {
            return new(null, "PG-018", false);
        }

        if (action == "open-log-files")
        {
            _directoryPath = "logs";
            return new(null, "PG-013", false);
        }

        if (action == "sync-content")
        {
            if (!await services.Interaction.ConfirmAsync(
                    "action.sync-content",
                    services.Localization.Get("danger.sync-content-impact")).ConfigureAwait(true))
            {
                return new("state.cancelled", null, false);
            }

            _ = await services.Session.Client.RunInstanceUpdateAsync(
                instance.Id, instance.NodeId, new RunInstanceUpdateRequest(true), cancellationToken).ConfigureAwait(false);
            return new("common.operation-accepted");
        }

        DownloadedFile export = await services.Session.Client.ExportTerminalSessionLogAsync(
            instance.Id, instance.NodeId, false, cancellationToken).ConfigureAwait(false);
        string? target = await services.Interaction.PickSaveFileAsync(export.FileName, ".log").ConfigureAwait(true);
        if (target is null)
        {
            return new("state.cancelled", null, false);
        }

        await File.WriteAllBytesAsync(target, export.Content, cancellationToken).ConfigureAwait(false);
        return new("common.operation-completed", null, false);
    }

    private async Task<PageActionResult> FileActionAsync(string action, IReadOnlyDictionary<string, string> fields, CancellationToken cancellationToken)
    {
        InstanceDto instance = RequireInstance();
        var entry = _selected as FileEntryDto;
        FileEntryDto[] selectedEntries = [.. _selectedBatch.OfType<FileEntryDto>().DistinctBy(item => item.Path, StringComparer.Ordinal)];
        string path = Value(fields, "path", _directoryPath);
        string collisionPolicy = Value(fields, "collision-policy", "reject");
        if (collisionPolicy is not ("reject" or "overwrite" or "keep-both" or "skip"))
        {
            throw new ArgumentException(services.Localization.Get("error.validation-failed"));
        }

        switch (action)
        {
            case "refresh":
                return new("common.operation-completed");
            case "new-tab":
                _directoryTabs.Add(_directoryPath);
                _activeDirectoryTab = _directoryTabs.Count - 1;
                return new("common.operation-completed");
            case "close-tab":
                if (_directoryTabs.Count > 1)
                {
                    _directoryTabs.RemoveAt(_activeDirectoryTab);
                    _activeDirectoryTab = Math.Min(_activeDirectoryTab, _directoryTabs.Count - 1);
                    _directoryPath = _directoryTabs[_activeDirectoryTab];
                }

                return new("common.operation-completed");
            case "create":
            case "create-file":
            case "create-directory":
                {
                    string? name = await services.Interaction.PromptAsync("action." + action, "field.path").ConfigureAwait(true);
                    if (string.IsNullOrWhiteSpace(name))
                    {
                        return new("state.cancelled", null, false);
                    }

                    FilePathRequest request = new(CombineRelative(path, name));
                    _ = action == "create-directory"
                        ? await services.Session.Client.CreateDirectoryAsync(instance.Id, instance.NodeId, request, cancellationToken).ConfigureAwait(false)
                        : await services.Session.Client.CreateFileAsync(instance.Id, instance.NodeId, request, cancellationToken).ConfigureAwait(false);
                    break;
                }
            case "upload":
                {
                    string? source = await services.Interaction.PickOpenFileAsync("*").ConfigureAwait(true);
                    if (source is null)
                    {
                        return new("state.cancelled", null, false);
                    }

                    _ = await UploadFileAsync(
                        instance,
                        source,
                        CombineRelative(path, Path.GetFileName(source)),
                        null,
                        cancellationToken,
                        Boolean(fields, "overwrite", false)).ConfigureAwait(false);
                    return new("common.operation-accepted");
                }
            case "download-from-url":
                {
                    if (!Uri.TryCreate(Value(fields, "url"), UriKind.Absolute, out Uri? url) ||
                        (url.Scheme != Uri.UriSchemeHttp && url.Scheme != Uri.UriSchemeHttps))
                    {
                        throw new ArgumentException(services.Localization.Get("error.validation-failed"));
                    }

                    string destination = CombineRelative(path, Value(fields, "target-file"));
                    _ = await services.Session.Client.DownloadFileFromUrlAsync(
                        instance.Id, instance.NodeId, new DownloadFromUrlRequest(url, destination, Boolean(fields, "overwrite", false)), cancellationToken).ConfigureAwait(false);
                    return new("common.operation-accepted");
                }
            case "edit-text":
                {
                    FileEntryDto file = entry is { IsDirectory: false } ? entry : throw SelectionRequired();
                    _selectedTextFile = await services.Session.Client.ReadTextFileAsync(
                        instance.Id, instance.NodeId, file.Path, null, cancellationToken).ConfigureAwait(false);
                    return new("state.ready");
                }
            case "preview":
                {
                    FileEntryDto file = entry is { IsDirectory: false } ? entry : throw SelectionRequired();
                    string extension = Path.GetExtension(file.Path);
                    if (extension.Equals(".png", StringComparison.OrdinalIgnoreCase) ||
                        extension.Equals(".jpg", StringComparison.OrdinalIgnoreCase) ||
                        extension.Equals(".jpeg", StringComparison.OrdinalIgnoreCase) ||
                        extension.Equals(".gif", StringComparison.OrdinalIgnoreCase) ||
                        extension.Equals(".bmp", StringComparison.OrdinalIgnoreCase) ||
                        extension.Equals(".webp", StringComparison.OrdinalIgnoreCase))
                    {
                        if (file.Size > 16 * 1024 * 1024)
                        {
                            throw new ArgumentException(services.Localization.Get("error.file-too-large"));
                        }

                        using MemoryStream image = new((int)file.Size);
                        await services.Session.Client.DownloadFileAsync(
                            instance.Id, instance.NodeId, file.Path, image, null, null, cancellationToken).ConfigureAwait(false);
                        await services.Interaction.ShowImageAsync(file.Path, image.ToArray()).ConfigureAwait(true);
                    }
                    else
                    {
                        _selectedTextFile = await services.Session.Client.ReadTextFileAsync(
                            instance.Id, instance.NodeId, file.Path, Value(fields, "encoding", "utf-8"), cancellationToken).ConfigureAwait(false);
                    }

                    return new("state.ready");
                }
            case "save-text":
                {
                    TextFileDto file = _selectedTextFile ?? throw SelectionRequired();
                    _selectedTextFile = await services.Session.Client.WriteTextFileAsync(
                        instance.Id,
                        instance.NodeId,
                        file.Path,
                        new WriteTextFileRequest(Value(fields, "content"), Value(fields, "encoding", file.Encoding)),
                        cancellationToken).ConfigureAwait(false);
                    return new("common.operation-completed");
                }
            case "download":
                {
                    FileEntryDto file = entry is { IsDirectory: false } ? entry : throw SelectionRequired();
                    string? target = await services.Interaction.PickSaveFileAsync(Path.GetFileName(file.Path), Path.GetExtension(file.Path).Length == 0 ? ".bin" : Path.GetExtension(file.Path)).ConfigureAwait(true);
                    if (target is null)
                    {
                        return new("state.cancelled", null, false);
                    }

                    var stopwatch = System.Diagnostics.Stopwatch.StartNew();
                    string partialTarget = $"{target}.servitium-{Guid.NewGuid():N}.part";
                    ReportTransfer(Path.GetFileName(file.Path), 0, file.Size, stopwatch, "downloading");
                    try
                    {
                        FileStream destination = new(partialTarget, FileMode.CreateNew, FileAccess.Write, FileShare.None, 128 * 1024, FileOptions.Asynchronous | FileOptions.WriteThrough);
                        await using (destination.ConfigureAwait(false))
                        {
                            using ProgressWriteStream progress = new(
                                destination,
                                _transferPause,
                                completed => ReportTransfer(Path.GetFileName(file.Path), completed, file.Size, stopwatch, "downloading"));
                            await services.Session.Client.DownloadFileAsync(
                                instance.Id, instance.NodeId, file.Path, progress, null, null, cancellationToken).ConfigureAwait(false);
                            await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
                        }

                        File.Move(partialTarget, target, true);
                        ReportTransfer(Path.GetFileName(file.Path), file.Size, file.Size, stopwatch, "succeeded");
                    }
                    catch
                    {
                        if (File.Exists(partialTarget))
                        {
                            File.Delete(partialTarget);
                        }

                        ReportTransfer(
                            Path.GetFileName(file.Path),
                            0,
                            file.Size,
                            stopwatch,
                            cancellationToken.IsCancellationRequested ? "cancelled" : "failed");
                        throw;
                    }
                    break;
                }
            case "rename":
                {
                    FileEntryDto source = entry ?? throw SelectionRequired();
                    string? name = await services.Interaction.PromptAsync("action.rename", "field.name", Path.GetFileName(source.Path)).ConfigureAwait(true);
                    if (string.IsNullOrWhiteSpace(name))
                    {
                        return new("state.cancelled", null, false);
                    }

                    _ = await services.Session.Client.RenameFileAsync(instance.Id, instance.NodeId, new RenameFileRequest(source.Path, name), cancellationToken).ConfigureAwait(false);
                    break;
                }
            case "copy":
            case "cut":
                {
                    if (selectedEntries.Length == 0)
                    {
                        throw SelectionRequired();
                    }

                    _fileClipboard = new FileClipboardPlan(
                        action == "cut" ? "move" : "copy",
                        [.. selectedEntries.Select(source => source.Path)]);
                    return new("file.clipboard-ready", null, false);
                }
            case "paste":
                {
                    FileClipboardPlan clipboard = _fileClipboard ?? throw SelectionRequired();
                    foreach ((string source, string destination) in clipboard.Destinations(path))
                    {
                        FileTransferRequest request = new(source, destination, collisionPolicy);
                        _ = clipboard.Operation == "copy"
                            ? await services.Session.Client.CopyFilesAsync(instance.Id, instance.NodeId, request, cancellationToken).ConfigureAwait(false)
                            : await services.Session.Client.MoveFilesAsync(instance.Id, instance.NodeId, request, cancellationToken).ConfigureAwait(false);
                    }

                    if (clipboard.Operation == "move")
                    {
                        _fileClipboard = null;
                    }

                    return new("common.operation-accepted");
                }
            case "move":
                {
                    FileEntryDto source = entry ?? throw SelectionRequired();
                    string? destination = await services.Interaction.PromptAsync("action." + action, "field.path", source.Path).ConfigureAwait(true);
                    if (string.IsNullOrWhiteSpace(destination))
                    {
                        return new("state.cancelled", null, false);
                    }

                    FileTransferRequest request = new(source.Path, destination, collisionPolicy);
                    _ = await services.Session.Client.MoveFilesAsync(instance.Id, instance.NodeId, request, cancellationToken).ConfigureAwait(false);
                    return new("common.operation-accepted");
                }
            case "delete":
                {
                    if (selectedEntries.Length == 0)
                    {
                        throw SelectionRequired();
                    }

                    if (!await services.Interaction.ConfirmAsync(
                            "action.delete",
                            services.Localization.Format("danger.delete-files-impact", selectedEntries.Length)).ConfigureAwait(true))
                    {
                        return new("state.cancelled", null, false);
                    }

                    _ = await services.Session.Client.DeleteFilesAsync(
                        instance.Id,
                        instance.NodeId,
                        new FileBatchRequest([.. selectedEntries.Select(source => source.Path)]),
                        cancellationToken).ConfigureAwait(false);
                    return new("common.operation-accepted");
                }
            case "compress":
                {
                    if (selectedEntries.Length == 0)
                    {
                        throw SelectionRequired();
                    }

                    string? destination = await services.Interaction.PromptAsync(
                        "action.compress",
                        "field.path",
                        selectedEntries.Length == 1 ? selectedEntries[0].Path + ".zip" : CombineRelative(path, "archive.zip")).ConfigureAwait(true);
                    if (string.IsNullOrWhiteSpace(destination))
                    {
                        return new("state.cancelled", null, false);
                    }

                    _ = await services.Session.Client.CompressFilesAsync(
                        instance.Id,
                        instance.NodeId,
                        new CompressFilesRequest(
                            [.. selectedEntries.Select(source => source.Path)],
                            destination,
                            Value(fields, "entry-name-encoding", "utf-8")),
                        cancellationToken).ConfigureAwait(false);
                    return new("common.operation-accepted");
                }
            case "extract":
                {
                    FileEntryDto source = entry is { IsDirectory: false } ? entry : throw SelectionRequired();
                    string? destination = await services.Interaction.PromptAsync("action.extract", "field.path", path).ConfigureAwait(true);
                    if (destination is null)
                    {
                        return new("state.cancelled", null, false);
                    }

                    _ = await services.Session.Client.ExtractArchiveAsync(
                        instance.Id,
                        instance.NodeId,
                        new ExtractArchiveRequest(source.Path, destination, Value(fields, "extract-encoding", "auto")),
                        cancellationToken).ConfigureAwait(false);
                    return new("common.operation-accepted");
                }
            default:
                throw new ArgumentOutOfRangeException(nameof(action));
        }

        return new("common.operation-completed");
    }

    private async Task<PageActionResult> SaveConfigurationAsync(IReadOnlyDictionary<string, string> fields, CancellationToken cancellationToken)
    {
        InstanceDto instance = RequireInstance();
        ConfigurationDto configuration = _selectedConfiguration ?? throw SelectionRequired();
        IReadOnlyDictionary<string, string?>? values = ConfigurationRawMode
            ? null
            : fields.Where(pair => pair.Key.StartsWith("config:", StringComparison.Ordinal)).ToDictionary(
                pair => pair.Key[7..], pair => (string?)pair.Value, StringComparer.Ordinal);
        _ = await services.Session.Client.WriteConfigurationAsync(
            instance.Id,
            instance.NodeId,
            configuration.Key,
            new WriteConfigurationRequest(values, ConfigurationRawMode ? Value(fields, "content") : null, _configurationRevision ?? string.Empty),
            cancellationToken).ConfigureAwait(false);
        return new("common.operation-completed");
    }

    private Task<PageActionResult> ConfigurationListActionAsync(string action)
    {
        ConfigurationDto configuration = _selectedConfiguration ?? throw SelectionRequired();
        if (action == "open-file-manager")
        {
            _directoryPath = Path.GetDirectoryName(configuration.RelativePath)?.Replace('\\', '/') ?? string.Empty;
            return Task.FromResult(new PageActionResult(null, "PG-013", false));
        }

        return Task.FromResult(new PageActionResult(null, "PG-015", false));
    }

    private Task<PageActionResult> ConfigurationEditorActionAsync(
        string action,
        IReadOnlyDictionary<string, string> fields,
        CancellationToken cancellationToken)
    {
        if (action == "save")
        {
            return SaveConfigurationAsync(fields, cancellationToken);
        }

        if (action == "toggle-view")
        {
            if (ConfigurationSchema.Count == 0)
            {
                throw new InvalidOperationException(services.Localization.Get("error.config-parse-failed"));
            }

            ToggleConfigurationView();
        }

        return Task.FromResult(new PageActionResult("state.ready"));
    }

    private async Task<PageActionResult> RestoreConfigurationAsync(CancellationToken cancellationToken)
    {
        InstanceDto instance = RequireInstance();
        ConfigurationBackupDto backup = _selectedConfigurationBackup ?? throw SelectionRequired();
        ConfigurationDto configuration = _selectedConfiguration ?? throw SelectionRequired();
        _ = await services.Session.Client.RestoreConfigurationBackupAsync(
            instance.Id, instance.NodeId, configuration.Key, backup.Id, cancellationToken).ConfigureAwait(false);
        return new("common.operation-completed");
    }

    private async Task<PageActionResult> UploadModAsync(IReadOnlyDictionary<string, string> fields, CancellationToken cancellationToken)
    {
        InstanceDto instance = RequireInstance();
        string kind = Value(fields, "type", "mods");
        string? source = await services.Interaction.PickOpenFileAsync(".jar").ConfigureAwait(true);
        if (source is null)
        {
            return new("state.cancelled", null, false);
        }

        _ = await UploadFileAsync(
            instance, source, CombineRelative(kind, Path.GetFileName(source)), kind, cancellationToken).ConfigureAwait(false);
        return new("common.operation-accepted");
    }

    private async Task<PageActionResult> ModNavigationActionAsync(string action, CancellationToken cancellationToken)
    {
        InstanceDto instance = RequireInstance();
        ModDetailsDto mod = _selected as ModDetailsDto ?? throw SelectionRequired();
        if (action == "details")
        {
            await services.Interaction.ShowAsync(
                "action.details",
                string.Join(Environment.NewLine, new[] { mod.Name, mod.Version, mod.Loader, mod.RelativePath }.Where(value => !string.IsNullOrWhiteSpace(value)))).ConfigureAwait(true);
            return new("common.operation-completed", null, false);
        }

        string kind = mod.RelativePath.StartsWith("plugins/", StringComparison.OrdinalIgnoreCase) ? "plugins" : "mods";
        Contracts.Transport.CursorPage<string> files = await services.Session.Client.ListModConfigurationFilesAsync(
            instance.Id, instance.NodeId, kind, mod.Id, cancellationToken).ConfigureAwait(false);
        string? first = (files.Items.Count == 0 ? null : files.Items[0]) ?? throw new InvalidOperationException(services.Localization.Get("common.empty"));
        _directoryPath = Path.GetDirectoryName(first)?.Replace('\\', '/') ?? string.Empty;
        return new(null, "PG-013", false);
    }

    private async Task<PageActionResult> ModActionAsync(
        string action,
        IReadOnlyDictionary<string, string> fields,
        CancellationToken cancellationToken)
    {
        InstanceDto instance = RequireInstance();
        ModDetailsDto mod = _selected as ModDetailsDto ?? throw SelectionRequired();
        string kind = Value(fields, "type", "mods");
        if (action == "delete")
        {
            if (!await services.Interaction.ConfirmAsync("action.delete", mod.Name).ConfigureAwait(true))
            {
                return new("state.cancelled", null, false);
            }

            await services.Session.Client.DeleteModAsync(instance.Id, instance.NodeId, kind, mod.Id, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            _ = action == "enable"
                ? await services.Session.Client.EnableModAsync(instance.Id, instance.NodeId, kind, mod.Id, cancellationToken).ConfigureAwait(false)
                : await services.Session.Client.DisableModAsync(instance.Id, instance.NodeId, kind, mod.Id, cancellationToken).ConfigureAwait(false);
        }

        return new("common.operation-completed");
    }

    private async Task<PageActionResult> ScheduleActionAsync(string action, IReadOnlyDictionary<string, string> fields, CancellationToken cancellationToken)
    {
        InstanceDto instance = RequireInstance();
        var task = _selected as ScheduledTaskDetailsDto;
        if (action is "refresh" or "edit")
        {
            return new("common.operation-completed");
        }

        if (action == "view-history")
        {
            ScheduledTaskDetailsDto selectedTask = task ?? throw SelectionRequired();
            Contracts.Transport.CursorPage<ScheduledTaskRunDto> runs = await services.Session.Client.ListScheduleRunsAsync(
                instance.Id, selectedTask.Id, cancellationToken).ConfigureAwait(false);
            string content = runs.Items.Count == 0
                ? services.Localization.Get("common.empty")
                : string.Join(Environment.NewLine, runs.Items.Select(run =>
                    run.StartedAt.ToLocalTime().ToString("G", CultureInfo.CurrentCulture) + " · " + run.OutcomeCode));
            await services.Interaction.ShowAsync("action.view-history", content).ConfigureAwait(true);
            return new("common.operation-completed", null, false);
        }

        if (action is "create" or "save")
        {
            UpsertScheduledTaskRequest request = ScheduleRequest(fields, task?.Revision);
            _ = action == "create"
                ? await services.Session.Client.CreateScheduleAsync(instance.Id, request, cancellationToken).ConfigureAwait(false)
                : await services.Session.Client.UpdateScheduleAsync(instance.Id, (task ?? throw SelectionRequired()).Id, request, cancellationToken).ConfigureAwait(false);
        }
        else if (action == "run-now")
        {
            _ = await services.Session.Client.RunScheduleAsync(instance.Id, (task ?? throw SelectionRequired()).Id, cancellationToken).ConfigureAwait(false);
        }
        else if (action == "enable")
        {
            _ = await services.Session.Client.EnableScheduleAsync(instance.Id, (task ?? throw SelectionRequired()).Id, cancellationToken).ConfigureAwait(false);
        }
        else if (action == "disable")
        {
            _ = await services.Session.Client.DisableScheduleAsync(instance.Id, (task ?? throw SelectionRequired()).Id, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            ScheduledTaskDetailsDto selectedTask = task ?? throw SelectionRequired();
            if (!await services.Interaction.ConfirmNamedAsync(
                    "action.delete", selectedTask.Name, services.Localization.Get("danger.delete-schedule-impact")).ConfigureAwait(true))
            {
                return new("state.cancelled", null, false);
            }

            await services.Session.Client.DeleteScheduleAsync(instance.Id, selectedTask.Id, cancellationToken).ConfigureAwait(false);
        }

        return new("common.operation-completed");
    }

    private async Task<PageActionResult> SaveInstanceAsync(IReadOnlyDictionary<string, string> fields, CancellationToken cancellationToken)
    {
        InstanceDto instance = RequireInstance();
        InstanceDetailsDto details = await services.Session.Client.GetInstanceAsync(instance.Id, cancellationToken).ConfigureAwait(false);
        UpdateInstanceRequest before = details.Configuration ?? DefaultInstanceConfiguration(details);
        Guid runtimeId = Guid.TryParse(Value(fields, "java-runtime"), out Guid parsedRuntime) ? parsedRuntime : before.Launch.JavaRuntimeId;
        Guid? updateChannel = Guid.TryParse(Value(fields, "update-channel"), out Guid parsedChannel) ? parsedChannel : null;
        Dictionary<string, string?> environment = ParseKeyValues(Value(fields, "environment-variables"));
        ShortcutCommandRequest[] shortcuts = [.. ParseKeyValues(Value(fields, "shortcut-commands")).Select(pair => new ShortcutCommandRequest(pair.Key, pair.Value ?? string.Empty))];
        UpdateInstanceRequest request = before with
        {
            Name = Value(fields, "name", details.Instance.Name),
            Tags = Split(fields, "tags"),
            Notes = Value(fields, "notes"),
            WorkingDirectory = Value(fields, "working-directory", details.Instance.WorkingDirectory),
            StopCommand = Value(fields, "stop-command", before.StopCommand),
            StopTimeoutSeconds = Integer(fields, "stop-timeout", before.StopTimeoutSeconds),
            PrimaryPort = NullableInteger(fields, "primary-port"),
            AutoStart = Boolean(fields, "auto-start", before.AutoStart),
            AutoStartDelaySeconds = Integer(fields, "auto-start-delay", before.AutoStartDelaySeconds),
            AutoRestart = Boolean(fields, "auto-restart", before.AutoRestart),
            MaximumRestartAttempts = Integer(fields, "maximum-restart-attempts", before.MaximumRestartAttempts),
            RestartIntervalSeconds = Integer(fields, "restart-interval", before.RestartIntervalSeconds),
            RestartResetSeconds = Integer(fields, "restart-reset", before.RestartResetSeconds),
            RunCredentialAlias = Value(fields, "run-credential-alias"),
            Launch = new StructuredLaunchRequest(
                runtimeId,
                Integer(fields, "minimum-heap", before.Launch.MinimumHeapMegabytes),
                Integer(fields, "maximum-heap", before.Launch.MaximumHeapMegabytes),
                Split(fields, "jvm-arguments"),
                Value(fields, "target-kind", before.Launch.TargetKind),
                Value(fields, "launch", before.Launch.Target),
                Split(fields, "server-arguments"),
                environment.ToDictionary(pair => pair.Key, pair => pair.Value ?? string.Empty, StringComparer.Ordinal),
                Boolean(fields, "advanced-mode", before.Launch.AdvancedMode),
                Split(fields, "advanced-arguments")),
            Terminal = new TerminalSettingsRequest(
                Boolean(fields, "terminal-conpty", before.Terminal?.UseConPty ?? true),
                Integer(fields, "terminal-columns", before.Terminal?.Columns ?? 164),
                Integer(fields, "terminal-rows", before.Terminal?.Rows ?? 40),
                Value(fields, "terminal-input-encoding", before.Terminal?.InputEncoding ?? "UTF-8"),
                Value(fields, "terminal-output-encoding", before.Terminal?.OutputEncoding ?? "UTF-8"),
                Value(fields, "terminal-newline", before.Terminal?.NewLine ?? "CRLF"),
                Boolean(fields, "terminal-ansi", before.Terminal?.AnsiEnabled ?? true)),
            Rcon = new RconSettingsRequest(
                Boolean(fields, "rcon-enabled", before.Rcon.Enabled),
                Integer(fields, "rcon-port", before.Rcon.Port),
                Value(fields, "rcon-password") is string password && password.Length > 0 ? password : null),
            Query = new QuerySettingsRequest(
                Boolean(fields, "query-enabled", before.Query.Enabled),
                Value(fields, "query-host", before.Query.Host),
                Integer(fields, "query-port", before.Query.Port)),
            UpdateAction = new UpdateActionRequest(
                Value(fields, "update-kind", before.UpdateAction?.Kind ?? "none"),
                Value(fields, "update-source"),
                Value(fields, "update-target"),
                updateChannel),
            ShortcutCommands = shortcuts,
        };
        _ = await services.Session.Client.UpdateInstanceAsync(instance.Id, request, cancellationToken).ConfigureAwait(false);
        return new("common.operation-completed");
    }

    private async Task<PageActionResult> DeleteWorkingDirectoryAsync(CancellationToken cancellationToken)
    {
        InstanceDto instance = RequireInstance();
        if (!await services.Interaction.ConfirmNamedAsync(
                "action.delete-working-directory",
                instance.Name,
                services.Localization.Format("danger.delete-directory-impact", instance.WorkingDirectory)).ConfigureAwait(true))
        {
            return new("state.cancelled", null, false);
        }

        _ = await services.Session.Client.DeleteInstanceWorkingDirectoryAsync(instance.Id, instance.NodeId, cancellationToken).ConfigureAwait(false);
        return new("common.operation-accepted");
    }

    private async Task<PageActionResult> InstanceCreationActionAsync(string action, IReadOnlyDictionary<string, string> fields, CancellationToken cancellationToken)
    {
        string sourceKind = Value(fields, "source-kind", "empty");
        string expectedAction = sourceKind switch
        {
            "empty" => "create-empty",
            "archive" => "import-server-archive",
            "directory" => "attach-directory",
            _ => string.Empty,
        };
        if (!string.Equals(action, expectedAction, StringComparison.Ordinal) ||
            !Guid.TryParse(Value(fields, "node"), out Guid nodeId))
        {
            throw new ArgumentException(services.Localization.Get("error.validation-failed"));
        }

        NodeDto node = services.Session.Nodes.FirstOrDefault(candidate => candidate.Id == nodeId)
            ?? throw new ArgumentException(services.Localization.Get("error.validation-failed"));
        services.Session.SelectedNode = node;
        string name = Value(fields, "name");
        string stopCommand = Value(fields, "stop-command", "stop");
        int stopTimeout = Integer(fields, "stop-timeout", 60);
        string[] tags = Split(fields, "tags");
        string? notes = NullIfEmpty(Value(fields, "notes"));
        if (name.Length == 0 || stopCommand.Length == 0 || stopCommand.Any(char.IsControl) || stopTimeout is < 1 or > 600)
        {
            throw new ArgumentException(services.Localization.Get("error.validation-failed"));
        }

        StructuredLaunchRequest launch = CreateLaunch(fields);
        bool owned = action is not "attach-directory";
        if (!await services.Interaction.ConfirmAsync(
                "instance.create.step.review",
                services.Localization.Get(owned ? "instance.create.owned-message" : "instance.create.external-message")).ConfigureAwait(true))
        {
            return new("state.cancelled", null, false);
        }

        if (action == "create-empty")
        {
            _ = await services.Session.Client.CreateInstanceAsync(
                new CreateInstanceRequest(name, node.Id, launch, stopCommand, stopTimeout, tags, notes), cancellationToken).ConfigureAwait(false);
        }
        else if (action == "attach-directory")
        {
            string path = Value(fields, "absolute-path");
            if (string.IsNullOrWhiteSpace(path))
            {
                path = await services.Interaction.PickFolderAsync().ConfigureAwait(true) ?? string.Empty;
            }

            if (path.Length == 0)
            {
                return new("state.cancelled", null, false);
            }

            _ = await services.Session.Client.AttachInstanceDirectoryAsync(
                new AttachInstanceDirectoryRequest(name, node.Id, path, launch, stopCommand, stopTimeout, tags, notes), cancellationToken).ConfigureAwait(false);
        }
        else if (action == "import-server-archive")
        {
            string path = Value(fields, "archive");
            if (string.IsNullOrWhiteSpace(path))
            {
                path = await services.Interaction.PickOpenFileAsync(".zip").ConfigureAwait(true) ?? string.Empty;
            }

            if (path.Length == 0)
            {
                return new("state.cancelled", null, false);
            }

            _ = await UploadStreamAsync(
                path,
                (stream, token) => services.Session.Client.ImportServerArchiveAsync(
                    stream, Path.GetFileName(path), name, node.Id, launch, stopCommand, stopTimeout, tags, notes, token),
                cancellationToken).ConfigureAwait(false);
        }
        else
        {
            throw new ArgumentOutOfRangeException(nameof(action));
        }

        return new("common.operation-accepted");
    }

    private async Task<PageActionResult> CreateNodeTokenAsync(
        IReadOnlyDictionary<string, string> fields,
        CancellationToken cancellationToken)
    {
        string expectedName = Value(fields, "registration-name");
        int lifetimeMinutes = Integer(fields, "registration-lifetime", 30);
        if (expectedName.Length == 0 || lifetimeMinutes is < 1 or > 1440)
        {
            throw new ArgumentException(services.Localization.Get("error.validation-failed"));
        }
        NodeRegistrationTokenResponse token = await services.Session.Client.CreateNodeRegistrationTokenAsync(
            new(expectedName, lifetimeMinutes), cancellationToken).ConfigureAwait(false);
        var instructions = NodeRegistrationInstructions.Create(
            services.Session.Client.BaseAddress,
            expectedName,
            token.Token,
            token.ExpiresAt);
        await services.Interaction.ShowNodeRegistrationAsync(instructions).ConfigureAwait(true);
        return new("common.operation-completed", null, false);
    }

    private async Task<PageActionResult> NodeActionAsync(string action, CancellationToken cancellationToken)
    {
        NodeDto node = RequireNode();
        if (action == "view-node-instances")
        {
            services.Session.PendingInstanceNodeFilter = node.Id;
            return new(null, "PG-010", false);
        }

        if (action == "view-java")
        {
            return new(null, "PG-022", false);
        }

        if (action == "reconnect")
        {
            await services.Session.Client.ReconnectNodeAsync(node.Id, cancellationToken).ConfigureAwait(false);
            return new("common.operation-completed");
        }

        if (action == "rotate-credential")
        {
            if (!await services.Interaction.ConfirmAsync(
                    "action.rotate-credential", services.Localization.Get("danger.rotate-node-credential-impact")).ConfigureAwait(true))
            {
                return new("state.cancelled", null, false);
            }

            _ = await services.Session.Client.RotateNodeCredentialAsync(node.Id, cancellationToken).ConfigureAwait(false);
            return new("common.operation-completed");
        }

        if (action == "revoke")
        {
            return await RevokeNodeCredentialAsync(cancellationToken).ConfigureAwait(false);
        }

        Contracts.Transport.CursorPage<InstanceDto> instances = await services.Session.Client.ListInstancesAsync(
            node.Id, null, null, [], null, 100, cancellationToken).ConfigureAwait(false);
        string impact = services.Localization.Format("danger.remove-node-impact", instances.Items.Count);
        if (!await services.Interaction.ConfirmNamedAsync("action.remove-node", node.Name, impact).ConfigureAwait(true))
        {
            return new("state.cancelled", null, false);
        }

        await services.Session.Client.RemoveNodeAsync(node.Id, false, cancellationToken).ConfigureAwait(false);
        await services.Session.RefreshSelectionsAsync(cancellationToken).ConfigureAwait(false);
        return new("common.operation-completed");
    }

    private async Task<PageActionResult> SaveNodeAsync(IReadOnlyDictionary<string, string> fields, CancellationToken cancellationToken)
    {
        NodeDto node = RequireNode();
        NodeDetailsDto details = await services.Session.Client.GetNodeAsync(node.Id, cancellationToken).ConfigureAwait(false);
        Dictionary<string, string?> mappings = ParseKeyValues(Value(fields, "address-mappings"));
        UpdateNodeRequest request = details.Configuration with
        {
            Name = Value(fields, "name", details.Name),
            DefaultInstancePath = Value(fields, "default-instance-path"),
            Notes = Value(fields, "notes"),
            Tags = Split(fields, "tags"),
            PublicAddress = Value(fields, "public-address"),
            AddressMappings = [.. mappings.Select(pair => new NodeAddressMappingRequest(pair.Key, pair.Value ?? string.Empty))],
            JavaDataDirectory = Value(fields, "java-data-directory"),
            UploadBytesPerSecond = LongInteger(fields, "upload-rate", details.Configuration.UploadBytesPerSecond),
            DownloadBytesPerSecond = LongInteger(fields, "download-rate", details.Configuration.DownloadBytesPerSecond),
            UrlDownloadConcurrency = Integer(fields, "url-download-concurrency", details.Configuration.UrlDownloadConcurrency),
            FileTaskConcurrency = Integer(fields, "file-task-concurrency", details.Configuration.FileTaskConcurrency),
            CompressionTaskConcurrency = Integer(fields, "compression-task-concurrency", details.Configuration.CompressionTaskConcurrency),
            TerminalBufferBytes = LongInteger(fields, "terminal-buffer-bytes", details.Configuration.TerminalBufferBytes),
            TerminalBufferLines = Integer(fields, "terminal-buffer-lines", details.Configuration.TerminalBufferLines),
            GracefulShutdownEnabled = Boolean(fields, "graceful-shutdown", details.Configuration.GracefulShutdownEnabled),
            ShutdownWaitSeconds = Integer(fields, "shutdown-wait", details.Configuration.ShutdownWaitSeconds),
            MetricIntervalSeconds = Integer(fields, "metric-interval", details.Configuration.MetricIntervalSeconds),
            MaximumSchedulesPerInstance = Integer(fields, "maximum-schedules", details.Configuration.MaximumSchedulesPerInstance),
            AdvancedRawCommandsEnabled = Boolean(fields, "advanced-raw-commands", details.Configuration.AdvancedRawCommandsEnabled),
            PrivateSourceAuthorities = Split(fields, "private-source-authorities"),
        };
        _ = await services.Session.Client.UpdateNodeAsync(node.Id, request, cancellationToken).ConfigureAwait(false);
        return new("common.operation-completed");
    }

    private async Task<PageActionResult> RevokeNodeCredentialAsync(CancellationToken cancellationToken)
    {
        NodeDto node = RequireNode();
        if (!await services.Interaction.ConfirmAsync("action.revoke", node.Name).ConfigureAwait(true))
        {
            return new("state.cancelled", null, false);
        }

        await services.Session.Client.RevokeNodeCredentialAsync(node.Id, cancellationToken).ConfigureAwait(false);
        return new("common.operation-completed");
    }

    private async Task<PageActionResult> JavaActionAsync(string action, IReadOnlyDictionary<string, string> fields, CancellationToken cancellationToken)
    {
        NodeDto node = RequireNode();
        if (action == "refresh")
        {
            return new("common.operation-completed");
        }

        if (action == "discover")
        {
            _ = await services.Session.Client.DiscoverJavaRuntimesAsync(node.Id, cancellationToken).ConfigureAwait(false);
            return new("common.operation-accepted");
        }

        if (action == "register")
        {
            _ = await services.Session.Client.RegisterJavaRuntimeAsync(node.Id, new RegisterJavaRuntimeRequest(Value(fields, "path")), cancellationToken).ConfigureAwait(false);
            return new("common.operation-completed");
        }

        if (action == "import")
        {
            string path = Value(fields, "archive");
            if (string.IsNullOrWhiteSpace(path))
            {
                path = await services.Interaction.PickOpenFileAsync(".zip", ".gz").ConfigureAwait(true) ?? string.Empty;
            }

            _ = await UploadStreamAsync(
                path,
                (stream, token) => services.Session.Client.ImportJavaRuntimeAsync(node.Id, stream, Path.GetFileName(path), token),
                cancellationToken).ConfigureAwait(false);
            return new("common.operation-accepted");
        }

        JavaRuntimeDto runtime = _selected as JavaRuntimeDto ?? throw SelectionRequired();
        JavaRuntimeDetailsDto details = await services.Session.Client.GetJavaRuntimeAsync(node.Id, runtime.Id, cancellationToken).ConfigureAwait(false);
        if (action == "view-runtime-instances")
        {
            string content = details.UsedByInstances.Count == 0
                ? services.Localization.Get("common.empty")
                : string.Join(Environment.NewLine, details.UsedByInstances);
            await services.Interaction.ShowAsync("action.view-runtime-instances", content).ConfigureAwait(true);
            return new("common.operation-completed", null, false);
        }

        bool deleteFiles = action == "delete-runtime-files";
        string displayName = runtime.DisplayName ?? runtime.Version;
        string impact = services.Localization.Format(
            deleteFiles ? "danger.delete-java-files-impact" : "danger.unregister-java-impact",
            details.UsedByInstances.Count);
        bool confirmed = deleteFiles
            ? await services.Interaction.ConfirmNamedAsync("action.delete-runtime-files", displayName, impact).ConfigureAwait(true)
            : await services.Interaction.ConfirmAsync("action.unregister", impact).ConfigureAwait(true);
        if (!confirmed)
        {
            return new("state.cancelled", null, false);
        }

        _ = await services.Session.Client.UnregisterJavaRuntimeAsync(node.Id, runtime.Id, deleteFiles, false, cancellationToken).ConfigureAwait(false);
        return new("common.operation-accepted");
    }

    private async Task<PageActionResult> UserActionAsync(string action, IReadOnlyDictionary<string, string> fields, CancellationToken cancellationToken)
    {
        if (action == "refresh")
        {
            return new("common.operation-completed");
        }

        if (action == "create")
        {
            UserDto created = await services.Session.Client.CreateUserAsync(new CreateUserRequest(
                Value(fields, "user-name"), Value(fields, "display-name"), Value(fields, "password"), Value(fields, "role", "User")), cancellationToken).ConfigureAwait(false);
            string sid = Value(fields, "windows-sid");
            if (!string.IsNullOrWhiteSpace(sid))
            {
                _ = await services.Session.Client.SetWindowsIdentityAsync(created.Id, new WindowsIdentityBindingDto(sid), cancellationToken).ConfigureAwait(false);
            }
            return new("common.operation-completed");
        }

        UserDto user = RequireUser();
        switch (action)
        {
            case "manage-grants":
                return new(null, "PG-031", false);
            case "edit":
                _ = await services.Session.Client.UpdateUserAsync(
                    user.Id, new UpdateUserRequest(Value(fields, "display-name", user.DisplayName), Value(fields, "role", user.Role)), cancellationToken).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(Value(fields, "password")))
                {
                    await services.Session.Client.ResetUserPasswordAsync(
                        user.Id, new ResetPasswordRequest(Value(fields, "password")), cancellationToken).ConfigureAwait(false);
                }
                if (!string.IsNullOrWhiteSpace(Value(fields, "windows-sid")))
                {
                    _ = await services.Session.Client.SetWindowsIdentityAsync(
                        user.Id, new WindowsIdentityBindingDto(Value(fields, "windows-sid")), cancellationToken).ConfigureAwait(false);
                }
                break;
            case "reset-totp":
                await services.Session.Client.ResetUserTotpAsync(user.Id, cancellationToken).ConfigureAwait(false);
                break;
            case "revoke-all-sessions":
                if (!await services.Interaction.ConfirmAsync(
                        "action.revoke-all-sessions", services.Localization.Get("danger.revoke-all-sessions-impact")).ConfigureAwait(true))
                {
                    return new("state.cancelled", null, false);
                }
                await services.Session.Client.RevokeUserSessionsAsync(user.Id, cancellationToken).ConfigureAwait(false);
                break;
            case "unbind-identity":
                if (!await services.Interaction.ConfirmAsync(
                        "action.unbind-identity", services.Localization.Get("danger.unbind-identity-impact")).ConfigureAwait(true))
                {
                    return new("state.cancelled", null, false);
                }
                await services.Session.Client.RemoveUserExternalIdentityAsync(user.Id, cancellationToken).ConfigureAwait(false);
                break;
            case "enable":
                _ = await services.Session.Client.EnableUserAsync(user.Id, cancellationToken).ConfigureAwait(false);
                break;
            case "disable":
                _ = await services.Session.Client.DisableUserAsync(user.Id, cancellationToken).ConfigureAwait(false);
                break;
            case "delete":
                if (!await services.Interaction.ConfirmNamedAsync(
                        "action.delete",
                        user.UserName,
                        services.Localization.Get("danger.delete-user-impact")).ConfigureAwait(true))
                {
                    return new("state.cancelled", null, false);
                }

                await services.Session.Client.DeleteUserAsync(user.Id, cancellationToken).ConfigureAwait(false);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(action));
        }

        return new("common.operation-completed");
    }

    private async Task<PageActionResult> GrantActionAsync(string action, CancellationToken cancellationToken)
    {
        UserDto user = RequireUser();
        Guid[] instanceIds = [.. _selectedBatch.OfType<InstanceDto>().Select(instance => instance.Id).Distinct()];
        if (instanceIds.Length == 0)
        {
            throw SelectionRequired();
        }

        if (action == "grant")
        {
            _ = await services.Session.Client.AddUserGrantsAsync(user.Id, new AddInstanceGrantsRequest(instanceIds), cancellationToken).ConfigureAwait(false);
        }
        else
        {
            if (!await services.Interaction.ConfirmAsync(
                    "action.revoke",
                    services.Localization.Format("danger.revoke-grants-impact", instanceIds.Length, user.DisplayName)).ConfigureAwait(true))
            {
                return new("state.cancelled", null, false);
            }

            HashSet<Guid> remaining = [.. _currentGrantIds];
            remaining.ExceptWith(instanceIds);
            _ = await services.Session.Client.ReplaceUserGrantsAsync(
                user.Id,
                new ReplaceInstanceGrantsRequest([.. remaining.OrderBy(id => id)]),
                cancellationToken).ConfigureAwait(false);
        }

        return new("common.operation-completed");
    }

    private async Task<PageActionResult> AccountActionAsync(string action, IReadOnlyDictionary<string, string> fields, CancellationToken cancellationToken)
    {
        if (action == "save")
        {
            _ = await services.Session.Client.UpdateProfileAsync(new UpdateProfileRequest(Value(fields, "display-name"), Value(fields, "language", "zh-Hans")), cancellationToken).ConfigureAwait(false);
        }
        else if (action == "change-password")
        {
            await services.Session.Client.ChangePasswordAsync(new ChangePasswordRequest(Value(fields, "current-password"), Value(fields, "new-password")), cancellationToken).ConfigureAwait(false);
        }
        else if (action == "configure-totp")
        {
            TotpSetupDto setup = await services.Session.Client.BeginTotpAsync(cancellationToken).ConfigureAwait(false);
            string? code = await services.Interaction.PromptAsync("action.configure-totp", "field.code", setup.Secret).ConfigureAwait(true);
            if (code is null)
            {
                return new("state.cancelled", null, false);
            }

            TotpRecoveryCodesDto recovery = await services.Session.Client.ConfirmTotpAsync(new ConfirmTotpRequest(code), cancellationToken).ConfigureAwait(false);
            await services.Interaction.ShowAsync("action.configure-totp", string.Join(Environment.NewLine, recovery.RecoveryCodes)).ConfigureAwait(true);
        }
        else if (action == "remove-totp")
        {
            if (!await services.Interaction.ConfirmAsync(
                    "action.remove-totp", services.Localization.Get("danger.remove-totp-impact")).ConfigureAwait(true))
            {
                return new("state.cancelled", null, false);
            }

            await services.Session.Client.RemoveTotpAsync(cancellationToken).ConfigureAwait(false);
        }
        else if (action == "create-api-key")
        {
            string? name = await services.Interaction.PromptAsync("action.create-api-key", "field.name").ConfigureAwait(true);
            if (name is null)
            {
                return new("state.cancelled", null, false);
            }

            CreatedApiKeyDto key = await services.Session.Client.CreateApiKeyAsync(new CreateApiKeyRequest(name, "full", null), cancellationToken).ConfigureAwait(false);
            await services.Interaction.ShowAsync("action.create-api-key", key.PlaintextKey).ConfigureAwait(true);
        }
        else if (action == "revoke-api-key")
        {
            ApiKeyDto key = _selected as ApiKeyDto ?? throw SelectionRequired();
            if (!await services.Interaction.ConfirmAsync("action.revoke-api-key", key.Name).ConfigureAwait(true))
            {
                return new("state.cancelled", null, false);
            }

            await services.Session.Client.RevokeApiKeyAsync(key.Id, cancellationToken).ConfigureAwait(false);
        }
        else if (action == "bind-oidc")
        {
            await services.Session.BindOidcAsync(cancellationToken).ConfigureAwait(false);
        }
        else if (action == "unbind-oidc")
        {
            if (!await services.Interaction.ConfirmAsync(
                    "action.unbind-oidc", services.Localization.Get("danger.unbind-identity-impact")).ConfigureAwait(true))
            {
                return new("state.cancelled", null, false);
            }

            await services.Session.Client.RemoveOwnExternalIdentityAsync(cancellationToken).ConfigureAwait(false);
        }
        else if (action == "revoke-session")
        {
            SessionDto session = _selected as SessionDto ?? throw SelectionRequired();
            if (!await services.Interaction.ConfirmAsync(
                    "action.revoke-session", services.Localization.Get("danger.revoke-session-impact")).ConfigureAwait(true))
            {
                return new("state.cancelled", null, false);
            }
            await services.Session.Client.RevokeSessionAsync(session.Id, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            throw new ArgumentOutOfRangeException(nameof(action));
        }

        return new("common.operation-completed");
    }

    private async Task<PageActionResult> CustomizeDashboardAsync(CancellationToken cancellationToken)
    {
        DashboardLayoutDto layout = await services.Session.Client.GetLayoutAsync("dashboard", cancellationToken).ConfigureAwait(false);
        DashboardLayoutDto? edited = await services.Interaction.ConfigureDashboardLayoutAsync(layout).ConfigureAwait(true);
        if (edited is null)
        {
            return new("state.cancelled", null, false);
        }

        _ = await services.Session.Client.SaveLayoutAsync(
            "dashboard", new UpdateDashboardLayoutRequest(edited.Version, edited.Cards), cancellationToken).ConfigureAwait(false);
        return new("common.operation-completed");
    }

    private async Task<PageActionResult> SettingsActionAsync(string action, IReadOnlyDictionary<string, string> fields, CancellationToken cancellationToken)
    {
        if (action == "export-diagnostics")
        {
            return await ExportAsync(
                "servitium-diagnostics.zip",
                ".zip",
                services.Session.Client.DownloadDiagnosticsAsync,
                cancellationToken).ConfigureAwait(false);
        }

        if (action == "backup-database")
        {
            DatabaseBackupDto backup = await services.Session.Client.BackupDatabaseAsync(cancellationToken).ConfigureAwait(false);
            await services.Interaction.ShowAsync(
                "action.backup-database",
                backup.FileName + " · " + FormatBytes(backup.SizeBytes)).ConfigureAwait(true);
            return new("common.operation-completed", null, false);
        }

        if (action == "reset-dashboard")
        {
            if (!await services.Interaction.ConfirmAsync(
                    "action.reset-dashboard", services.Localization.Get("danger.reset-dashboard-impact")).ConfigureAwait(true))
            {
                return new("state.cancelled", null, false);
            }

            _ = await services.Session.Client.ResetLayoutAsync("PG-040", cancellationToken).ConfigureAwait(false);
            return new("common.operation-completed", null, false);
        }

        if (action == "test")
        {
            string authority = Value(fields, "oidcIssuer");
            SettingsTestResultDto result = string.IsNullOrWhiteSpace(authority)
                ? await services.Session.Client.TestTlsAsync(new TestTlsCertificateRequest(Value(fields, "tlsCertificatePath"), Value(fields, "tlsCertificatePassword")), cancellationToken).ConfigureAwait(false)
                : await services.Session.Client.TestOidcAsync(new TestOidcDiscoveryRequest(authority), cancellationToken).ConfigureAwait(false);
            await services.Interaction.ShowAsync("action.test", result.Code + " · " + result.Summary).ConfigureAwait(true);
            return new(result.Succeeded ? "state.succeeded" : "state.failed", null, false);
        }

        SettingsGroupDto group = _selected as SettingsGroupDto ?? throw SelectionRequired();
        if (group.Group == "identity" && group.Values.Any(pair =>
                pair.Key is "oidcIssuer" or "oidcClientId" or "oidcSubjectClaim" or "oidcUserNameClaim" &&
                !string.Equals(pair.Value, Value(fields, pair.Key), StringComparison.Ordinal)) &&
            !await services.Interaction.ConfirmAsync("action.save", services.Localization.Get("danger.oidc-impact")).ConfigureAwait(true))
        {
            return new("state.cancelled", null, false);
        }

        if (group.Group == "security" &&
            !string.Equals(group.Values.GetValueOrDefault("advancedRawCommandsEnabled"), "true", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(Value(fields, "advancedRawCommandsEnabled"), "true", StringComparison.OrdinalIgnoreCase) &&
            !await services.Interaction.ConfirmAsync(
                "action.save", services.Localization.Get("danger.enable-advanced-commands-impact")).ConfigureAwait(true))
        {
            return new("state.cancelled", null, false);
        }

        if (group.Group == "network" &&
            IPAddress.TryParse(Value(fields, "listenAddress"), out IPAddress? listenAddress) &&
            !IPAddress.IsLoopback(listenAddress) &&
            !string.Equals(group.Values.GetValueOrDefault("listenAddress"), Value(fields, "listenAddress"), StringComparison.Ordinal) &&
            !await services.Interaction.ConfirmAsync(
                "action.save", services.Localization.Get("danger.non-loopback-listen-impact")).ConfigureAwait(true))
        {
            return new("state.cancelled", null, false);
        }

        var values = group.Values.Keys.ToDictionary(
            key => key,
            key => fields.TryGetValue(key, out string? value) ? value : group.Values[key],
            StringComparer.Ordinal);
        _ = await services.Session.Client.UpdateSettingsAsync(group.Group, new UpdateSettingsGroupRequest(values, group.Revision), cancellationToken).ConfigureAwait(false);
        return new("common.operation-completed");
    }

    private Task<PageActionResult> ExportActivityAsync(
        IReadOnlyDictionary<string, string> fields,
        CancellationToken cancellationToken)
    {
        Client.ActivityQuery query = ActivityFilter(fields);
        return ExportAsync("activity.csv", ".csv", (stream, token) => services.Session.Client.ExportActivityLogsAsync(query, stream, token), cancellationToken);
    }

    private Task<PageActionResult> ExportAuditAsync(
        IReadOnlyDictionary<string, string> fields,
        CancellationToken cancellationToken)
    {
        Client.SecurityAuditQuery query = SecurityFilter(fields);
        return ExportAsync("security-audit.csv", ".csv", (stream, token) => services.Session.Client.ExportSecurityAuditAsync(query, stream, token), cancellationToken);
    }

    private async Task<PageActionResult> OperationActionAsync(string action, CancellationToken cancellationToken)
    {
        OperationDetailsDto operation = _selected as OperationDetailsDto ?? throw SelectionRequired();
        if (action == "cancel")
        {
            await services.Session.Client.CancelOperationAsync(operation.Operation.Id, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            string message = operation.Result
                ?? LocalizeOperationValue(operation.Operation.ErrorCode, operation.Operation.Status);
            await services.Interaction.ShowAsync("section.details", message).ConfigureAwait(true);
        }

        return new("common.operation-completed");
    }

    private string LocalizeOperationValue(string? errorCode, string status)
    {
        if (!string.IsNullOrWhiteSpace(errorCode))
        {
            string error = services.Localization.Get("error." + errorCode);
            if (!string.Equals(error, "error." + errorCode, StringComparison.Ordinal))
            {
                return error;
            }
        }

        string localizedStatus = services.Localization.Get("state." + ToKebabCase(status));
        return string.Equals(localizedStatus, "state." + ToKebabCase(status), StringComparison.Ordinal)
            ? services.Localization.Get("common.unknown")
            : localizedStatus;
    }

    private static string ToKebabCase(string value)
    {
        System.Text.StringBuilder result = new(value.Length + 8);
        foreach (char character in value)
        {
            if (character is '-' or '_' or ' ')
            {
                if (result.Length > 0 && result[^1] != '-')
                {
                    _ = result.Append('-');
                }

                continue;
            }

            if (char.IsUpper(character) && result.Length > 0 && result[^1] != '-')
            {
                _ = result.Append('-');
            }

            _ = result.Append(char.ToLowerInvariant(character));
        }

        return result.ToString();
    }

    private async Task<PageActionResult> CreateChannelAsync(IReadOnlyDictionary<string, string> fields, CancellationToken cancellationToken)
    {
        HmclChannelDto channel = await services.Session.Client.CreateHmclChannelAsync(new CreateHmclChannelRequest(
            Value(fields, "slug"), Value(fields, "name"), Value(fields, "visibility", "public")), cancellationToken).ConfigureAwait(false);
        services.Session.SelectedChannel = channel;
        if (channel.Token is not null)
        {
            await services.Interaction.ShowAsync("action.create-channel", channel.Token).ConfigureAwait(true);
        }
        return new("common.operation-completed");
    }

    private async Task<PageActionResult> ChannelActionAsync(
        string action,
        IReadOnlyDictionary<string, string> fields,
        CancellationToken cancellationToken)
    {
        HmclChannelDto channel = RequireChannel();
        if (action == "edit-channel")
        {
            return new(null, "PG-061", false);
        }

        if (action == "save")
        {
            Guid? linkedInstance = Guid.TryParse(Value(fields, "linked-instance"), out Guid parsed) ? parsed : null;
            _ = await services.Session.Client.UpdateHmclChannelAsync(
                channel.Id,
                new UpdateHmclChannelRequest(
                    Value(fields, "name", channel.Name),
                    Value(fields, "description"),
                    ParseInt(fields, "retention-count", 10),
                    linkedInstance),
                cancellationToken).ConfigureAwait(false);
            await services.Session.RefreshSelectionsAsync(cancellationToken).ConfigureAwait(false);
            return new("common.operation-completed");
        }

        if (action == "delete-channel")
        {
            if (!await services.Interaction.ConfirmNamedAsync(
                    "action.delete-channel", channel.Name, services.Localization.Get("danger.delete-channel-impact")).ConfigureAwait(true))
            {
                return new("state.cancelled", null, false);
            }

            _ = await services.Session.Client.DeleteHmclChannelAsync(
                channel.Id, new ConfirmHmclChannelRequest(channel.Name), cancellationToken).ConfigureAwait(false);
            await services.Session.RefreshSelectionsAsync(cancellationToken).ConfigureAwait(false);
            return new("common.operation-accepted", "PG-060", false);
        }

        if (action == "change-visibility")
        {
            string visibility = Value(fields, "visibility", channel.Visibility);
            if (!await services.Interaction.ConfirmNamedAsync(
                    "action.change-visibility", channel.Name, services.Localization.Get("danger.change-visibility-impact")).ConfigureAwait(true))
            {
                return new("state.cancelled", null, false);
            }

            HmclTokenResultDto result = await services.Session.Client.ChangeHmclChannelVisibilityAsync(
                channel.Id, new ChangeHmclVisibilityRequest(visibility, channel.Name), cancellationToken).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(result.Token))
            {
                await services.Interaction.ShowAsync("action.change-visibility", result.Token).ConfigureAwait(true);
            }
            await services.Session.RefreshSelectionsAsync(cancellationToken).ConfigureAwait(false);
            return new("common.operation-completed", null, false);
        }

        if (action == "rotate-token")
        {
            if (!await services.Interaction.ConfirmNamedAsync(
                    "action.rotate-token",
                    channel.Name,
                    services.Localization.Get("danger.rotate-token-impact")).ConfigureAwait(true))
            {
                return new("state.cancelled", null, false);
            }

            HmclTokenResultDto token = await services.Session.Client.RotateHmclChannelTokenAsync(channel.Id, new ConfirmHmclChannelRequest(channel.Name), cancellationToken).ConfigureAwait(false);
            await services.Interaction.ShowAsync("action.rotate-token", token.Token).ConfigureAwait(true);
            return new("common.operation-completed", null, false);
        }

        HmclReleaseSummaryDto release = services.Session.SelectedRelease ?? _selected as HmclReleaseSummaryDto ?? throw SelectionRequired();
        HmclPublishDiffDto preview = await services.Session.Client.PreviewHmclPublishAsync(channel.Id, new PublishHmclChannelRequest(release.Id), cancellationToken).ConfigureAwait(false);
        string summary = "+" + preview.Added.Count.ToString(CultureInfo.CurrentCulture) + " ~" + preview.Replaced.Count.ToString(CultureInfo.CurrentCulture) + " -" + preview.Deleted.Count.ToString(CultureInfo.CurrentCulture);
        bool confirmed = preview.Deleted.Count > 0 || preview.LoaderChanged || action == "rollback"
            ? await services.Interaction.ConfirmNamedAsync(
                "action." + action,
                channel.Name,
                services.Localization.Format("danger.publish-delete-impact", summary)).ConfigureAwait(true)
            : await services.Interaction.ConfirmAsync("action." + action, summary).ConfigureAwait(true);
        if (!confirmed)
        {
            return new("state.cancelled", null, false);
        }

        _ = await services.Session.Client.PublishHmclChannelAsync(channel.Id, new PublishHmclChannelRequest(release.Id), cancellationToken).ConfigureAwait(false);
        return new("common.operation-accepted");
    }

    private async Task<PageActionResult> ReleaseCreationActionAsync(string action, IReadOnlyDictionary<string, string> fields, CancellationToken cancellationToken)
    {
        HmclChannelDto channel = RequireChannel();
        string sourceKind = Value(fields, "source-kind", "archive");
        string path = Value(fields, sourceKind == "prism" ? "prism-archive" : "archive");
        if (action == "inspect")
        {
            if (sourceKind != "prism")
            {
                throw new InvalidOperationException(services.Localization.Get("hmcl.preview.prism-only"));
            }

            if (string.IsNullOrWhiteSpace(path))
            {
                path = await services.Interaction.PickOpenFileAsync(".zip").ConfigureAwait(true) ?? string.Empty;
            }

            _stagedPrismPreview = await UploadStreamAsync(
                path,
                (stream, token) => services.Session.Client.PreviewPrismArchiveAsync(
                    stream,
                    Path.GetFileName(path),
                    channel.Id,
                    token),
                cancellationToken).ConfigureAwait(false);
            return new("state.ready");
        }

        OperationAcceptedDto accepted;
        if (sourceKind == "prism")
        {
            StagedPrismPreviewDto preview = _stagedPrismPreview ?? throw new InvalidOperationException(services.Localization.Get("hmcl.preview.required"));
            HmclPublishDiffDto? difference = preview.Difference;
            if (difference is not null && (difference.Deleted.Count > 0 || difference.LoaderChanged))
            {
                string summary = "+" + difference.Added.Count.ToString(CultureInfo.CurrentCulture) +
                    " ~" + difference.Replaced.Count.ToString(CultureInfo.CurrentCulture) +
                    " -" + difference.Deleted.Count.ToString(CultureInfo.CurrentCulture);
                bool confirmed = await services.Interaction.ConfirmNamedAsync(
                    "action." + action,
                    channel.Name,
                    services.Localization.Format("danger.publish-delete-impact", summary)).ConfigureAwait(true);
                if (!confirmed)
                {
                    return new("state.cancelled", null, false);
                }
            }

            accepted = await services.Session.Client.ConvertPrismArchiveAsync(
                preview.UploadId,
                new ConvertStagedPrismRequest(channel.Id, Metadata(fields, preview.Preview.GameVersion)),
                cancellationToken).ConfigureAwait(false);
        }
        else if (sourceKind == "instance")
        {
            accepted = await services.Session.Client.CreateHmclReleaseFromInstanceAsync(
                channel.Id, new CreateHmclReleaseFromInstanceRequest(RequireInstance().Id, Metadata(fields)), cancellationToken).ConfigureAwait(false);
        }
        else
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                path = await services.Interaction.PickOpenFileAsync(".zip").ConfigureAwait(true) ?? string.Empty;
            }

            accepted = await UploadStreamAsync(
                path,
                (stream, token) => services.Session.Client.CreateHmclReleaseFromStreamAsync(channel.Id, stream, Path.GetFileName(path), Metadata(fields), token),
                cancellationToken).ConfigureAwait(false);
        }

        _stagedPrismPreview = null;

        if (action == "create-and-publish")
        {
            OperationDetailsDto operation = await WaitForOperationAsync(accepted.OperationId, cancellationToken).ConfigureAwait(false);
            HmclReleaseCreationResult creation = JsonSerializer.Deserialize<HmclReleaseCreationResult>(
                operation.Result ?? string.Empty,
                WebJson)
                ?? throw new InvalidOperationException(services.Localization.Get("state.failed"));
            services.Session.SelectedRelease = new HmclReleaseSummaryDto(
                creation.Release.Id,
                creation.Release.ChannelId,
                creation.Release.Sequence,
                creation.Release.Name,
                creation.Release.Version,
                creation.Release.GameVersion,
                creation.Release.Files.Count,
                creation.Release.Files.Sum(file => file.Size),
                creation.Release.CreatedAt,
                null,
                false);
            return await ChannelActionAsync("publish", fields, cancellationToken).ConfigureAwait(false);
        }

        return new("common.operation-accepted");
    }

    private async Task<PageActionResult> ReleaseActionAsync(string action, CancellationToken cancellationToken)
    {
        HmclReleaseSummaryDto release = services.Session.SelectedRelease ?? throw SelectionRequired();
        if (action == "rollback")
        {
            if (services.Session.SelectedChannel?.Id != release.ChannelId)
            {
                await services.Session.RefreshSelectionsAsync(cancellationToken).ConfigureAwait(false);
                services.Session.SelectedChannel = services.Session.Channels.FirstOrDefault(channel => channel.Id == release.ChannelId)
                    ?? throw SelectionRequired();
            }

            return await ChannelActionAsync(
                "rollback", new Dictionary<string, string>(StringComparer.Ordinal), cancellationToken).ConfigureAwait(false);
        }

        if (action == "verify")
        {
            HmclIntegrityResultDto result = await services.Session.Client.RunHmclIntegrityAsync(cancellationToken).ConfigureAwait(false);
            return new(result.Failures.Any(item => item.ReleaseId == release.Id) ? "state.invalid" : "state.valid", null, false);
        }

        string? path = await services.Interaction.PickSaveFileAsync("server-manifest.json", ".json").ConfigureAwait(true);
        if (path is null)
        {
            return new("state.cancelled", null, false);
        }

        FileStream destination = new(path, FileMode.Create, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.Asynchronous | FileOptions.WriteThrough);
        await using (destination.ConfigureAwait(false))
        {
            _ = await services.Session.Client.DownloadHmclReleaseManifestAsync(release.Id, destination, cancellationToken).ConfigureAwait(false);
        }
        return new("common.operation-completed");
    }

    private async Task<OperationDetailsDto> WaitForOperationAsync(Guid operationId, CancellationToken cancellationToken)
    {
        DateTimeOffset deadline = services.Session.Client.TimeProvider.GetUtcNow().AddMinutes(5);
        while (services.Session.Client.TimeProvider.GetUtcNow() < deadline)
        {
            OperationDetailsDto operation = await services.Session.Client.GetOperationAsync(operationId, cancellationToken).ConfigureAwait(false);
            if (operation.Operation.Status == "Succeeded")
            {
                return operation;
            }

            if (operation.Operation.Status is "Failed" or "Cancelled")
            {
                throw new InvalidOperationException(operation.Operation.ErrorCode ?? services.Localization.Get("state.failed"));
            }

            await Task.Delay(TimeSpan.FromMilliseconds(250), services.Session.Client.TimeProvider, cancellationToken).ConfigureAwait(false);
        }

        throw new TimeoutException(services.Localization.Get("error.operation-timeout"));
    }

    private async Task<PageActionResult> PublicDownloadAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        HmclChannelDto channel = _selected as HmclChannelDto ?? RequireChannel();
        Uri target = channel.ManifestUrl ?? (await services.Session.Client.GetHmclChannelAsync(channel.Id, cancellationToken).ConfigureAwait(false)).ManifestUrl;
        _ = await Services.UserInteractionService.LaunchUriAsync(target).ConfigureAwait(false);
        return new("common.operation-completed", null, false);
    }

    private static Task<PageActionResult> Result(string pageId)
    {
        return Task.FromResult(new PageActionResult(null, pageId, false));
    }
}
