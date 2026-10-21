using System.Globalization;

using CommunityToolkit.Mvvm.ComponentModel;

using Servitium.Contracts.Scheduling;
using Servitium.Desktop.Services;

namespace Servitium.Desktop.ViewModels.Instances;

internal sealed partial class ScheduleRow(ScheduledTaskDetailsDto value, LocalizationService localization) : ObservableObject
{
    public ScheduledTaskDetailsDto Value { get; private set; } = value;
    public string Name => Value.Name;
    public string RunLabel => localization.Get("action.run-now");
    public string Trigger => SchedulePresentation.Trigger(Value.Trigger, localization);
    public string NextRun => Value.NextRunAt?.ToLocalTime().ToString("g", CultureInfo.CurrentCulture) ?? localization.Get(
        Value.RemainingRuns == 0 ? "desktop.schedule-runs-exhausted" : !Value.Enabled ? "desktop.schedule-enable-next" : "desktop.schedule-check-time-next");
    public bool Enabled => Value.Enabled;
    public ScheduledTaskRunDto? LastRun { get; internal set { if (SetProperty(ref field, value)) { OnPropertyChanged(nameof(LastResult)); } } }
    public string LastResult => SchedulePresentation.Result(LastRun, localization);
    public bool CanOperate { get; internal set => SetProperty(ref field, value); }
    internal void Update(ScheduledTaskDetailsDto result)
    {
        Value = result; OnPropertyChanged(nameof(Name)); OnPropertyChanged(nameof(Trigger)); OnPropertyChanged(nameof(NextRun)); OnPropertyChanged(nameof(Enabled));
    }
    internal void ResetToggle() { OnPropertyChanged(nameof(Enabled)); }
    public override string ToString() { return Name; }
}
