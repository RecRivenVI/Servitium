using CommunityToolkit.Mvvm.ComponentModel;
using Servitium.Desktop.Services;
namespace Servitium.Desktop.ViewModels.Settings;

internal sealed partial class SettingsNavigationViewModel(LocalizationService localization, bool administrator, string connectionName) : ObservableObject
{
    public string Text(string key)
    {
        return localization.Get(key);
    }

    public bool ShowServer => administrator;
    public string ConnectionName { get; set { if (SetProperty(ref field, value)) { OnPropertyChanged(nameof(ServerGroup)); } } } = connectionName;
    public string ServerGroup => localization.Format("desktop.settings-server-group", ConnectionName);
    internal bool CanOpen(string category)
    {
        return category is "appearance" or "terminal" or "notifications" or "connections" or "account" || administrator && category is "server-general" or "server-security" or "server-identity" or "server-about";
    }
}
