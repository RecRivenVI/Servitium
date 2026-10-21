using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;

namespace Servitium.Desktop.Services;

internal static class WindowThemeService
{
    internal static void Apply(FrameworkElement root, AppWindow window, string theme)
    {
        root.RequestedTheme = theme switch { "light" => ElementTheme.Light, "dark" => ElementTheme.Dark, _ => ElementTheme.Default };
        window.TitleBar.PreferredTheme = theme switch { "light" => TitleBarTheme.Light, "dark" => TitleBarTheme.Dark, _ => TitleBarTheme.UseDefaultAppMode };
        if (AppWindowTitleBar.IsCustomizationSupported())
        {
            window.TitleBar.ButtonBackgroundColor = Colors.Transparent;
            window.TitleBar.ButtonInactiveBackgroundColor = Colors.Transparent;
        }
    }
}
