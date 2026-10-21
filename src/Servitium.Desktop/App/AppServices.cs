using System;

using Microsoft.UI.Xaml;

using Servitium.Desktop.Services;

namespace Servitium.Desktop.AppModel;

internal sealed partial class AppServices : IDisposable
{
    internal AppServices(string? testDataDirectory = null)
    {
        TimeProvider = TimeProvider.System;
        Localization = new LocalizationService();
        Settings = new DesktopSettingsService(testDataDirectory);
        Log = new DesktopLogService(Settings.DataDirectory, TimeProvider);
        Profiles = new ServerProfileService(Settings, TimeProvider);
        Credentials = new WindowsCredentialSessionTokenStore(testDataDirectory);
        Session = new SessionService(Profiles, Credentials, Log, TimeProvider);
        WindowState = new WindowStateService(Settings);
        Notifications = new DesktopNotificationService(Session, Settings, Localization, Log, TimeProvider);
        TerminalWindows = new TerminalWindowService(this);
    }

    internal TimeProvider TimeProvider { get; }

    internal LocalizationService Localization { get; }

    internal DesktopSettingsService Settings { get; }

    internal DesktopLogService Log { get; }

    internal ServerProfileService Profiles { get; }

    internal WindowsCredentialSessionTokenStore Credentials { get; }

    internal SessionService Session { get; }

    internal WindowStateService WindowState { get; }

    internal DesktopNotificationService Notifications { get; }

    internal TerminalWindowService TerminalWindows { get; }

    internal UserInteractionService Interaction { get; private set; } = null!;

    internal TrayIconService Tray { get; private set; } = null!;

    internal void AttachWindow(Window window)
    {
        Interaction = new UserInteractionService(window, Localization);
        Tray = new TrayIconService(window, Localization);
    }

    public void Dispose()
    {
        TerminalWindows.Dispose();
        Notifications.Dispose();
        Session.Dispose();
        Tray?.Dispose();
        Log.Dispose();
        Settings.Dispose();
    }
}
