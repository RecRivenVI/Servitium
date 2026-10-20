using System;

using Servitium.Desktop.Models;

namespace Servitium.Desktop.Services;

/// <summary>区分可持久化的定时暂停与仅在当前 Desktop 进程内有效的暂停。</summary>
internal sealed class NotificationPauseState
{
    internal bool UntilRestart { get; private set; }

    internal void PauseUntilRestart()
    {
        UntilRestart = true;
    }

    internal void ClearProcessPause()
    {
        UntilRestart = false;
    }

    internal bool IsPaused(DesktopSettings settings, DateTimeOffset now)
    {
        return UntilRestart || DesktopNotificationService.IsPaused(settings, now);
    }

    internal static DateTimeOffset? PersistedDeadline(NotificationPauseMode mode, DateTimeOffset now)
    {
        return mode switch
        {
            NotificationPauseMode.ThirtyMinutes => now.AddMinutes(30),
            NotificationPauseMode.OneHour => now.AddHours(1),
            NotificationPauseMode.UntilRestart or NotificationPauseMode.Cancel => null,
            _ => throw new ArgumentOutOfRangeException(nameof(mode)),
        };
    }
}
