using System;
using System.Collections.Generic;
using System.Threading.Tasks;

using CommunityToolkit.Mvvm.Input;

using Servitium.Desktop.Models;
using Servitium.Desktop.Services;

namespace Servitium.Desktop.ViewModels.Settings;

internal sealed partial class LocalPreferencesViewModel : DesktopViewModel
{
    private readonly DesktopSettingsService _settings;
    private readonly string _category;
    private readonly Func<string, string, Task<bool>> _confirm;
    private readonly Action<int> _pause;
    private readonly TimeProvider _clock;
    private string _baseline = string.Empty;
    private int _savedPause;
    public LocalPreferencesViewModel(LocalizationService localization, DesktopSettingsService settings, string category,
        Func<string, string, Task<bool>> confirm, Action<int> pause, TimeProvider clock, bool currentlyPaused) : base(localization)
    {
        _settings = settings; _category = category; _confirm = confirm; _pause = pause; _clock = clock;
        DesktopSettings current = settings.Current;
        ThemeIndex = current.Theme switch { "light" => 1, "dark" => 2, _ => 0 };
        LanguageIndex = current.Language == "en-US" ? 1 : 0;
        TerminalFontSize = current.TerminalFontSize; CloseToTray = current.CloseToTray;
        PauseIndex = currentlyPaused ? current.NotificationsPausedUntil is null ? 3 : 1 : 0; _savedPause = PauseIndex;
        InstanceCrashed = current.NotificationPreferences?.GetValueOrDefault("instance-crashed") ?? DesktopNotificationService.DefaultEnabled("instance-crashed");
        RestartExhausted = current.NotificationPreferences?.GetValueOrDefault("auto-restart-exhausted") ?? DesktopNotificationService.DefaultEnabled("auto-restart-exhausted");
        NodeOffline = current.NotificationPreferences?.GetValueOrDefault("node-offline") ?? DesktopNotificationService.DefaultEnabled("node-offline");
        NodeOnline = current.NotificationPreferences?.GetValueOrDefault("node-online") ?? DesktopNotificationService.DefaultEnabled("node-online");
        OperationCompleted = current.NotificationPreferences?.GetValueOrDefault("operation-completed") ?? DesktopNotificationService.DefaultEnabled("operation-completed");
        OperationFailed = current.NotificationPreferences?.GetValueOrDefault("operation-failed") ?? DesktopNotificationService.DefaultEnabled("operation-failed");
        HmclPublished = current.NotificationPreferences?.GetValueOrDefault("hmcl-published") ?? DesktopNotificationService.DefaultEnabled("hmcl-published");
        HmclIntegrityAlert = current.NotificationPreferences?.GetValueOrDefault("hmcl-integrity-alert") ?? DesktopNotificationService.DefaultEnabled("hmcl-integrity-alert");
        _baseline = Snapshot(); PropertyChanged += OnEdited;
    }
    public int ThemeIndex { get; set => SetProperty(ref field, value); }
    public int LanguageIndex { get; set => SetProperty(ref field, value); }
    public double TerminalFontSize { get; set => SetProperty(ref field, value); }
    public bool CloseToTray { get; set => SetProperty(ref field, value); }
    public int PauseIndex { get; set => SetProperty(ref field, value); }
    public bool InstanceCrashed { get; set => SetProperty(ref field, value); }
    public bool RestartExhausted { get; set => SetProperty(ref field, value); }
    public bool NodeOffline { get; set => SetProperty(ref field, value); }
    public bool NodeOnline { get; set => SetProperty(ref field, value); }
    public bool OperationCompleted { get; set => SetProperty(ref field, value); }
    public bool OperationFailed { get; set => SetProperty(ref field, value); }
    public bool HmclPublished { get; set => SetProperty(ref field, value); }
    public bool HmclIntegrityAlert { get; set => SetProperty(ref field, value); }
    public bool IsDirty => _baseline != Snapshot();
    public bool HasValidation => _category switch
    {
        "appearance" => ThemeIndex is < 0 or > 2 || LanguageIndex is < 0 or > 1,
        "terminal" => !double.IsFinite(TerminalFontSize) || TerminalFontSize is < 8 or > 32,
        _ => PauseIndex is < 0 or > 3,
    };
    public string Validation => HasValidation ? Text("desktop.local-settings-invalid") : string.Empty;
    public bool CanSave => CanInteract && IsDirty && !HasValidation;
    public string DirtyText => Text(IsDirty ? "desktop.settings-unsaved" : "desktop.all-saved");
    private string Snapshot()
    {
        return _category switch
        {
            "appearance" => $"{ThemeIndex}|{LanguageIndex}",
            "terminal" => TerminalFontSize.ToString(System.Globalization.CultureInfo.InvariantCulture),
            _ => $"{CloseToTray}|{PauseIndex}|" + InstanceCrashed.ToString() + "|" + RestartExhausted.ToString() + "|" + NodeOffline.ToString() + "|" + NodeOnline.ToString() + "|" + OperationCompleted.ToString() + "|" + OperationFailed.ToString() + "|" + HmclPublished.ToString() + "|" + HmclIntegrityAlert.ToString(),
        };
    }

    [RelayCommand(CanExecute = nameof(CanSave))]
    private Task SaveAsync()
    {
        return RunAsync(async token =>
    {
        await _settings.UpdateAsync(current => _category switch
        {
            "appearance" => current with { Theme = ThemeIndex switch { 1 => "light", 2 => "dark", _ => "system" }, Language = LanguageIndex == 1 ? "en-US" : "zh-Hans" },
            "terminal" => current with { TerminalFontSize = TerminalFontSize },
            _ => current with
            {
                CloseToTray = CloseToTray,
                NotificationPreferences = new Dictionary<string, bool>(StringComparer.Ordinal) { ["instance-crashed"] = InstanceCrashed, ["auto-restart-exhausted"] = RestartExhausted, ["node-offline"] = NodeOffline, ["node-online"] = NodeOnline, ["operation-completed"] = OperationCompleted, ["operation-failed"] = OperationFailed, ["hmcl-published"] = HmclPublished, ["hmcl-integrity-alert"] = HmclIntegrityAlert },
                NotificationsPaused = PauseIndex != _savedPause ? PauseIndex is 1 or 2 : current.NotificationsPaused,
                NotificationsPausedUntil = PauseIndex != _savedPause ? PauseIndex switch { 1 => _clock.GetUtcNow().AddMinutes(30), 2 => _clock.GetUtcNow().AddHours(1), _ => null } : current.NotificationsPausedUntil,
            },
        }, token).ConfigureAwait(true);
        if (_category == "notifications" && PauseIndex != _savedPause) { _pause(PauseIndex); _savedPause = PauseIndex; }
        _baseline = Snapshot(); OnPropertyChanged(nameof(IsDirty)); OnPropertyChanged(nameof(DirtyText));
        Message = Text(_category == "appearance" ? "desktop.local-appearance-saved" : "desktop.local-settings-saved");
    });
    }

    public async Task<bool> CanLeaveAsync()
    {
        return !IsDirty || await _confirm("desktop.unsaved-title", Text("desktop.settings-discard")).ConfigureAwait(true);
    }

    private void OnEdited(object? sender, System.ComponentModel.PropertyChangedEventArgs args)
    {
        if (args.PropertyName is nameof(IsDirty) or nameof(HasValidation) or nameof(Validation) or nameof(CanSave) or nameof(DirtyText) or nameof(IsBusy) or nameof(CanInteract) or nameof(Message) or nameof(HasMessage) or nameof(Severity)) { return; }
        OnPropertyChanged(nameof(IsDirty)); OnPropertyChanged(nameof(HasValidation)); OnPropertyChanged(nameof(Validation)); OnPropertyChanged(nameof(DirtyText)); RefreshCommands();
    }
    protected override void RefreshCommands() { OnPropertyChanged(nameof(CanSave)); SaveCommand.NotifyCanExecuteChanged(); }
    public override void Dispose() { PropertyChanged -= OnEdited; base.Dispose(); }
}
