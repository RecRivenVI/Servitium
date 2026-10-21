using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

using Servitium.Desktop.Services;

namespace Servitium.Desktop.Views.Authentication;

internal static class AuthenticationFormLayout
{
    internal static void Arrange(ColumnDefinition leading, ColumnDefinition trailing, double width)
    {
        GridLength spacing = WindowStateService.Breakpoint(width) == "compact" ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
        leading.Width = spacing;
        trailing.Width = spacing;
    }
}
