using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

using CommunityToolkit.Mvvm.ComponentModel;

using Servitium.Contracts.Scheduling;
using Servitium.Desktop.Services;

namespace Servitium.Desktop.ViewModels.Instances;

internal sealed partial class ScheduleTriggerEditor : ObservableObject
{
    private readonly LocalizationService _localization;
    internal ScheduleTriggerEditor(LocalizationService localization, TimeProvider timeProvider)
    {
        _localization = localization;
        DateTimeOffset tomorrow = timeProvider.GetLocalNow().AddDays(1);
        SpecifiedDate = new DateTimeOffset(tomorrow.Date, tomorrow.Offset);
    }
    public int KindIndex { get; set { if (SetProperty(ref field, value)) { Changed(); } } }
    public bool IsInterval => KindIndex == 0;
    public bool IsCalendar => KindIndex == 1;
    public bool IsSpecified => KindIndex == 2;
    public string IntervalSeconds { get; set { if (SetProperty(ref field, value)) { Changed(); } } } = "300";
    public TimeSpan CalendarTime { get; set { if (SetProperty(ref field, value)) { Changed(); } } } = TimeSpan.FromHours(4);
    public DateTimeOffset? SpecifiedDate { get; set { if (SetProperty(ref field, value)) { Changed(); } } }
    public TimeSpan SpecifiedTime { get; set { if (SetProperty(ref field, value)) { Changed(); } } } = TimeSpan.FromHours(4);
    public bool Sunday { get; set { if (SetProperty(ref field, value)) { Changed(); } } }
    public bool Monday { get; set { if (SetProperty(ref field, value)) { Changed(); } } } = true;
    public bool Tuesday { get; set { if (SetProperty(ref field, value)) { Changed(); } } } = true;
    public bool Wednesday { get; set { if (SetProperty(ref field, value)) { Changed(); } } } = true;
    public bool Thursday { get; set { if (SetProperty(ref field, value)) { Changed(); } } } = true;
    public bool Friday { get; set { if (SetProperty(ref field, value)) { Changed(); } } } = true;
    public bool Saturday { get; set { if (SetProperty(ref field, value)) { Changed(); } } }
    private List<int> Days => [.. new[] { Sunday, Monday, Tuesday, Wednesday, Thursday, Friday, Saturday }.Select((enabled, day) => (enabled, day)).Where(item => item.enabled).Select(item => item.day)];
    public string Validation => TryBuild(out _) ? string.Empty : _localization.Get("desktop.schedule-trigger-invalid");
    internal bool TryBuild(out ScheduleTriggerDto? trigger)
    {
        trigger = null;
        if (IsInterval && int.TryParse(IntervalSeconds, NumberStyles.None, CultureInfo.InvariantCulture, out int seconds) && seconds is >= 10 and <= 31536000)
        { trigger = new("interval", seconds, null, null, null); }
        else if (IsCalendar && Days.Count > 0 && CalendarTime >= TimeSpan.Zero && CalendarTime < TimeSpan.FromDays(1))
        { trigger = new("calendar", null, Days, TimeOnly.FromTimeSpan(CalendarTime), null); }
        else if (IsSpecified && SpecifiedDate is { } date && SpecifiedTime >= TimeSpan.Zero && SpecifiedTime < TimeSpan.FromDays(1))
        {
            var local = DateTime.SpecifyKind(date.Date.Add(SpecifiedTime), DateTimeKind.Unspecified);
            if (!TimeZoneInfo.Local.IsInvalidTime(local)) { trigger = new("specified", null, null, null, new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(local))); }
        }
        return trigger is not null;
    }
    internal void Load(ScheduleTriggerDto trigger)
    {
        KindIndex = trigger.Kind switch { "calendar" => 1, "specified" => 2, _ => 0 };
        IntervalSeconds = trigger.IntervalSeconds?.ToString(CultureInfo.InvariantCulture) ?? "300";
        CalendarTime = trigger.LocalTime?.ToTimeSpan() ?? TimeSpan.FromHours(4);
        Sunday = trigger.DaysOfWeek?.Contains(0) == true; Monday = trigger.DaysOfWeek?.Contains(1) == true;
        Tuesday = trigger.DaysOfWeek?.Contains(2) == true; Wednesday = trigger.DaysOfWeek?.Contains(3) == true;
        Thursday = trigger.DaysOfWeek?.Contains(4) == true; Friday = trigger.DaysOfWeek?.Contains(5) == true; Saturday = trigger.DaysOfWeek?.Contains(6) == true;
        if (trigger.SpecifiedAt is { } specified) { DateTimeOffset local = specified.ToLocalTime(); SpecifiedDate = local; SpecifiedTime = local.TimeOfDay; }
    }
    private void Changed()
    {
        OnPropertyChanged(nameof(IsInterval)); OnPropertyChanged(nameof(IsCalendar)); OnPropertyChanged(nameof(IsSpecified)); OnPropertyChanged(nameof(Validation));
    }
    internal string Snapshot()
    {
        return KindIndex switch
        {
            0 => "interval|" + IntervalSeconds,
            1 => "calendar|" + CalendarTime + "|" + string.Join(',', Days),
            _ => "specified|" + SpecifiedDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + "|" + SpecifiedTime,
        };
    }
}
