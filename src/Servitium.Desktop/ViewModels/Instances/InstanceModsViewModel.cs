using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using CommunityToolkit.Mvvm.Input;

using Servitium.Client;
using Servitium.Contracts.Events;
using Servitium.Contracts.Instances;
using Servitium.Contracts.Mods;
using Servitium.Contracts.Operations;
using Servitium.Contracts.Transport;
using Servitium.Desktop.Models;
using Servitium.Desktop.Services;

namespace Servitium.Desktop.ViewModels.Instances;

internal sealed partial class InstanceModsViewModel : DesktopViewModel
{
    private readonly ServitiumApiClient _client;
    private readonly InstanceDto _instance;
    private readonly Func<Task<string?>> _pickUpload;
    private readonly Func<string, string, Task<bool>> _confirm;
    private readonly Action<string> _openConfiguration;
    private readonly TransferPauseGate _pause = new();
    private readonly FileTransferService _transfer;
    private CancellationTokenSource? _uploadCancellation;
    private List<ModRow> _allRows = [];
    private string _status;

    internal InstanceModsViewModel(LocalizationService localization, ServitiumApiClient client, InstanceDto instance,
        Func<Task<string?>> pickUpload, Func<string, string, Task<bool>> confirm, Action<string> openConfiguration, bool reachable = true) : base(localization)
    {
        _client = client; _instance = instance; _pickUpload = pickUpload; _confirm = confirm; _openConfiguration = openConfiguration;
        IsReachable = reachable; _status = instance.Status;
        IProgress<TransferProgressEventArgs> progress = new Progress<TransferProgressEventArgs>(value =>
        {
            TransferPercent = value.TotalBytes == 0 ? 0 : 100d * value.TransferredBytes / value.TotalBytes;
            TransferText = Format("desktop.file-transfer-progress", value.FileName, value.TransferredBytes, value.TotalBytes, value.BytesPerSecond, Text("state." + value.State));
        });
        _transfer = new(client, localization, _pause, progress.Report);
    }
    public IReadOnlyList<ModRow> Mods { get; private set => SetProperty(ref field, value); } = [];
    public ModRow? SelectedMod { get; set { if (SetProperty(ref field, value)) { ConfigurationFiles = []; SelectedConfiguration = null; RefreshCommands(); OnPropertyChanged(nameof(Details)); } } }
    public string Search { get; set { if (SetProperty(ref field, value)) { Filter(); } } } = string.Empty;
    public int EnabledFilter { get; set { if (SetProperty(ref field, value)) { Filter(); } } }
    public int KindIndex { get; private set => SetProperty(ref field, value); }
    private string Kind => KindIndex == 1 ? "plugins" : "mods";
    public bool IsEmpty => Mods.Count == 0;
    public bool IsReachable { get; private set; }
    public bool IsOffline => !IsReachable;
    public bool IsRunning => _status == "Running";
    public bool CanRead => CanInteract && IsReachable;
    public bool CanChangeSelected => CanRead && SelectedMod is not null;
    public bool CanUpload => CanRead && _status is "Stopped" or "Crashed";
    public bool CanDelete => CanUpload && SelectedMod is not null;
    public bool IsUploading => _uploadCancellation is not null;
    public bool CanCancel => IsUploading;
    public string TransferText { get; private set => SetProperty(ref field, value); } = string.Empty;
    public double TransferPercent { get; private set => SetProperty(ref field, value); }
    public string Count => Format("desktop.mods-shown", Mods.Count);
    public string Details => SelectedMod is not ModRow row ? Text("desktop.mods-select-next") :
        Format("desktop.mod-details", row.Name, row.Version, row.Value.Description ?? Text("desktop.not-provided"),
            string.Join("、", row.Value.Authors), row.Value.Loader ?? Text("desktop.not-provided"), row.Value.RelativePath,
            string.Join("、", row.Value.Dependencies ?? []), row.Value.MetadataUnreadable ? Text("desktop.mod-metadata-unreadable") : string.Empty);
    public IReadOnlyList<string> ConfigurationFiles { get; private set => SetProperty(ref field, value); } = [];
    public string? SelectedConfiguration { get; set { if (SetProperty(ref field, value)) { OpenConfigurationCommand.NotifyCanExecuteChanged(); } } }
    public bool NoConfigurationFiles => ConfigurationFiles.Count == 0;
    public bool CanOpenConfiguration => CanChangeSelected && SelectedConfiguration is not null;

    public Task LoadAsync() { return RunAsync(LoadCoreAsync); }
    private async Task LoadCoreAsync(CancellationToken token)
    {
        string? selectedId = SelectedMod?.Value.Id; List<ModRow> rows = []; string? cursor = null;
        do
        {
            CursorPage<ModDetailsDto> page = await _client.ListModsAsync(_instance.Id, _instance.NodeId, Kind, null, cursor, 200, token).ConfigureAwait(true);
            rows.AddRange(page.Items.Select(value => new ModRow(value, Localization))); cursor = page.NextCursor;
        } while (cursor is not null);
        _allRows = rows; Filter(); SelectedMod = Mods.FirstOrDefault(row => row.Value.Id == selectedId);
    }
    public async Task ChangeKindAsync(int index)
    {
        if (!CanRead || index == KindIndex) { return; }
        KindIndex = index; SelectedMod = null; await LoadAsync().ConfigureAwait(true);
    }
    private void Filter()
    {
        Mods = [.. _allRows.Where(row => (row.Name.Contains(Search, StringComparison.CurrentCultureIgnoreCase) ||
            row.Value.Identifier?.Contains(Search, StringComparison.CurrentCultureIgnoreCase) == true || row.Value.Authors.Any(author => author.Contains(Search, StringComparison.CurrentCultureIgnoreCase))) &&
            (EnabledFilter == 0 || row.Enabled == (EnabledFilter == 1)))];
        if (SelectedMod is not null && !Mods.Contains(SelectedMod)) { SelectedMod = null; }
        OnPropertyChanged(nameof(IsEmpty)); OnPropertyChanged(nameof(Count)); RefreshCommands();
    }
    [RelayCommand(CanExecute = nameof(CanRead))]
    private Task RefreshAsync() { return LoadAsync(); }
    public async Task SetEnabledAsync(ModRow row, bool enabled)
    {
        if (!CanRead || row.Enabled == enabled) { row.ResetToggle(); return; }
        await RunAsync(async token =>
        {
            ModDetailsDto result = enabled
                ? await _client.EnableModAsync(_instance.Id, _instance.NodeId, Kind, row.Value.Id, token).ConfigureAwait(true)
                : await _client.DisableModAsync(_instance.Id, _instance.NodeId, Kind, row.Value.Id, token).ConfigureAwait(true);
            row.Update(result); Filter(); Message = Text("desktop.mod-changed-next");
        }).ConfigureAwait(true);
        row.ResetToggle();
    }
    [RelayCommand(CanExecute = nameof(CanDelete))]
    private async Task DeleteAsync()
    {
        ModRow row = SelectedMod!;
        if (!await _confirm("action.delete", Format("desktop.mod-delete-impact", row.Name, row.Value.RelativePath)).ConfigureAwait(true)) { return; }
        await RunAsync(async token =>
        {
            await _client.DeleteModAsync(_instance.Id, _instance.NodeId, Kind, row.Value.Id, token).ConfigureAwait(true);
            await LoadCoreAsync(token).ConfigureAwait(true); Message = Text("common.operation-completed");
        }).ConfigureAwait(true);
    }
    [RelayCommand(CanExecute = nameof(CanUpload))]
    private Task UploadAsync()
    {
        return RunAsync(async token =>
        {
            string? path = await _pickUpload().ConfigureAwait(true); if (path is null) { return; }
            _uploadCancellation = CancellationTokenSource.CreateLinkedTokenSource(token); RefreshCommands();
            try
            {
                OperationAcceptedDto accepted = await _transfer.UploadAsync(_instance, path, Kind + "/" + System.IO.Path.GetFileName(path), Kind, _uploadCancellation.Token).ConfigureAwait(true);
                _ = await DesktopOperationService.WaitAsync(_client, accepted.OperationId, _uploadCancellation.Token).ConfigureAwait(true);
                await LoadCoreAsync(token).ConfigureAwait(true); Message = Text("desktop.mod-uploaded-next");
            }
            catch (OperationCanceledException) when (_uploadCancellation.IsCancellationRequested) { Message = Text("desktop.transfer-cancelled"); }
            finally { _uploadCancellation.Dispose(); _uploadCancellation = null; RefreshCommands(); }
        });
    }
    [RelayCommand(CanExecute = nameof(CanCancel))]
    private void Cancel() { _uploadCancellation?.Cancel(); }
    [RelayCommand(CanExecute = nameof(CanChangeSelected))]
    private Task FindConfigurationAsync()
    {
        return RunAsync(async token =>
        {
            List<string> paths = []; string? cursor = null;
            do
            {
                CursorPage<string> page = await _client.ListModConfigurationFilesAsync(_instance.Id, _instance.NodeId, Kind, SelectedMod!.Value.Id, cursor, 200, token).ConfigureAwait(true);
                paths.AddRange(page.Items); cursor = page.NextCursor;
            } while (cursor is not null);
            ConfigurationFiles = paths; SelectedConfiguration = paths.FirstOrDefault(); OnPropertyChanged(nameof(NoConfigurationFiles));
        });
    }
    [RelayCommand(CanExecute = nameof(CanOpenConfiguration))]
    private void OpenConfiguration() { _openConfiguration(SelectedConfiguration!); }
    public async Task<bool> CanLeaveAsync() { return !IsUploading || await _confirm("desktop.transfer-leave-title", Text("desktop.transfer-leave-impact")).ConfigureAwait(true); }
    public void ApplyRuntimeEvent(RealtimeEventDto value)
    {
        if (value.Type == EventTypes.NodeStatusChanged && value.Payload.Deserialize<NodeStatusChangedPayload>(JsonSerializerOptions.Web) is { } node && node.NodeId == _instance.NodeId) { IsReachable = node.Status == "Online"; }
        if (value.Type == EventTypes.InstanceStatusChanged && value.Payload.Deserialize<InstanceStatusChangedPayload>(JsonSerializerOptions.Web) is { } status && status.InstanceId == _instance.Id) { _status = status.Status; }
        OnPropertyChanged(nameof(IsReachable)); OnPropertyChanged(nameof(IsOffline)); OnPropertyChanged(nameof(IsRunning)); RefreshCommands();
    }
    protected override void RefreshCommands()
    {
        foreach (ModRow row in _allRows) { row.CanToggle = CanRead; }
        foreach (string name in new[] { nameof(CanRead), nameof(CanChangeSelected), nameof(CanUpload), nameof(CanDelete), nameof(IsUploading), nameof(CanCancel), nameof(CanOpenConfiguration) }) { OnPropertyChanged(name); }
        RefreshCommand.NotifyCanExecuteChanged(); DeleteCommand.NotifyCanExecuteChanged(); UploadCommand.NotifyCanExecuteChanged();
        CancelCommand.NotifyCanExecuteChanged(); FindConfigurationCommand.NotifyCanExecuteChanged(); OpenConfigurationCommand.NotifyCanExecuteChanged();
    }
    public override void Dispose() { _uploadCancellation?.Cancel(); _uploadCancellation?.Dispose(); base.Dispose(); }
}
