using System;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Servitium.Desktop.AppModel;
using Servitium.Desktop.Shell;
using Servitium.Desktop.ViewModels.Settings;
namespace Servitium.Desktop.Views.Settings;

internal sealed partial class AccountSettingsPage : UserControl, INavigationGuard, IDisposable
{
    private readonly AppServices _services;
    private readonly IPageNavigator _navigator;
    public AccountSettingsViewModel ViewModel { get; }
    internal AccountSettingsPage(AppServices services, IPageNavigator navigator)
    {
        _services = services; _navigator = navigator;
        ViewModel = new(services.Localization, services.Session.Client, services.Interaction.ConfirmAsync, services.Interaction.PromptAsync, services.Interaction.ShowAsync, services.Session.BindOidcAsync);
        InitializeComponent(); Loaded += OnLoaded; Unloaded += (_, _) => Dispose(); ViewModel.SignedOut += OnSignedOut;
    }
    private async void OnLoaded(object sender, RoutedEventArgs args) { await ViewModel.LoadAsync().ConfigureAwait(true); }
    private void OnSignedOut(object? sender, EventArgs args) { _services.Session.ClearAuthenticationState(); _navigator.Navigate("PG-002", false); }
    public Task<bool> CanNavigateAwayAsync()
    {
        return ViewModel.CanLeaveAsync();
    }

    public void Dispose() { ViewModel.SignedOut -= OnSignedOut; ViewModel.Dispose(); GC.SuppressFinalize(this); }
}
