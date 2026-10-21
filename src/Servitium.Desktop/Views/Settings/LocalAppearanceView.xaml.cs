using Microsoft.UI.Xaml.Controls;
using Servitium.Desktop.ViewModels.Settings;
namespace Servitium.Desktop.Views.Settings;

internal sealed partial class LocalAppearanceView : UserControl
{
    public LocalPreferencesViewModel ViewModel { get; }
    internal LocalAppearanceView(LocalPreferencesViewModel model) { ViewModel = model; InitializeComponent(); }
}
