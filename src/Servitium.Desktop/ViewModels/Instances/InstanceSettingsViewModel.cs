using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using CommunityToolkit.Mvvm.Input;

using Servitium.Client;
using Servitium.Contracts.Distribution;
using Servitium.Contracts.Instances;
using Servitium.Contracts.Java;
using Servitium.Contracts.Nodes;
using Servitium.Contracts.Transport;
using Servitium.Desktop.Services;

namespace Servitium.Desktop.ViewModels.Instances;

internal sealed partial class InstanceSettingsViewModel : DesktopViewModel
{
    private readonly ServitiumApiClient _client;
    private readonly InstanceDto _instance;
    private readonly bool _administrator;
    private readonly Func<string, string, Task<bool>> _confirm;
    private readonly Func<string, string, string?, Task<string?>> _prompt;
    private readonly HashSet<ShortcutCommandEditor> _subscribedShortcuts = [];
    private InstanceDetailsDto? _details;
    private bool _reachable;
    private string _status;
    private bool _loading;
    private string _baseline = string.Empty;

    internal InstanceSettingsViewModel(LocalizationService localization, ServitiumApiClient client, InstanceDto instance, bool administrator,
        Func<string, string, Task<bool>> confirm, Func<string, string, string?, Task<string?>> prompt, bool reachable = true) : base(localization)
    {
        _client = client; _instance = instance; _administrator = administrator; _confirm = confirm; _prompt = prompt;
        _reachable = reachable; _status = instance.Status;
        Launch = new(localization) { NodeId = instance.NodeId }; Launch.PropertyChanged += OnEditorChanged;
        Encodings = [new("UTF-8", "UTF-8"), new("GBK", "GBK"), new("Big5", "Big5"), new("Shift_JIS", "Shift JIS"), new("system-ansi", Text("desktop.system-encoding"))];
        NewLines = [new("CRLF", Text("desktop.newline-windows")), new("LF", Text("desktop.newline-unix"))];
        Shortcuts.CollectionChanged += OnShortcutsChanged;
    }
    public event EventHandler? Removed;
    public LaunchEditor Launch { get; }
    public Task LoadAsync()
    {
        if (!_administrator || !_reachable)
        {
            Severity = Microsoft.UI.Xaml.Controls.InfoBarSeverity.Warning; Message = Text(!_administrator ? "desktop.permission-error" : "desktop.instance-offline-next");
            return Task.CompletedTask;
        }
        return RunAsync(LoadCoreAsync);
    }
    private async Task LoadCoreAsync(CancellationToken token)
    {
        InstanceDetailsDto details = await _client.GetInstanceAsync(_instance.Id, token).ConfigureAwait(true);
        if (details.Configuration is null) { throw new System.IO.InvalidDataException("The node did not provide complete instance configuration."); }
        NodeDetailsDto node = await _client.GetNodeAsync(_instance.NodeId, token).ConfigureAwait(true);
        List<JavaRuntimeDto> runtimes = []; string? cursor = null;
        do
        {
            CursorPage<JavaRuntimeDto> page = await _client.ListJavaRuntimesAsync(_instance.NodeId, cursor, 200, token).ConfigureAwait(true);
            runtimes.AddRange(page.Items); cursor = page.NextCursor;
        } while (cursor is not null);
        List<HmclChannelDto> channels = []; cursor = null;
        do
        {
            CursorPage<HmclChannelDto> page = await _client.ListHmclChannelsAsync(cursor, 200, token).ConfigureAwait(true);
            channels.AddRange(page.Items); cursor = page.NextCursor;
        } while (cursor is not null);
        _loading = true;
        try
        {
            Launch.SetRuntimes(runtimes, details.Launch.JavaRuntimeId); Launch.AdvancedAllowed = node.Configuration.AdvancedRawCommandsEnabled;
            Channels = channels; Apply(details);
        }
        finally { _loading = false; Changed(); }
    }
    private void Apply(InstanceDetailsDto details)
    {
        UpdateInstanceRequest settings = details.Configuration!;
        _details = details; _status = details.Instance.Status;
        Name = details.Instance.Name; Tags = string.Join(Environment.NewLine, details.Instance.Tags); Notes = details.Notes ?? string.Empty;
        WorkingDirectory = details.Instance.WorkingDirectory; PrimaryPort = settings.PrimaryPort is int port ? port : double.NaN;
        Launch.Load(details.Launch); StopCommand = settings.StopCommand; StopTimeout = settings.StopTimeoutSeconds;
        AutoStart = settings.AutoStart; AutoStartDelay = settings.AutoStartDelaySeconds; AutoRestart = settings.AutoRestart;
        MaximumRestartAttempts = settings.MaximumRestartAttempts; RestartInterval = settings.RestartIntervalSeconds; RestartReset = settings.RestartResetSeconds;
        RunCredentialAlias = settings.RunCredentialAlias ?? string.Empty;
        TerminalSettingsRequest terminal = settings.Terminal ?? new();
        UseConPty = terminal.UseConPty; TerminalColumns = terminal.Columns; TerminalRows = terminal.Rows;
        InputEncoding = CanonicalEncoding(terminal.InputEncoding); OutputEncoding = CanonicalEncoding(terminal.OutputEncoding); NewLine = terminal.NewLine; AnsiEnabled = terminal.AnsiEnabled;
        RconEnabled = settings.Rcon.Enabled; RconPort = settings.Rcon.Port; RconPassword = string.Empty; ClearRconPassword = false;
        QueryEnabled = settings.Query.Enabled; QueryHost = settings.Query.Host; QueryPort = settings.Query.Port;
        UpdateActionRequest update = settings.UpdateAction ?? new();
        UpdateKind = update.Kind switch { "jar-replace" => 1, "hmcl-channel-sync" => 2, _ => 0 };
        UpdateSource = update.SourceRelativePath ?? string.Empty; UpdateTarget = update.TargetRelativePath ?? string.Empty;
        SelectedChannel = Channels.FirstOrDefault(channel => channel.Id == update.ChannelId);
        Shortcuts.Clear();
        foreach (ShortcutCommandRequest shortcut in settings.ShortcutCommands ?? []) { Shortcuts.Add(new(Localization, shortcut.Name, shortcut.Command)); }
        HasData = true; _baseline = Snapshot(); OnPropertyChanged(nameof(Ownership)); OnPropertyChanged(nameof(IsOwned));
    }
    private string CanonicalEncoding(string encoding) { return Encodings.FirstOrDefault(choice => string.Equals(choice.Value, encoding, StringComparison.OrdinalIgnoreCase))?.Value ?? encoding; }
    [RelayCommand(CanExecute = nameof(CanRead))]
    private async Task RefreshAsync() { if (await CanLeaveAsync().ConfigureAwait(true)) { await LoadAsync().ConfigureAwait(true); } }
    [RelayCommand(CanExecute = nameof(CanDiscard))]
    private void Discard()
    {
        _loading = true;
        try { Apply(_details!); }
        finally { _loading = false; Changed(); }
    }
    public async Task<bool> CanLeaveAsync() { return !IsDirty || await _confirm("desktop.unsaved-title", Text("desktop.unsaved-discard-impact")).ConfigureAwait(true); }
    [RelayCommand(CanExecute = nameof(CanAddShortcut))]
    private void AddShortcut() { Shortcuts.Add(new(Localization)); }
    [RelayCommand(CanExecute = nameof(CanEdit))]
    private void RemoveShortcut(ShortcutCommandEditor shortcut) { _ = Shortcuts.Remove(shortcut); }
    private void OnShortcutsChanged(object? sender, NotifyCollectionChangedEventArgs args)
    {
        foreach (ShortcutCommandEditor removed in _subscribedShortcuts.Where(shortcut => !Shortcuts.Contains(shortcut)).ToArray()) { removed.PropertyChanged -= OnEditorChanged; _ = _subscribedShortcuts.Remove(removed); }
        foreach (ShortcutCommandEditor added in Shortcuts.Where(shortcut => !_subscribedShortcuts.Contains(shortcut))) { added.PropertyChanged += OnEditorChanged; _ = _subscribedShortcuts.Add(added); }
        Changed();
    }
    private void OnEditorChanged(object? sender, PropertyChangedEventArgs args) { Changed(); }
    private void Changed()
    {
        if (_loading) { return; }
        foreach (string name in new[] { nameof(IsDirty), nameof(DirtyText), nameof(Validation), nameof(HasValidation), nameof(IsJarUpdate), nameof(IsChannelUpdate), nameof(HasRestriction), nameof(Restriction) }) { OnPropertyChanged(name); }
        RefreshCommands();
    }
    protected override void RefreshCommands()
    {
        foreach (string name in new[] { nameof(CanRead), nameof(CanEdit), nameof(CanSave), nameof(CanDiscard), nameof(CanUnregister), nameof(CanDeleteDirectory), nameof(CanAddShortcut) }) { OnPropertyChanged(name); }
        RefreshCommand.NotifyCanExecuteChanged(); SaveCommand.NotifyCanExecuteChanged(); DiscardCommand.NotifyCanExecuteChanged();
        UnregisterCommand.NotifyCanExecuteChanged(); DeleteDirectoryCommand.NotifyCanExecuteChanged(); AddShortcutCommand.NotifyCanExecuteChanged(); RemoveShortcutCommand.NotifyCanExecuteChanged();
    }
    public override void Dispose()
    {
        Launch.PropertyChanged -= OnEditorChanged; Launch.Dispose(); Shortcuts.CollectionChanged -= OnShortcutsChanged;
        foreach (ShortcutCommandEditor shortcut in _subscribedShortcuts) { shortcut.PropertyChanged -= OnEditorChanged; }
        RconPassword = string.Empty; base.Dispose();
    }
}
