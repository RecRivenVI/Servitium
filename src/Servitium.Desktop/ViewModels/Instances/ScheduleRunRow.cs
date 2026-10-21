using System;
using System.Globalization;
using System.Linq;

using Servitium.Client;
using Servitium.Contracts.Scheduling;
using Servitium.Desktop.Services;

namespace Servitium.Desktop.ViewModels.Instances;

internal sealed class ScheduleRunRow(ScheduledTaskRunDto value, LocalizationService localization)
{
    public string Started => value.StartedAt.ToLocalTime().ToString("g", CultureInfo.CurrentCulture);
    public string Result => SchedulePresentation.Result(value, localization);
    public string Details => string.Join(Environment.NewLine, value.ActionResults.Select(result =>
    {
        string[] parts = result.Split(':', 2);
        string outcome = parts.Length == 2 && parts[1] == "succeeded" ? localization.Get("desktop.schedule-result-success") :
            DesktopErrorPresentation.Describe(new ServitiumApiException(parts.Length == 2 ? parts[1] : value.OutcomeCode), localization);
        return SchedulePresentation.Action(parts[0], localization) + "：" + outcome;
    }));
    public override string ToString() { return Started + " · " + Result; }
}
