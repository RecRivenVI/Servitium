using Microsoft.UI.Xaml.Controls;
using Servitium.Desktop.ViewModels.Settings;
namespace Servitium.Desktop.Views.Settings;

internal sealed partial class LocalTerminalView : UserControl
{
    public LocalPreferencesViewModel ViewModel { get; }
    internal LocalTerminalView(LocalPreferencesViewModel model) { ViewModel = model; InitializeComponent(); }
}
