using System;
using System.Collections.Generic;

namespace Servitium.Desktop.Models;

internal sealed record ServerProfile(Guid Id, string Name, Uri Address, DateTimeOffset LastUsedAt);

internal sealed record WindowGeometry(int X, int Y, int Width, int Height, bool Maximized);

internal sealed record DesktopSettings(
    IReadOnlyList<ServerProfile> Servers,
    Guid? ActiveServerId,
    string Language,
    string Theme,
    bool CloseToTray,
    bool NotificationsPaused,
    WindowGeometry? MainWindow,
    IReadOnlyList<Guid> RecentInstanceIds,
    string? LastPageId = null,
    IReadOnlyDictionary<string, bool>? NotificationPreferences = null,
    DateTimeOffset? NotificationsPausedUntil = null,
    bool CloseToTrayExplained = false,
    double TerminalFontSize = 14,
    IReadOnlyDictionary<Guid, WindowGeometry>? TerminalWindows = null,
    bool GrantSafetyExplained = false);

[System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1812:Avoid uninstantiated internal classes", Justification = "The formal page layer constructs list items after the application shell is activated.")]
internal sealed record PageListItem(string Primary, string? Secondary, object Value)
{
    public override string ToString()
    {
        return string.IsNullOrWhiteSpace(Secondary) ? Primary : Primary + " — " + Secondary;
    }
}

internal sealed record DeepLinkTarget(string PageId, Guid? ResourceId, IReadOnlyDictionary<string, string> Parameters);

internal enum NotificationPauseMode
{
    ThirtyMinutes,
    OneHour,
    UntilRestart,
    Cancel,
}

internal sealed class NotificationPauseRequestedEventArgs(NotificationPauseMode mode) : EventArgs
{
    internal NotificationPauseMode Mode { get; } = mode;
}

internal sealed class TrayInstanceActionEventArgs(Guid instanceId, string action) : EventArgs
{
    internal Guid InstanceId { get; } = instanceId;

    internal string Action { get; } = action;
}
