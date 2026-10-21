using Microsoft.UI.Xaml.Controls;
using Servitium.Desktop.ViewModels.Settings;

namespace Servitium.Desktop.Views.Settings;

internal sealed partial class ServerIdentityView : UserControl
{
    public ServerSettingsViewModel ViewModel { get; }
    internal ServerIdentityView(ServerSettingsViewModel model) { ViewModel = model; InitializeComponent(); }
}
