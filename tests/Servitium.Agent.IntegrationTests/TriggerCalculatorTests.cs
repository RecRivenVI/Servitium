using System;

using Servitium.Agent.Scheduling;
using Servitium.Contracts.Scheduling;
using Servitium.Domain.Common;

using Xunit;

namespace Servitium.Agent.IntegrationTests;

public sealed class TriggerCalculatorTests
{
    [Fact]
    [Trait("Category", "Unit")]
    [Trait("TestId", "UNT-400")]
    public void 三种触发器_计算下次UTC时间并拒绝无效边界()
    {
        DateTimeOffset after = new(2026, 8, 3, 12, 0, 0, TimeSpan.Zero);
        Assert.Equal(after.AddSeconds(10), TriggerCalculator.Next(new("interval", 10, null, null, null), after, TimeZoneInfo.Utc));
        _ = Assert.Throws<DomainException>(() => TriggerCalculator.Next(new("interval", 9, null, null, null), after, TimeZoneInfo.Utc));

        DateTimeOffset specified = after.AddMinutes(1);
        Assert.Equal(specified, TriggerCalculator.Next(new("specified", null, null, null, specified), after, TimeZoneInfo.Utc));
        Assert.Null(TriggerCalculator.Next(new("specified", null, null, null, after), after, TimeZoneInfo.Utc));

        ScheduleTriggerDto calendar = new("calendar", null, [(int)DayOfWeek.Tuesday], new TimeOnly(10, 30), null);
        Assert.Equal(new DateTimeOffset(2026, 8, 4, 10, 30, 0, TimeSpan.Zero), TriggerCalculator.Next(calendar, after, TimeZoneInfo.Utc));
        _ = Assert.Throws<DomainException>(() => TriggerCalculator.Next(new("calendar", null, [], new TimeOnly(10, 30), null), after, TimeZoneInfo.Utc));
    }

    [Fact]
    [Trait("Category", "Unit")]
    [Trait("TestId", "UNT-400")]
    public void 日历触发_跨周跨月且随节点时区重新计算()
    {
        DateTimeOffset mondayAfterTime = new(2026, 8, 31, 12, 0, 0, TimeSpan.Zero);
        ScheduleTriggerDto mondayMorning = new("calendar", null, [(int)DayOfWeek.Monday], new TimeOnly(10, 0), null);
        Assert.Equal(
            new DateTimeOffset(2026, 9, 7, 10, 0, 0, TimeSpan.Zero),
            TriggerCalculator.Next(mondayMorning, mondayAfterTime, TimeZoneInfo.Utc));

        ScheduleTriggerDto tuesdayMorning = new("calendar", null, [(int)DayOfWeek.Tuesday], new TimeOnly(9, 0), null);
        DateTimeOffset utc = TriggerCalculator.Next(tuesdayMorning, mondayAfterTime, TimeZoneInfo.Utc)!.Value;
        var utcPlusEight = TimeZoneInfo.CreateCustomTimeZone("Synthetic UTC+8", TimeSpan.FromHours(8), "Synthetic UTC+8", "Synthetic UTC+8");
        DateTimeOffset shifted = TriggerCalculator.Next(tuesdayMorning, mondayAfterTime, utcPlusEight)!.Value;
        Assert.Equal(new DateTimeOffset(2026, 9, 1, 9, 0, 0, TimeSpan.Zero), utc);
        Assert.Equal(new DateTimeOffset(2026, 9, 1, 1, 0, 0, TimeSpan.Zero), shifted);
    }
}
