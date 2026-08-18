using System;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;

using Servitium.Desktop.Models;

using Windows.Graphics;

namespace Servitium.Desktop.Services;

internal sealed class WindowStateService(DesktopSettingsService settings)
{
    internal static AppWindow GetAppWindow(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);
        nint handle = WinRT.Interop.WindowNative.GetWindowHandle(window);
        return AppWindow.GetFromWindowId(Microsoft.UI.Win32Interop.GetWindowIdFromWindow(handle));
    }

    internal void Restore(Window window)
    {
        WindowGeometry? geometry = settings.Current.MainWindow;
        Restore(window, geometry, 1280, 800);
    }

    internal static void Restore(Window window, WindowGeometry? geometry, int defaultWidth, int defaultHeight)
    {
        AppWindow appWindow = GetAppWindow(window);
        if (geometry is null)
        {
            appWindow.Resize(new SizeInt32(defaultWidth, defaultHeight));
            return;
        }

        RectInt32 requested = new(geometry.X, geometry.Y, Math.Max(800, geometry.Width), Math.Max(560, geometry.Height));
        var display = DisplayArea.GetFromRect(requested, DisplayAreaFallback.Primary);
        RectInt32 work = display.WorkArea;
        int width = Math.Min(requested.Width, work.Width);
        int height = Math.Min(requested.Height, work.Height);
        int x = Math.Clamp(requested.X, work.X, work.X + work.Width - width);
        int y = Math.Clamp(requested.Y, work.Y, work.Y + work.Height - height);
        appWindow.MoveAndResize(new RectInt32(x, y, width, height));
        if (geometry.Maximized && appWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.Maximize();
        }
    }

    internal Task SaveAsync(Window window, CancellationToken cancellationToken)
    {
        WindowGeometry geometry = Capture(window);
        return settings.UpdateAsync(current => current with { MainWindow = geometry }, cancellationToken);
    }

    internal static WindowGeometry Capture(Window window)
    {
        AppWindow appWindow = GetAppWindow(window);
        bool maximized = appWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Maximized };
        return new WindowGeometry(
            appWindow.Position.X,
            appWindow.Position.Y,
            appWindow.Size.Width,
            appWindow.Size.Height,
            maximized);
    }

    internal static string Breakpoint(double width)
    {
        return width switch
        {
            < 640 => "compact",
            < 1200 => "medium",
            _ => "expanded",
        };
    }
}
