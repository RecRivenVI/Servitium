using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

using Servitium.Client.Realtime;
using Servitium.Contracts.Instances;
using Servitium.Desktop.AppModel;
using Servitium.Desktop.Models;

namespace Servitium.Desktop.Services;

/// <summary>管理最多八个互相独立的终端窗口及其本地几何状态。</summary>
internal sealed partial class TerminalWindowService(AppServices services) : IDisposable
{
    private const int MaximumWindows = 8;
    private readonly List<TerminalWindow> _windows = [];
    private bool _disposed;

    internal async Task OpenAsync(InstanceDto instance)
    {
        ArgumentNullException.ThrowIfNull(instance);
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_windows.Count >= MaximumWindows)
        {
            await services.Interaction.ShowAsync("action.open-terminal", services.Localization.Get("terminal.window-limit")).ConfigureAwait(true);
            return;
        }

        string nodeName = services.Session.Nodes.FirstOrDefault(node => node.Id == instance.NodeId)?.Name ?? instance.NodeId.ToString("D");
        TerminalWindow window = new(services, instance, nodeName);
        window.Closed += OnWindowClosed;
        _windows.Add(window);
        WindowGeometry? geometry = services.Settings.Current.TerminalWindows?.GetValueOrDefault(instance.Id);
        WindowStateService.Restore(window, geometry, 960, 640);
        window.Activate();
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "A local geometry persistence failure must not keep a terminal window alive or crash the Desktop event loop.")]
    private async void OnWindowClosed(object sender, WindowEventArgs args)
    {
        if (sender is not TerminalWindow window)
        {
            return;
        }

        window.Closed -= OnWindowClosed;
        _ = _windows.Remove(window);
        try
        {
            WindowGeometry geometry = window.LastGeometry;
            await services.Settings.UpdateAsync(current =>
            {
                Dictionary<Guid, WindowGeometry> values = current.TerminalWindows is null
                    ? []
                    : new Dictionary<Guid, WindowGeometry>(current.TerminalWindows);
                values[window.InstanceId] = geometry;
                return current with { TerminalWindows = values };
            }, CancellationToken.None).ConfigureAwait(true);
        }
        catch (Exception exception)
        {
            await services.Log.WriteAsync("warning", "terminal.window-state-save-failed", exception.GetType().Name).ConfigureAwait(true);
        }
        finally
        {
            window.Dispose();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        foreach (TerminalWindow window in _windows.ToArray())
        {
            window.Closed -= OnWindowClosed;
            window.Close();
            window.Dispose();
        }

        _windows.Clear();
    }
}

internal sealed partial class TerminalWindow : Window, IDisposable
{
    private readonly RestrictedWebViewHost _host;
    private readonly AppWindow _appWindow;
    private readonly AppServices _services;
    private bool _disposed;

    internal TerminalWindow(AppServices services, InstanceDto instance, string nodeName)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(instance);
        ArgumentException.ThrowIfNullOrWhiteSpace(nodeName);
        _services = services;
        InstanceId = instance.Id;
        Title = services.Localization.Format("terminal.window-title", instance.Name, nodeName);
        TerminalConnection connection = services.Session.Client.CreateTerminalConnection();
        _host = new RestrictedWebViewHost(services, "terminal", instance, connection);
        Content = new Grid
        {
            Padding = new Thickness(12),
            Background = Application.Current.Resources["ApplicationPageBackgroundThemeBrush"] as Microsoft.UI.Xaml.Media.Brush,
            Children = { _host },
        };
        _appWindow = WindowStateService.GetAppWindow(this);
        if (_appWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.SetBorderAndTitleBar(true, true);
            presenter.PreferredMinimumWidth = 640;
            presenter.PreferredMinimumHeight = 420;
        }

        WindowThemeService.Apply((FrameworkElement)Content, _appWindow, services.Settings.Current.Theme);
        services.Settings.Changed += OnSettingsChanged;
    }

    private void OnSettingsChanged(object? sender, EventArgs args)
    {
        _ = DispatcherQueue.TryEnqueue(() => { if (!_disposed && Content is FrameworkElement root) { WindowThemeService.Apply(root, _appWindow, _services.Settings.Current.Theme); } });
    }

    internal Guid InstanceId { get; }

    internal WindowGeometry LastGeometry => WindowStateService.Capture(this);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _services.Settings.Changed -= OnSettingsChanged;
        _host.Dispose();
    }
}
