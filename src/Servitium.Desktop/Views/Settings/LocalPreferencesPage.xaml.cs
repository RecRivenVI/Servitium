using System;
using System.Threading.Tasks;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Servitium.Desktop.Shell;
using Servitium.Desktop.ViewModels.Settings;
namespace Servitium.Desktop.Views.Settings;

internal sealed partial class LocalPreferencesPage : UserControl, INavigationGuard, IDisposable
{
    public LocalPreferencesViewModel ViewModel { get; }
    internal LocalPreferencesPage(LocalPreferencesViewModel model, string category)
    { ViewModel = model; InitializeComponent(); CategoryContent.Content = category switch { "terminal" => new LocalTerminalView(model), "notifications" => new LocalNotificationsView(model), _ => new LocalAppearanceView(model) }; Unloaded += (_, _) => Dispose(); }

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
