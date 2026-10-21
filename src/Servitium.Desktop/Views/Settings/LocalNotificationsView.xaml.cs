using Microsoft.UI.Xaml.Controls;
using Servitium.Desktop.ViewModels.Settings;
namespace Servitium.Desktop.Views.Settings;

internal sealed partial class LocalNotificationsView : UserControl
{
    public LocalPreferencesViewModel ViewModel { get; }
    internal LocalNotificationsView(LocalPreferencesViewModel model) { ViewModel = model; InitializeComponent(); }
}
