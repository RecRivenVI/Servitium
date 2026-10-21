using System.Collections.Generic;
using System.Collections.ObjectModel;

using Servitium.Contracts.Distribution;
using Servitium.Desktop.Models;

namespace Servitium.Desktop.ViewModels.Instances;

internal sealed partial class InstanceSettingsViewModel
{
    public string Name { get; set { if (SetProperty(ref field, value)) { Changed(); } } } = string.Empty;
    public string Tags { get; set { if (SetProperty(ref field, value)) { Changed(); } } } = string.Empty;
    public string Notes { get; set { if (SetProperty(ref field, value)) { Changed(); } } } = string.Empty;
    public string WorkingDirectory { get; set { if (SetProperty(ref field, value)) { Changed(); } } } = string.Empty;
    public double PrimaryPort { get; set { if (SetProperty(ref field, value)) { Changed(); } } } = double.NaN;
    public string StopCommand { get; set { if (SetProperty(ref field, value)) { Changed(); } } } = "stop";
    public double StopTimeout { get; set { if (SetProperty(ref field, value)) { Changed(); } } } = 60;
    public bool AutoStart { get; set { if (SetProperty(ref field, value)) { Changed(); } } }
    public double AutoStartDelay { get; set { if (SetProperty(ref field, value)) { Changed(); } } }
    public bool AutoRestart { get; set { if (SetProperty(ref field, value)) { Changed(); } } }
    public double MaximumRestartAttempts { get; set { if (SetProperty(ref field, value)) { Changed(); } } } = 3;
    public double RestartInterval { get; set { if (SetProperty(ref field, value)) { Changed(); } } } = 5;
    public double RestartReset { get; set { if (SetProperty(ref field, value)) { Changed(); } } } = 600;
    public string RunCredentialAlias { get; set { if (SetProperty(ref field, value)) { Changed(); } } } = string.Empty;
    public bool UseConPty { get; set { if (SetProperty(ref field, value)) { Changed(); } } } = true;
    public double TerminalColumns { get; set { if (SetProperty(ref field, value)) { Changed(); } } } = 164;
    public double TerminalRows { get; set { if (SetProperty(ref field, value)) { Changed(); } } } = 40;
    public string InputEncoding { get; set { if (SetProperty(ref field, value)) { Changed(); } } } = "UTF-8";
    public string OutputEncoding { get; set { if (SetProperty(ref field, value)) { Changed(); } } } = "UTF-8";
    public string NewLine { get; set { if (SetProperty(ref field, value)) { Changed(); } } } = "CRLF";
    public bool AnsiEnabled { get; set { if (SetProperty(ref field, value)) { Changed(); } } } = true;
    public bool RconEnabled { get; set { if (SetProperty(ref field, value)) { Changed(); } } }
    public double RconPort { get; set { if (SetProperty(ref field, value)) { Changed(); } } } = 25575;
    public string RconPassword { get; set { if (SetProperty(ref field, value)) { Changed(); } } } = string.Empty;
    public bool ClearRconPassword { get; set { if (SetProperty(ref field, value)) { Changed(); } } }
    public bool QueryEnabled { get; set { if (SetProperty(ref field, value)) { Changed(); } } }
    public string QueryHost { get; set { if (SetProperty(ref field, value)) { Changed(); } } } = "127.0.0.1";
    public double QueryPort { get; set { if (SetProperty(ref field, value)) { Changed(); } } } = 25565;
    public int UpdateKind { get; set { if (SetProperty(ref field, value)) { Changed(); } } }
    public string UpdateSource { get; set { if (SetProperty(ref field, value)) { Changed(); } } } = string.Empty;
    public string UpdateTarget { get; set { if (SetProperty(ref field, value)) { Changed(); } } } = string.Empty;
    public IReadOnlyList<HmclChannelDto> Channels { get; private set => SetProperty(ref field, value); } = [];
    public HmclChannelDto? SelectedChannel { get; set { if (SetProperty(ref field, value)) { Changed(); } } }
    public ObservableCollection<ShortcutCommandEditor> Shortcuts { get; } = [];
    public IReadOnlyList<SettingChoice> Encodings { get; private set; } = [];
    public IReadOnlyList<SettingChoice> NewLines { get; private set; } = [];
    public bool IsJarUpdate => UpdateKind == 1;
    public bool IsChannelUpdate => UpdateKind == 2;
    public bool HasData { get; private set => SetProperty(ref field, value); }
    public bool IsDirty => !_loading && HasData && Snapshot() != _baseline;
    public bool HasRestriction => !_administrator || !_reachable || _status is not ("Stopped" or "Crashed");
    public string Restriction => Text(!_administrator ? "desktop.permission-error" : !_reachable ? "desktop.instance-offline-next" : "desktop.instance-stop-required-next");
    public string DirtyText => Text(IsDirty ? "desktop.schedule-unsaved" : "desktop.config-saved");
    public string Ownership => Text(_details?.Instance.OwnedByServitium == true ? "desktop.settings-owned-directory" : "desktop.settings-external-directory");
    public bool IsOwned => _details?.Instance.OwnedByServitium == true;
    public bool CanRead => CanInteract && _administrator && _reachable;
    public bool CanEdit => CanRead && HasData && _status is "Stopped" or "Crashed";
    public bool CanSave => CanEdit && IsDirty && !HasValidation;
    public bool CanDiscard => CanInteract && IsDirty;
    public bool CanUnregister => CanEdit;
    public bool CanDeleteDirectory => CanRead && HasData && _details?.Instance.OwnedByServitium == true && _status == "Stopped";
    public bool CanAddShortcut => CanEdit && Shortcuts.Count < 20;
}
