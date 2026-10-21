using System.Globalization;
using System.Linq;

using Servitium.Contracts.Scheduling;
using Servitium.Desktop.Services;

namespace Servitium.Desktop.ViewModels.Instances;

internal static class SchedulePresentation
{
    internal static string Action(string kind, LocalizationService localization)
    {
        return localization.Get(kind switch
        {
            "send-console-command" => "desktop.schedule-action-command",
            "delay" => "desktop.schedule-action-delay",
            "start" => "action.start",
            "stop" => "action.stop",
            "restart" => "action.restart",
            "force-stop" => "action.force-stop",
            _ => "desktop.schedule-unknown-action",
        });
    }
    internal static string Trigger(ScheduleTriggerDto trigger, LocalizationService localization)
    {
        return trigger.Kind switch
        {
            "interval" => localization.Format("desktop.schedule-interval-summary", trigger.IntervalSeconds),
            "calendar" => localization.Format("desktop.schedule-calendar-summary", string.Join("、", (trigger.DaysOfWeek ?? []).Select(day => localization.Get("desktop.day-" + day.ToString(CultureInfo.InvariantCulture)))), trigger.LocalTime?.ToString("HH:mm", CultureInfo.CurrentCulture)),
            "specified" => trigger.SpecifiedAt?.ToLocalTime().ToString("g", CultureInfo.CurrentCulture) ?? localization.Get("desktop.not-provided"),
            _ => localization.Get("desktop.not-provided"),
        };
    }
    internal static string Result(ScheduledTaskRunDto? run, LocalizationService localization)
    {
        return localization.Get(run is null ? "desktop.schedule-never-run" : run.CompletedAt is null ? "state.running" : run.Succeeded ? "desktop.schedule-result-success" : "desktop.schedule-result-failure");
    }
}
