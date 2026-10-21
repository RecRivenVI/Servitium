using Microsoft.UI.Xaml.Controls;
using Servitium.Desktop.ViewModels.Settings;

namespace Servitium.Desktop.Views.Settings;

internal sealed partial class ServerGeneralView : UserControl
{
    public ServerSettingsViewModel ViewModel { get; }
    internal ServerGeneralView(ServerSettingsViewModel model) { ViewModel = model; InitializeComponent(); }

    private void OnChoiceChanged(object sender, SelectionChangedEventArgs args)
    {
        if (sender is ComboBox { Tag: ServerSettingValue tag, SelectedValue: string selectedValue })
        {
            tag.Value = selectedValue;
        }
    }
}
