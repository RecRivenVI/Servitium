using Microsoft.UI.Xaml.Controls;
using Servitium.Desktop.ViewModels.Settings;

namespace Servitium.Desktop.Views.Settings;

internal sealed partial class ServerAboutView : UserControl
{
    public ServerSettingsViewModel ViewModel { get; }
    internal ServerAboutView(ServerSettingsViewModel model) { ViewModel = model; InitializeComponent(); }
}
