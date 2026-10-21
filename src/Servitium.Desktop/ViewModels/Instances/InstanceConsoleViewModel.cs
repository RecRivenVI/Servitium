using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

using CommunityToolkit.Mvvm.Input;

using Servitium.Client;
using Servitium.Contracts.Distribution;
using Servitium.Contracts.Events;
using Servitium.Contracts.Instances;
using Servitium.Contracts.Java;
using Servitium.Contracts.Nodes;
using Servitium.Desktop.Services;

namespace Servitium.Desktop.ViewModels.Instances;

internal sealed partial class InstanceConsoleViewModel(
    LocalizationService localization,
    ServitiumApiClient client,
    InstanceDto instance,
    NodeDto? node,
    bool reachable,
    bool administrator,
    IReadOnlyList<HmclChannelDto> channels) : DesktopViewModel(localization)
{
    private InstanceDetailsDto? _details;
    private InstanceRuntimeDto? _runtime;
    private DateTimeOffset? _crashedAt;
    public string Command { get; set { if (SetProperty(ref field, value)) { SendCommand.NotifyCanExecuteChanged(); } } } = string.Empty;
    public bool IsReachable { get; private set => SetProperty(ref field, value); } = reachable;
    public bool IsRunning => IsReachable && _runtime?.Status == "Running";
    public string RunningNote => IsRunning ? string.Empty : Text("desktop.console-command-disabled");
    public bool HasRunningNote => !IsRunning;
    public string StateMessage { get; private set => SetProperty(ref field, value); } = string.Empty;
    public bool HasStateMessage => StateMessage.Length > 0;
    public string RecentOutput { get; private set { if (SetProperty(ref field, value)) { OnPropertyChanged(nameof(OutputFallback)); } } } = string.Empty;
    public string OutputFallback => RecentOutput.Length > 0 ? RecentOutput : Text("desktop.console-output-empty");
    public string Players { get; private set => SetProperty(ref field, value); } = string.Empty;
    public IReadOnlyList<string> PlayerNames { get; private set => SetProperty(ref field, value); } = [];
    public string Resources { get; private set => SetProperty(ref field, value); } = string.Empty;
    public string Java { get; private set => SetProperty(ref field, value); } = string.Empty;
    public string Version { get; private set => SetProperty(ref field, value); } = string.Empty;
    public string WorkingDirectory => instance.WorkingDirectory;
    public string Address => instance.PrimaryPort is int port && node?.PublicAddress is { Length: > 0 } host
        ? host + ":" + port.ToString(System.Globalization.CultureInfo.InvariantCulture) : Text("desktop.address-not-reported");
    public string AutomaticStart => Text(_details?.Configuration?.AutoStart == true ? "state.enabled" : "state.disabled");
    public string AutomaticRestart => Text(_details?.Configuration?.AutoRestart == true ? "state.enabled" : "state.disabled");
    public string Channel => _details?.Configuration?.UpdateAction?.ChannelId is Guid channel
        ? channels.FirstOrDefault(value => value.Id == channel)?.Name ?? Text("desktop.channel-not-visible") : Text("desktop.no-associated-channel");
    public string Uptime => _runtime is { Status: "Running", StartedAt: DateTimeOffset started }
        ? Format("desktop.instance-uptime", (int)Math.Max(0, (client.TimeProvider.GetUtcNow() - started).TotalHours), Math.Max(0, (client.TimeProvider.GetUtcNow() - started).Minutes)) : Text("desktop.not-running");
    public IReadOnlyList<ShortcutCommandRequest> Shortcuts => _details?.Configuration?.ShortcutCommands ?? [];
    public bool HasShortcuts => Shortcuts.Count > 0;
    public bool CanType => CanInteract && IsRunning;
    public bool CanSend => CanInteract && IsRunning && ValidCommand(Command);
    private static bool ValidCommand(string command) { return !string.IsNullOrWhiteSpace(command) && !command.Any(char.IsControl); }

    public Task LoadAsync()
    {
        return RunAsync(async token =>
        {
            _details = await client.GetInstanceAsync(instance.Id, token).ConfigureAwait(true);
            Java = Text(administrator ? "desktop.java-not-reported" : "desktop.java-administrator-only");
            Version = Text("desktop.version-not-reported");
            PlayerNames = [];
            Players = Text("desktop.players-not-running"); Resources = Text("desktop.metrics-not-running");
            if (!IsReachable)
            {
                StateMessage = Text("desktop.instance-offline-next"); NotifyState(); return;
            }
            _runtime = await client.GetInstanceStatusAsync(instance.Id, instance.NodeId, token).ConfigureAwait(true);
            RecentOutput = string.Join(Environment.NewLine, _runtime.RecentOutput);
            SetRuntimeMessage(); NotifyState();
            if (administrator)
            {
                JavaRuntimeDetailsDto runtime = await client.GetJavaRuntimeAsync(instance.NodeId, _details.Launch.JavaRuntimeId, token).ConfigureAwait(true);
                Java = string.Join(" · ", runtime.Runtime.DisplayName ?? runtime.Runtime.Version, runtime.Runtime.ExecutablePath);
            }
            if (IsRunning)
            {
                InstanceMetricsDto metrics = await client.GetInstanceMetricsAsync(instance.Id, instance.NodeId, token).ConfigureAwait(true);
                Resources = Format("desktop.console-resources", metrics.CpuPercent, metrics.MemoryBytes / 1024d / 1024d);
                if (_details.Configuration?.Query.Enabled == true)
                {
                    try
                    {
                        PlayerListDto players = await client.GetInstancePlayersAsync(instance.Id, instance.NodeId, token).ConfigureAwait(true);
                        Players = Format("desktop.console-players", players.Online, players.Maximum); PlayerNames = players.Players;
                        Version = players.Version ?? Text("desktop.version-not-reported");
                    }
                    catch (ServitiumApiException exception) when (exception.Code is "query-disabled" or "query-unavailable" or "query-failed" or "query-timeout")
                    {
                        Players = Text("desktop.players-query-unavailable");
                    }
                }
                else { Players = Text("desktop.players-query-disabled"); }
            }
            NotifyState();
        });
    }

    private void SetRuntimeMessage()
    {
        if (_runtime?.Status != "Crashed") { StateMessage = string.Empty; return; }
        bool memory = _runtime.RecentOutput.Any(line => line.Contains("OutOfMemoryError", StringComparison.Ordinal) || line.Contains("Java heap space", StringComparison.Ordinal));
        StateMessage = Format(memory ? administrator ? "desktop.crash-memory-next" : "desktop.crash-memory-contact" : "desktop.crash-exit-next",
            _crashedAt?.ToLocalTime().ToString("g", System.Globalization.CultureInfo.CurrentCulture) ?? Text("desktop.crash-time-not-reported"),
            _runtime.LastExitCode?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? Text("desktop.value-not-reported"));
        OnPropertyChanged(nameof(HasStateMessage));
    }

    [RelayCommand(CanExecute = nameof(CanSend))]
    private Task SendAsync()
    {
        return SendCoreAsync(Command, true);
    }
    private bool CanSendShortcut(ShortcutCommandRequest? shortcut) { return CanInteract && IsRunning && shortcut is not null && ValidCommand(shortcut.Command); }
    [RelayCommand(CanExecute = nameof(CanSendShortcut))]
    private Task SendShortcutAsync(ShortcutCommandRequest shortcut) { return SendCoreAsync(shortcut.Command, false); }
    private Task SendCoreAsync(string command, bool clearInput)
    {
        return RunAsync(async token =>
        {
            _runtime = await client.SendInstanceCommandAsync(instance.Id, instance.NodeId, new SendInstanceCommandRequest(command), token).ConfigureAwait(true);
            if (clearInput) { Command = string.Empty; }
            RecentOutput = string.Join(Environment.NewLine, _runtime.RecentOutput);
            NotifyState();
        });
    }
    public void Tick() { OnPropertyChanged(nameof(Uptime)); }
    internal bool ApplyEvent(RealtimeEventDto value)
    {
        switch (value.Type)
        {
            case EventTypes.NodeStatusChanged:
                NodeStatusChangedPayload? nodeState = value.Payload.Deserialize<NodeStatusChangedPayload>(JsonSerializerOptions.Web);
                if (nodeState?.NodeId != instance.NodeId) { return false; }
                IsReachable = nodeState.Status == "Online";
                if (!IsReachable)
                {
                    StateMessage = Text("desktop.instance-offline-next");
                    Players = Text("desktop.players-node-offline"); Resources = Text("desktop.metrics-node-offline"); PlayerNames = [];
                }
                NotifyState(); return IsReachable;
            case EventTypes.InstanceResourceUsage:
                InstanceResourceUsagePayload? metrics = value.Payload.Deserialize<InstanceResourceUsagePayload>(JsonSerializerOptions.Web);
                if (metrics?.InstanceId == instance.Id) { ApplyMetrics(metrics.CpuPercent, metrics.MemoryBytes); }
                return false;
            case EventTypes.InstancePlayersChanged:
                InstancePlayersChangedPayload? players = value.Payload.Deserialize<InstancePlayersChangedPayload>(JsonSerializerOptions.Web);
                if (players?.InstanceId == instance.Id) { ApplyPlayers(players.OnlinePlayers, players.MaximumPlayers); }
                return false;
            case EventTypes.InstanceCrashed:
                InstanceCrashedPayload? crash = value.Payload.Deserialize<InstanceCrashedPayload>(JsonSerializerOptions.Web);
                if (crash?.InstanceId != instance.Id) { return false; }
                _crashedAt = value.OccurredAt;
                _runtime = new(instance.Id, instance.NodeId, "Crashed", null, _runtime?.StartedAt, crash.ExitCode, crash.RecentOutput);
                RecentOutput = string.Join(Environment.NewLine, crash.RecentOutput);
                SetRuntimeMessage(); NotifyState(); return true;
            case EventTypes.InstanceStatusChanged:
                InstanceStatusChangedPayload? state = value.Payload.Deserialize<InstanceStatusChangedPayload>(JsonSerializerOptions.Web);
                if (state?.InstanceId != instance.Id) { return false; }
                _runtime = new(instance.Id, instance.NodeId, state.Status, _runtime?.ProcessId, _runtime?.StartedAt, state.ExitCode, _runtime?.RecentOutput ?? []);
                SetRuntimeMessage(); NotifyState(); return true;
            default: return false;
        }
    }
    internal void ApplyMetrics(double cpuPercent, long memoryBytes)
    {
        if (IsRunning) { Resources = Format("desktop.console-resources", cpuPercent, memoryBytes / 1024d / 1024d); }
    }
    internal void ApplyPlayers(int online, int maximum)
    {
        if (IsRunning) { Players = Format("desktop.console-players", online, maximum); }
    }
    private void NotifyState()
    {
        foreach (string property in new[] { nameof(IsRunning), nameof(RunningNote), nameof(HasRunningNote), nameof(HasStateMessage), nameof(AutomaticStart), nameof(AutomaticRestart), nameof(Channel), nameof(Uptime), nameof(Shortcuts), nameof(HasShortcuts) }) { OnPropertyChanged(property); }
        RefreshCommands();
    }
    protected override void RefreshCommands() { OnPropertyChanged(nameof(CanSend)); OnPropertyChanged(nameof(CanType)); SendCommand.NotifyCanExecuteChanged(); SendShortcutCommand.NotifyCanExecuteChanged(); }
}
