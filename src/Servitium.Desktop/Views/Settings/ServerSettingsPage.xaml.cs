using System;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Servitium.Desktop.Shell;
using Servitium.Desktop.ViewModels.Settings;
namespace Servitium.Desktop.Views.Settings;

internal sealed partial class ServerSettingsPage : UserControl, INavigationGuard, IDisposable
{
    public ServerSettingsViewModel ViewModel { get; }
    internal ServerSettingsPage(ServerSettingsViewModel model, string category)
    { ViewModel = model; InitializeComponent(); CategoryContent.Content = category switch { "server-security" => new ServerSecurityView(model), "server-identity" => new ServerIdentityView(model), "server-about" => new ServerAboutView(model), _ => new ServerGeneralView(model) }; Loaded += OnLoaded; Unloaded += (_, _) => Dispose(); }
    private async void OnLoaded(object sender, RoutedEventArgs args) { await ViewModel.LoadAsync().ConfigureAwait(true); }
    public Task<bool> CanNavigateAwayAsync()
    {
        return ViewModel.CanLeaveAsync();
    }

    public void Dispose() { ViewModel.Dispose(); GC.SuppressFinalize(this); }

    private void OnSaveInvoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (ViewModel.CanSave)
        {
            ViewModel.SaveCommand.Execute(null);
            args.Handled = true;
        }
    }
}
