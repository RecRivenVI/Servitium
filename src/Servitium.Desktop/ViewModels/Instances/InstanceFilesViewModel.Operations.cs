using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Text.Json;

using CommunityToolkit.Mvvm.Input;

using Servitium.Contracts.Files;
using Servitium.Contracts.Operations;
using Servitium.Contracts.Events;
using Servitium.Desktop.Models;
using Servitium.Desktop.Services;

namespace Servitium.Desktop.ViewModels.Instances;

internal sealed partial class InstanceFilesViewModel
{
    private static readonly string[] EncodingNames = ["UTF-8", "UTF-8-BOM", "UTF-16", "UTF-16LE", "UTF-16BE", "GB18030", "GBK", "Big5", "Shift_JIS", "ANSI"];
    private static readonly string[] ArchiveEncodingNames = ["UTF-8", "GBK", "Big5", "Shift_JIS"];
    public IReadOnlyList<SettingChoice> Encodings => [new("auto", Text("desktop.encoding-auto")),
        .. EncodingNames.Select(value => new SettingChoice(value, value))];
    public IReadOnlyList<SettingChoice> WriteEncodings { get; } = [.. EncodingNames.Select(value => new SettingChoice(value, value))];
    public IReadOnlyList<SettingChoice> ArchiveEncodings => [new("auto", Text("desktop.encoding-auto")), .. ArchiveEncodingNames.Select(value => new SettingChoice(value, value))];
    public bool CanDownloadUrl => CanMutate && Uri.TryCreate(Url, UriKind.Absolute, out Uri? uri) && uri.Scheme is "http" or "https" && ValidDestination;
    public bool CanCompress => CanMutateSelection && ValidDestination;
    public bool CanExtract => CanMutateFile && (Destination.Length == 0 || ValidDestination);
    public bool CanPreviewImage => CanUseFile && Path.GetExtension(SelectedFiles[0].Name).ToUpperInvariant() is ".PNG" or ".JPG" or ".JPEG" or ".GIF" or ".BMP";
    private bool ValidDestination => !string.IsNullOrWhiteSpace(Destination) && !Path.IsPathRooted(Destination) && !Destination.Split(['/', '\\']).Contains("..", StringComparer.Ordinal);
    private bool CanPause => IsTransferring && !_paused;
    private bool CanResume => IsTransferring && _paused;
    private bool CanCancel => IsTransferring;

    [RelayCommand(CanExecute = nameof(CanMutate))]
    private Task CreateFileAsync() { return CreateEntryAsync(false); }
    [RelayCommand(CanExecute = nameof(CanMutate))]
    private Task CreateDirectoryAsync() { return CreateEntryAsync(true); }
    private Task CreateEntryAsync(bool directory)
    {
        return RunAsync(async token =>
        {
            string? name = await _prompt(directory ? "action.create-directory" : "action.create-file", "field.name", null).ConfigureAwait(true);
            if (string.IsNullOrWhiteSpace(name)) { return; }
            FilePathRequest request = new(Combine(DirectoryPath, name));
            _ = directory ? await _client.CreateDirectoryAsync(_instance.Id, _instance.NodeId, request, token).ConfigureAwait(true)
                : await _client.CreateFileAsync(_instance.Id, _instance.NodeId, request, token).ConfigureAwait(true);
            await LoadCoreAsync(token).ConfigureAwait(true);
        });
    }
    [RelayCommand(CanExecute = nameof(CanMutateOne))]
    private Task RenameAsync()
    {
        FileEntryDto source = SelectedFiles[0].Value;
        return RunAsync(async token =>
        {
            string? name = await _prompt("action.rename", "field.name", Path.GetFileName(source.Path)).ConfigureAwait(true);
            if (string.IsNullOrWhiteSpace(name)) { return; }
            _ = await _client.RenameFileAsync(_instance.Id, _instance.NodeId, new(source.Path, name), token).ConfigureAwait(true);
            await LoadCoreAsync(token).ConfigureAwait(true);
        });
    }
    [RelayCommand(CanExecute = nameof(CanUseSelection))]
    private void Copy() { SetClipboard("copy"); }
    [RelayCommand(CanExecute = nameof(CanUseSelection))]
    private void Cut() { SetClipboard("move"); }
    private void SetClipboard(string operation)
    {
        _clipboard = new(operation, [.. SelectedFiles.Select(row => row.Value.Path)]);
        Message = Text("file.clipboard-ready"); RefreshCommands();
    }
    [RelayCommand(CanExecute = nameof(CanPaste))]
    private Task PasteAsync()
    {
        FileClipboardPlan clipboard = _clipboard!;
        return RunAsync(async token =>
        {
            if (!await ConfirmOverwriteAsync(CollisionPolicy == "overwrite").ConfigureAwait(true)) { return; }
            foreach ((string source, string destination) in clipboard.Destinations(DirectoryPath))
            {
                FileTransferRequest request = new(source, destination, CollisionPolicy);
                OperationAcceptedDto accepted = clipboard.Operation == "move"
                    ? await _client.MoveFilesAsync(_instance.Id, _instance.NodeId, request, token).ConfigureAwait(true)
                    : await _client.CopyFilesAsync(_instance.Id, _instance.NodeId, request, token).ConfigureAwait(true);
                _ = await DesktopOperationService.WaitAsync(_client, accepted.OperationId, token).ConfigureAwait(true);
            }
            if (clipboard.Operation == "move") { _clipboard = null; }
            await LoadCoreAsync(token).ConfigureAwait(true); Message = Text("common.operation-completed");
        });
    }
    [RelayCommand(CanExecute = nameof(CanMutateOne))]
    private Task MoveAsync()
    {
        FileEntryDto source = SelectedFiles[0].Value;
        return RunAsync(async token =>
        {
            string? target = await _prompt("action.move", "desktop.relative-destination", source.Path).ConfigureAwait(true);
            if (string.IsNullOrWhiteSpace(target) || !await ConfirmOverwriteAsync(CollisionPolicy == "overwrite").ConfigureAwait(true)) { return; }
            OperationAcceptedDto accepted = await _client.MoveFilesAsync(_instance.Id, _instance.NodeId, new(source.Path, NormalizeRelative(target), CollisionPolicy), token).ConfigureAwait(true);
            _ = await DesktopOperationService.WaitAsync(_client, accepted.OperationId, token).ConfigureAwait(true);
            await LoadCoreAsync(token).ConfigureAwait(true);
        });
    }
    [RelayCommand(CanExecute = nameof(CanMutateSelection))]
    private Task DeleteAsync()
    {
        string[] paths = [.. SelectedFiles.Select(row => row.Value.Path)];
        return RunAsync(async token =>
        {
            if (!await _confirm("action.delete", Format("desktop.files-delete-impact", string.Join(Environment.NewLine, paths))).ConfigureAwait(true)) { return; }
            OperationAcceptedDto accepted = await _client.DeleteFilesAsync(_instance.Id, _instance.NodeId, new(paths), token).ConfigureAwait(true);
            _ = await DesktopOperationService.WaitAsync(_client, accepted.OperationId, token).ConfigureAwait(true);
            await LoadCoreAsync(token).ConfigureAwait(true);
        });
    }
    [RelayCommand(CanExecute = nameof(CanCompress))]
    private Task CompressAsync()
    {
        string[] paths = [.. SelectedFiles.Select(row => row.Value.Path)];
        return RunAsync(async token =>
        {
            OperationAcceptedDto accepted = await _client.CompressFilesAsync(_instance.Id, _instance.NodeId,
                new(paths, Combine(DirectoryPath, Destination), ArchiveEncoding), token).ConfigureAwait(true);
            _ = await DesktopOperationService.WaitAsync(_client, accepted.OperationId, token).ConfigureAwait(true);
            await LoadCoreAsync(token).ConfigureAwait(true);
        });
    }
    [RelayCommand(CanExecute = nameof(CanExtract))]
    private Task ExtractAsync()
    {
        string source = SelectedFiles[0].Value.Path;
        return RunAsync(async token =>
        {
            OperationAcceptedDto accepted = await _client.ExtractArchiveAsync(_instance.Id, _instance.NodeId,
                new(source, Destination.Length == 0 ? DirectoryPath : Combine(DirectoryPath, Destination), ExtractEncoding), token).ConfigureAwait(true);
            _ = await DesktopOperationService.WaitAsync(_client, accepted.OperationId, token).ConfigureAwait(true);
            await LoadCoreAsync(token).ConfigureAwait(true);
        });
    }
    [RelayCommand(CanExecute = nameof(CanDownloadUrl))]
    private Task DownloadUrlAsync()
    {
        return RunAsync(async token =>
        {
            if (!await ConfirmOverwriteAsync(Overwrite).ConfigureAwait(true)) { return; }
            OperationAcceptedDto accepted = await _client.DownloadFileFromUrlAsync(_instance.Id, _instance.NodeId,
                new(new Uri(Url), Combine(DirectoryPath, Destination), Overwrite), token).ConfigureAwait(true);
            _ = await DesktopOperationService.WaitAsync(_client, accepted.OperationId, token).ConfigureAwait(true);
            await LoadCoreAsync(token).ConfigureAwait(true);
        });
    }
    private Task<bool> ConfirmOverwriteAsync(bool overwrite)
    {
        return overwrite ? _confirm("desktop.files-overwrite-title", Text("desktop.files-overwrite-impact")) : Task.FromResult(true);
    }

    [RelayCommand(CanExecute = nameof(CanMutate))]
    private async Task UploadAsync()
    {
        string? path = await _pickUpload().ConfigureAwait(true);
        if (path is not null) { await UploadPathsAsync([path]).ConfigureAwait(true); }
    }
    public async Task UploadPathsAsync(IReadOnlyList<string> paths)
    {
        if (!CanMutate) { return; }
        CancellationTokenSource operation = new(); _transferCancellation = operation; _paused = false; RefreshCommands();
        try
        {
            await RunAsync(async lifetime =>
            {
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(lifetime, operation.Token);
                if (!await ConfirmOverwriteAsync(Overwrite).ConfigureAwait(true)) { return; }
                try
                {
                    List<(string Source, string Relative)> sources = FileTransferService.EnumerateDroppedFiles(paths);
                    if (sources.Count == 0) { Message = Text("desktop.upload-no-files"); return; }
                    foreach ((string source, string relative) in sources)
                    {
                        OperationAcceptedDto accepted = await _transfer.UploadAsync(_instance, source, Combine(DirectoryPath, relative), null, linked.Token, Overwrite).ConfigureAwait(true);
                        _ = await DesktopOperationService.WaitAsync(_client, accepted.OperationId, linked.Token).ConfigureAwait(true);
                    }
                    await LoadCoreAsync(linked.Token).ConfigureAwait(true);
                    Message = Text("common.operation-completed");
                }
                catch (OperationCanceledException) when (operation.IsCancellationRequested) { Message = Text("state.cancelled"); }
            }).ConfigureAwait(true);
        }
        finally { _pause.Resume(); operation.Dispose(); _transferCancellation = null; _paused = false; RefreshCommands(); }
    }
    [RelayCommand(CanExecute = nameof(CanPause))]
    private void Pause() { _pause.Pause(); _paused = true; RefreshCommands(); }
    [RelayCommand(CanExecute = nameof(CanResume))]
    private void Resume() { _pause.Resume(); _paused = false; RefreshCommands(); }
    [RelayCommand(CanExecute = nameof(CanCancel))]
    private void Cancel() { _transferCancellation?.Cancel(); _pause.Resume(); }
    private void OnTransferProgress(TransferProgressEventArgs progress)
    {
        TransferPercent = progress.TotalBytes == 0 ? 0 : 100d * progress.TransferredBytes / progress.TotalBytes;
        TransferText = Format("desktop.file-transfer-progress", progress.FileName, progress.TransferredBytes, progress.TotalBytes,
            progress.BytesPerSecond, Text("state." + progress.State));
    }

    [RelayCommand(CanExecute = nameof(CanUseFile))]
    private Task DownloadAsync()
    {
        FileEntryDto file = SelectedFiles[0].Value;
        return RunAsync(async token =>
        {
            string name = Path.GetFileName(file.Path), extension = Path.GetExtension(file.Path);
            string? target = await _pickDownload(name, extension.Length == 0 ? ".bin" : extension).ConfigureAwait(true);
            if (target is null) { return; }
            string partial = target + ".servitium-" + Guid.NewGuid().ToString("N") + ".part";
            FileStream destination = new(partial, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true);
            await using (destination.ConfigureAwait(false))
            {
                await _client.DownloadFileAsync(_instance.Id, _instance.NodeId, file.Path, destination, null, null, token).ConfigureAwait(true);
                await destination.FlushAsync(token).ConfigureAwait(true);
            }
            File.Move(partial, target, true); Message = Text("common.operation-completed");
        });
    }
    [RelayCommand(CanExecute = nameof(CanPreviewImage))]
    private Task PreviewAsync()
    {
        FileEntryDto file = SelectedFiles[0].Value;
        return RunAsync(async token =>
        {
            if (file.Size > 16 * 1024 * 1024) { Message = Text("error.file-too-large"); return; }
            using MemoryStream buffer = new((int)file.Size);
            await _client.DownloadFileAsync(_instance.Id, _instance.NodeId, file.Path, buffer, null, null, token).ConfigureAwait(true);
            await _preview(file.Path, buffer.ToArray()).ConfigureAwait(true);
        });
    }
    [RelayCommand(CanExecute = nameof(CanUseFile))]
    private Task EditTextAsync()
    {
        FileEntryDto file = SelectedFiles[0].Value;
        return RunAsync(async token =>
        {
            string requestedEncoding = TextEncoding;
            TextFileDto text = await _client.ReadTextFileAsync(_instance.Id, _instance.NodeId, file.Path, requestedEncoding == "auto" ? null : requestedEncoding, token).ConfigureAwait(true);
            _editingPath = text.Path; _savedText = text.Content; EditorText = text.Content;
            TextEncoding = requestedEncoding == "auto" ? Encodings.FirstOrDefault(choice => string.Equals(choice.Value, text.Encoding, StringComparison.OrdinalIgnoreCase))?.Value ?? text.Encoding : requestedEncoding;
            IsEditing = true; OnPropertyChanged(nameof(IsDirty)); OnPropertyChanged(nameof(EditorTitle)); EditorLoaded?.Invoke(this, EventArgs.Empty);
        });
    }
    [RelayCommand(CanExecute = nameof(CanSave))]
    private Task SaveTextAsync()
    {
        return RunAsync(async token =>
        {
            TextFileDto text = await _client.WriteTextFileAsync(_instance.Id, _instance.NodeId, _editingPath!, new(EditorText, TextEncoding), token).ConfigureAwait(true);
            _savedText = text.Content; EditorText = text.Content; OnPropertyChanged(nameof(IsDirty)); EditorLoaded?.Invoke(this, EventArgs.Empty);
            Message = Text("common.operation-completed");
        });
    }
    [RelayCommand(CanExecute = nameof(CanCloseEditor))]
    private async Task CloseEditorAsync()
    {
        if (!await CanLeaveAsync().ConfigureAwait(true)) { return; }
        IsEditing = false; _editingPath = null; EditorText = string.Empty; _savedText = string.Empty;
        OnPropertyChanged(nameof(IsDirty)); RefreshCommands();
    }
    private void RefreshFileCommands()
    {
        foreach (string property in new[] { nameof(CanDownloadUrl), nameof(CanCompress), nameof(CanExtract) }) { OnPropertyChanged(property); }
        CreateFileCommand.NotifyCanExecuteChanged(); CreateDirectoryCommand.NotifyCanExecuteChanged(); RenameCommand.NotifyCanExecuteChanged(); CopyCommand.NotifyCanExecuteChanged(); CutCommand.NotifyCanExecuteChanged();
        PasteCommand.NotifyCanExecuteChanged(); MoveCommand.NotifyCanExecuteChanged(); DeleteCommand.NotifyCanExecuteChanged(); CompressCommand.NotifyCanExecuteChanged(); ExtractCommand.NotifyCanExecuteChanged();
        DownloadUrlCommand.NotifyCanExecuteChanged(); UploadCommand.NotifyCanExecuteChanged(); PauseCommand.NotifyCanExecuteChanged(); ResumeCommand.NotifyCanExecuteChanged(); CancelCommand.NotifyCanExecuteChanged();
        DownloadCommand.NotifyCanExecuteChanged(); PreviewCommand.NotifyCanExecuteChanged(); EditTextCommand.NotifyCanExecuteChanged(); SaveTextCommand.NotifyCanExecuteChanged(); CloseEditorCommand.NotifyCanExecuteChanged();
    }
    public string EditorLanguage => Path.GetExtension(_editingPath)?.ToUpperInvariant() switch { ".JSON" => "json", ".YML" or ".YAML" => "yaml", ".TOML" => "toml", ".PROPERTIES" => "properties", _ => "plaintext" };
    public void ReportInteractionError(Exception exception)
    {
        Severity = Microsoft.UI.Xaml.Controls.InfoBarSeverity.Error;
        Message = DesktopErrorPresentation.Describe(exception, Localization);
    }
    public void ApplyRuntimeEvent(RealtimeEventDto value)
    {
        if (value.Type == EventTypes.NodeStatusChanged)
        {
            NodeStatusChangedPayload? node = value.Payload.Deserialize<NodeStatusChangedPayload>(JsonSerializerOptions.Web);
            if (node?.NodeId == _instance.NodeId) { IsReachable = node.Status == "Online"; RefreshCommands(); }
            return;
        }
        if (value.Type != EventTypes.InstanceStatusChanged) { return; }
        InstanceStatusChangedPayload? state = value.Payload.Deserialize<InstanceStatusChangedPayload>(JsonSerializerOptions.Web);
        if (state?.InstanceId == _instance.Id) { _status = state.Status; IsRunning = state.Status == "Running"; RefreshCommands(); }
    }
}
