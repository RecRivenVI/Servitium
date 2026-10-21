using System;
using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Servitium.Contracts.Instances;
using Servitium.Desktop.Models;
using Servitium.Desktop.Services;

namespace Servitium.Desktop;

internal sealed partial class MainWindow
{
    private void OnAppWindowClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (!_exitRequested && _services.Settings.Current.CloseToTray)
        {
            args.Cancel = true;
            _ = HandleCloseToTrayAsync(sender);
        }
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "The close-to-tray boundary must contain local settings failures and keep the running client usable.")]
    private async Task HandleCloseToTrayAsync(AppWindow sender)
    {
        if (_closePromptPending)
        {
            return;
        }

        _closePromptPending = true;
        try
        {
            if (!_services.Settings.Current.CloseToTrayExplained)
            {
                await _services.Interaction.ShowAsync("shell.close-to-tray", _services.Localization.Get("tray.close-explanation")).ConfigureAwait(true);
                _ = await TryUpdateSettingsAsync(current => current with { CloseToTrayExplained = true }, "settings.close-explanation-save-failed").ConfigureAwait(true);
            }

            sender.Hide();
        }
        catch (Exception exception)
        {
            await _services.Log.WriteAsync("warning", "tray.close-failed", exception.GetType().Name).ConfigureAwait(true);
        }
        finally
        {
            _closePromptPending = false;
        }
    }

    private void OnTrayShowRequested(object? sender, EventArgs e)
    {
        _appWindow.Show();
        Activate();
    }

    private async void OnTrayPauseRequested(object? sender, NotificationPauseRequestedEventArgs e)
    {
        DateTimeOffset now = _services.TimeProvider.GetUtcNow();
        if (e.Mode == NotificationPauseMode.UntilRestart)
        {
            bool clearedPersistentPause = await TryUpdateSettingsAsync(
                current => current with { NotificationsPaused = false, NotificationsPausedUntil = null },
                "settings.pause-save-failed").ConfigureAwait(true);
            if (clearedPersistentPause)
            {
                _services.Notifications.PauseUntilRestart();
            }

            return;
        }

        bool paused = e.Mode is NotificationPauseMode.ThirtyMinutes or NotificationPauseMode.OneHour;
        DateTimeOffset? until = NotificationPauseState.PersistedDeadline(e.Mode, now);
        _services.Notifications.ClearProcessPause();
        bool saved = await TryUpdateSettingsAsync(
            current => current with { NotificationsPaused = paused, NotificationsPausedUntil = until },
            "settings.pause-save-failed").ConfigureAwait(true);
        if (saved && !paused)
        {
            _services.Notifications.ResumeAndFlush();
        }
    }

    private async void OnTrayInstanceActionRequested(object? sender, TrayInstanceActionEventArgs e)
    {
        InstanceDto? instance = _services.Session.Instances.FirstOrDefault(item => item.Id == e.InstanceId);
        if (instance is null)
        {
            return;
        }

        try
        {
            _services.Session.SelectedInstance = instance;
            if (e.Action == "open-console")
            {
                OnTrayShowRequested(this, EventArgs.Empty);
                Navigate("PG-012");
                return;
            }

            _ = e.Action switch
            {
                "start" => await _services.Session.Client.StartInstanceAsync(instance.Id, instance.NodeId, CancellationToken.None).ConfigureAwait(true),
                "stop" => await _services.Session.Client.StopInstanceAsync(instance.Id, instance.NodeId, CancellationToken.None).ConfigureAwait(true),
                "restart" => await _services.Session.Client.RestartInstanceAsync(instance.Id, instance.NodeId, CancellationToken.None).ConfigureAwait(true),
                _ => throw new ArgumentOutOfRangeException(nameof(e)),
            };
        }
        catch (Exception exception) when (exception is Client.ServitiumApiException or InvalidOperationException or ArgumentException)
        {
            OnTrayShowRequested(this, EventArgs.Empty);
            ShowError(exception);
        }
    }

    private async void OnTrayAboutRequested(object? sender, EventArgs e)
    {
        await _services.Interaction.ShowAsync("tray.about", _services.Localization.Get("app.description")).ConfigureAwait(true);
    }

    private async void OnTrayExitRequested(object? sender, EventArgs e)
    {
        if (!await _services.Interaction.ConfirmAsync("shell.exit", _services.Localization.Get("tray.exit-confirmation")).ConfigureAwait(true))
        {
            return;
        }

        _exitRequested = true;
        _services.Tray.Dispose();
        Close();
    }

    private async void OnSessionPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SessionService.Principal))
        {
            if (_services.Session.Principal is null)
            {
                _ = RootGrid.DispatcherQueue.TryEnqueue(async () => await ClearSearchAsync().ConfigureAwait(true));
            }
            _ = RootGrid.DispatcherQueue.TryEnqueue(RebuildNavigation);
            return;
        }

        if (e.PropertyName != nameof(SessionService.SelectedInstance) || _services.Session.SelectedInstance is not InstanceDto instance)
        {
            return;
        }

        bool saved = await TryUpdateSettingsAsync(
            current => current with { RecentInstanceIds = [instance.Id, .. current.RecentInstanceIds.Where(id => id != instance.Id).Take(9)] },
            "settings.recent-instance-save-failed").ConfigureAwait(true);
        if (saved)
        {
            UpdateTrayInstances();
        }
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "Local preference persistence failures are logged without terminating the WinUI event loop.")]
    private async Task<bool> TryUpdateSettingsAsync(Func<DesktopSettings, DesktopSettings> update, string eventName)
    {
        try
        {
            await _services.Settings.UpdateAsync(update, CancellationToken.None).ConfigureAwait(true);
            return true;
        }
        catch (Exception exception)
        {
            await _services.Log.WriteAsync("warning", eventName, exception.GetType().Name).ConfigureAwait(true);
            return false;
        }
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "Realtime tray refresh failures are recorded without terminating the Desktop event loop.")]
    private async void RefreshTrayInstancesAsync()
    {
        try
        {
            await _services.Session.RefreshSelectionsAsync(CancellationToken.None).ConfigureAwait(true);
            UpdateTrayInstances();
        }
        catch (Exception exception)
        {
            await _services.Log.WriteAsync("warning", "tray.instances-refresh-failed", exception.GetType().Name).ConfigureAwait(true);
        }
    }

    private void UpdateTrayInstances()
    {
        _services.Tray.UpdateInstances(_services.Session.Instances, _services.Settings.Current.RecentInstanceIds);
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "The window-close boundary must not prevent process shutdown if settings persistence fails.")]
    private async void OnClosed(object sender, WindowEventArgs args)
    {
        try
        {
            await _services.WindowState.SaveAsync(this, CancellationToken.None).ConfigureAwait(true);
        }
        catch (Exception exception)
        {
            await _services.Log.WriteAsync("warning", "window.state-save-failed", exception.GetType().Name).ConfigureAwait(true);
        }

        Dispose();
    }

}
