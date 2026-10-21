using Servitium.Contracts.Instances;
using Servitium.Desktop.Services;

namespace Servitium.Desktop.ViewModels.Instances;

internal sealed class InstanceRow(InstanceDto value, LocalizationService localization, bool reachable)
{
    public InstanceDto Value { get; } = value;
    public string Name => Value.Name;
    public bool IsReachable { get; } = reachable;
    public string State => IsReachable ? Value.Status : "Unreachable";
    public string Status => localization.Get(State switch
    {
        "Running" => "desktop.instance-state-running",
        "Stopped" => "desktop.instance-state-stopped",
        "Crashed" => "desktop.instance-state-crashed",
        "Failed" => "desktop.instance-state-failed",
        "Unreachable" => "desktop.instance-state-unreachable",
        "Starting" => "desktop.instance-state-starting",
        "Stopping" => "desktop.instance-state-stopping",
        "Restarting" => "desktop.instance-state-restarting",
        "Creating" => "desktop.instance-state-creating",
        _ => "desktop.instance-state-unknown",
    });
    public string Version => localization.Get("desktop.version-not-reported");
    public string Detail => Value.Status == "Running" && Value.OnlinePlayers is int count
        ? localization.Format("desktop.players-count", count) : Status;
    public bool NeedsAttention => !IsReachable || Value.Status is "Crashed" or "Failed" or "Unknown";
    public override string ToString() { return Name; }
}
