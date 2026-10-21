using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using CommunityToolkit.Mvvm.Input;

using Servitium.Client;
using Servitium.Contracts.Configurations;
using Servitium.Contracts.Events;
using Servitium.Contracts.Instances;
using Servitium.Contracts.Transport;
using Servitium.Desktop.Services;

namespace Servitium.Desktop.ViewModels.Instances;

internal sealed partial class InstanceConfigurationViewModel : DesktopViewModel
{
    private readonly ServitiumApiClient _client;
    private readonly InstanceDto _instance;
    private readonly Func<string, string, Task<bool>> _confirm;
    private readonly Action<string> _openFiles;
    private readonly IReadOnlyDictionary<string, ConfigurationValue> _fields;
    private ConfigurationDetailsDto? _details;
    private List<ConfigurationFileRow> _allFiles = [];
    private bool _loading;
    private bool _reachable;
    private string _status;

    internal InstanceConfigurationViewModel(LocalizationService localization, ServitiumApiClient client, InstanceDto instance,
        Func<string, string, Task<bool>> confirm, Action<string> openFiles, bool reachable = true) : base(localization)
    {
        _client = client; _instance = instance; _confirm = confirm; _openFiles = openFiles;
        _reachable = reachable; _status = instance.Status;
        _fields = ServerPropertiesSchema.Fields.ToDictionary(setting => setting.Key, setting => new ConfigurationValue(setting, localization), StringComparer.Ordinal);
        foreach (ConfigurationValue setting in _fields.Values) { setting.PropertyChanged += OnFieldChanged; }
    }
    public event EventHandler? EditorLoaded;
    public IReadOnlyList<ConfigurationFileRow> Files { get; private set => SetProperty(ref field, value); } = [];
    public ConfigurationFileRow? SelectedFile { get; private set { if (SetProperty(ref field, value)) { OnPropertyChanged(nameof(FileTitle)); } } }
    public string FileTitle => SelectedFile?.Name ?? Text("desktop.config-files");
    public string Search { get; set { if (SetProperty(ref field, value)) { FilterFiles(); } } } = string.Empty;
    public bool HasFile => _details is not null;
    public bool IsEmpty => _allFiles.Count == 0;
    public bool HasStructuredView => _details?.Key == "server-properties";
    public bool IsRaw { get; private set { if (SetProperty(ref field, value)) { OnPropertyChanged(nameof(ShowStructured)); } } }
    public bool ShowStructured => HasFile && HasStructuredView && !IsRaw;
    public string RawText { get; set { if (SetProperty(ref field, value)) { UpdateDirty(); } } } = string.Empty;
    public string EditorLanguage => _details?.Format switch { "json" => "json", "yaml" => "yaml", "toml" => "toml", "properties" => "properties", _ => "plaintext" };
    public bool IsDirty => HasFile && (IsRaw ? RawText != _details!.RawText : _fields.Values.Any(setting => setting.IsDirty));
    public int DirtyCount => IsRaw ? IsDirty ? 1 : 0 : _fields.Values.Count(setting => setting.IsDirty);
    public string DirtySummary => IsDirty ? Format("desktop.config-dirty-count", DirtyCount) : Text("desktop.config-saved");
    public string DirtyDetails => IsRaw ? IsDirty ? Text("desktop.config-raw-changed") : string.Empty : string.Join("、", _fields.Values.Where(setting => setting.IsDirty).Select(setting => setting.Title));
    public string Validation => IsRaw ? string.Empty : string.Join(Environment.NewLine, _fields.Values.Where(setting => setting.IsDirty).Select(setting => setting.Error).Where(error => error.Length > 0));
    public bool HasValidation => Validation.Length > 0;
    public bool IsReadOnly => !_reachable || _status is not ("Stopped" or "Crashed") || SelectedFile?.Value.HasConflict == true;
    public string Restriction => !_reachable ? Text("desktop.instance-offline-next") : SelectedFile?.Value.HasConflict == true ? Text("desktop.config-conflict-next") :
        _status is not ("Stopped" or "Crashed") ? Text("desktop.config-stop-first") : Text("desktop.config-restart-needed");
    public string FileNotice => _details is null ? Text("desktop.config-choose-next") : Format("desktop.config-file-details", _details.RelativePath, _details.Encoding, _details.UnknownKeys?.Count ?? 0) +
        (SelectedFile?.Value.HasConflict == true ? Environment.NewLine + Format("desktop.config-conflict-candidates", string.Join(Environment.NewLine, SelectedFile.Value.Candidates ?? [])) : string.Empty);
    public bool CanEdit => CanInteract && HasFile && !IsReadOnly;
    public bool CanSave => CanEdit && IsDirty && !HasValidation;
    public bool CanDiscard => CanInteract && IsDirty;
    public bool CanRead => CanInteract && _reachable;
    public bool CanOpenFiles => CanInteract && SelectedFile is not null;
    public bool CanViewBackups => CanRead && HasFile;
    public IReadOnlyList<ConfigurationBackupRow> Backups { get; private set => SetProperty(ref field, value); } = [];
    public ConfigurationBackupRow? SelectedBackup { get; set { if (SetProperty(ref field, value)) { RestoreBackupCommand.NotifyCanExecuteChanged(); } } }
    public bool CanRestoreBackup => CanEdit && SelectedBackup is not null;
    public bool BackupsEmpty => Backups.Count == 0;

    public Task LoadAsync() { return RunAsync(LoadCoreAsync); }
    private async Task LoadCoreAsync(CancellationToken token)
    {
        List<ConfigurationFileRow> files = []; string? cursor = null;
        do
        {
            CursorPage<ConfigurationDto> page = await _client.ListConfigurationsAsync(_instance.Id, _instance.NodeId, cursor, 200, token).ConfigureAwait(true);
            files.AddRange(page.Items.Select(value => new ConfigurationFileRow(value))); cursor = page.NextCursor;
        } while (cursor is not null);
        _allFiles = files; FilterFiles(); OnPropertyChanged(nameof(IsEmpty));
        ConfigurationFileRow? selected = files.FirstOrDefault(file => file.Value.Key == SelectedFile?.Value.Key) ?? files.FirstOrDefault(file => file.Value.Key == "server-properties") ?? files.FirstOrDefault();
        SelectedFile = selected; _details = null;
        if (selected is not null) { Apply(await _client.GetConfigurationAsync(_instance.Id, _instance.NodeId, selected.Value.Key, token).ConfigureAwait(true)); }
        else { RefreshPresentation(); }
    }
    private void FilterFiles() { Files = [.. _allFiles.Where(file => file.Path.Contains(Search, StringComparison.CurrentCultureIgnoreCase))]; }
    public async Task OpenAsync(ConfigurationFileRow file)
    {
        if (file == SelectedFile || !CanRead || !await CanLeaveAsync().ConfigureAwait(true)) { return; }
        SelectedFile = file; _details = null; RefreshPresentation();
        await RunAsync(async token => Apply(await _client.GetConfigurationAsync(_instance.Id, _instance.NodeId, file.Value.Key, token).ConfigureAwait(true))).ConfigureAwait(true);
    }
    private void Apply(ConfigurationDetailsDto details)
    {
        _loading = true;
        try
        {
            _details = details;
            foreach (ConfigurationValue setting in _fields.Values) { _ = details.Values.TryGetValue(setting.Key, out string? value); setting.Load(value, details.Values.ContainsKey(setting.Key)); }
            RawText = details.RawText; IsRaw = !HasStructuredView;
            Backups = []; SelectedBackup = null; RefreshPresentation();
            if (IsRaw) { EditorLoaded?.Invoke(this, EventArgs.Empty); }
        }
        finally { _loading = false; }
    }
    public async Task SwitchViewAsync(bool raw)
    {
        if (!HasFile || IsRaw == raw || !HasStructuredView || !await CanLeaveAsync().ConfigureAwait(true)) { return; }
        Apply(_details!); IsRaw = raw; UpdateDirty();
        if (raw) { EditorLoaded?.Invoke(this, EventArgs.Empty); }
    }
    public async Task<bool> CanLeaveAsync() { return !IsDirty || await _confirm("desktop.unsaved-title", Text("desktop.unsaved-discard-impact")).ConfigureAwait(true); }
    [RelayCommand(CanExecute = nameof(CanRead))]
    private async Task RefreshAsync() { if (await CanLeaveAsync().ConfigureAwait(true)) { await LoadAsync().ConfigureAwait(true); } }
    [RelayCommand(CanExecute = nameof(CanSave))]
    private Task SaveAsync()
    {
        WriteConfigurationRequest request = new(IsRaw ? null : _fields.Values.Where(setting => setting.IsDirty).ToDictionary(setting => setting.Key, setting => (string?)setting.Value, StringComparer.Ordinal), IsRaw ? RawText : null, _details!.Revision);
        return RunAsync(async token =>
        {
            bool raw = IsRaw;
            Apply(await _client.WriteConfigurationAsync(_instance.Id, _instance.NodeId, _details.Key, request, token).ConfigureAwait(true));
            IsRaw = raw; if (raw) { EditorLoaded?.Invoke(this, EventArgs.Empty); }
            Message = Text("desktop.config-saved-next");
        });
    }
    [RelayCommand(CanExecute = nameof(CanDiscard))]
    private void Discard()
    {
        bool raw = IsRaw; Apply(_details!); IsRaw = raw;
        if (raw) { EditorLoaded?.Invoke(this, EventArgs.Empty); }
    }
    [RelayCommand(CanExecute = nameof(CanOpenFiles))]
    private void OpenFiles() { _openFiles(SelectedFile!.Path); }
    [RelayCommand(CanExecute = nameof(CanViewBackups))]
    private Task ViewBackupsAsync()
    {
        return RunAsync(async token =>
        {
            List<ConfigurationBackupRow> values = []; string? cursor = null;
            do
            {
                CursorPage<ConfigurationBackupDto> page = await _client.ListConfigurationBackupsAsync(_instance.Id, _instance.NodeId, _details!.Key, cursor, 200, token).ConfigureAwait(true);
                values.AddRange(page.Items.Select(value => new ConfigurationBackupRow(value))); cursor = page.NextCursor;
            } while (cursor is not null);
            Backups = values; OnPropertyChanged(nameof(BackupsEmpty));
        });
    }
    [RelayCommand(CanExecute = nameof(CanRestoreBackup))]
    private async Task RestoreBackupAsync()
    {
        if (!await _confirm("action.restore", Text("desktop.config-restore-impact")).ConfigureAwait(true)) { return; }
        await RunAsync(async token =>
        {
            Apply(await _client.RestoreConfigurationBackupAsync(_instance.Id, _instance.NodeId, _details!.Key, SelectedBackup!.Value.Id, token).ConfigureAwait(true));
            Message = Text("desktop.config-saved-next");
        }).ConfigureAwait(true);
    }
    private void OnFieldChanged(object? sender, PropertyChangedEventArgs args) { if (!_loading && args.PropertyName == nameof(ConfigurationValue.Value)) { UpdateDirty(); } }
    private void UpdateDirty()
    {
        foreach (string name in new[] { nameof(IsDirty), nameof(DirtyCount), nameof(DirtySummary), nameof(DirtyDetails), nameof(Validation), nameof(HasValidation) }) { OnPropertyChanged(name); }
        RefreshCommands();
    }
    private void RefreshPresentation()
    {
        foreach (string name in new[] { nameof(HasFile), nameof(HasStructuredView), nameof(ShowStructured), nameof(IsReadOnly), nameof(Restriction), nameof(FileNotice), nameof(BackupsEmpty) }) { OnPropertyChanged(name); }
        UpdateDirty();
    }
    public void ReportInteractionError(Exception exception) { Severity = Microsoft.UI.Xaml.Controls.InfoBarSeverity.Error; Message = DesktopErrorPresentation.Describe(exception, Localization); }
    public void ApplyRuntimeEvent(RealtimeEventDto value)
    {
        if (value.Type == EventTypes.InstanceStatusChanged && value.Payload.Deserialize<InstanceStatusChangedPayload>(JsonSerializerOptions.Web) is { } status && status.InstanceId == _instance.Id) { _status = status.Status; RefreshPresentation(); }
        if (value.Type == EventTypes.NodeStatusChanged && value.Payload.Deserialize<NodeStatusChangedPayload>(JsonSerializerOptions.Web) is { } node && node.NodeId == _instance.NodeId) { _reachable = node.Status == "Online"; RefreshPresentation(); }
    }
    protected override void RefreshCommands()
    {
        foreach (string name in new[] { nameof(CanRead), nameof(CanEdit), nameof(CanSave), nameof(CanDiscard), nameof(CanOpenFiles), nameof(CanViewBackups), nameof(CanRestoreBackup) }) { OnPropertyChanged(name); }
        RefreshCommand.NotifyCanExecuteChanged(); SaveCommand.NotifyCanExecuteChanged(); DiscardCommand.NotifyCanExecuteChanged();
        OpenFilesCommand.NotifyCanExecuteChanged(); ViewBackupsCommand.NotifyCanExecuteChanged(); RestoreBackupCommand.NotifyCanExecuteChanged();
    }
    public override void Dispose() { foreach (ConfigurationValue setting in _fields.Values) { setting.PropertyChanged -= OnFieldChanged; } base.Dispose(); }
}
