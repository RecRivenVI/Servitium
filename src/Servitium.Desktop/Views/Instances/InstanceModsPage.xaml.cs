using System;
using System.Threading.Tasks;

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

using Servitium.Client.Realtime;
using Servitium.Desktop.AppModel;
using Servitium.Desktop.Shell;
using Servitium.Desktop.ViewModels.Instances;

namespace Servitium.Desktop.Views.Instances;

internal sealed partial class InstanceModsPage : UserControl, INavigationGuard, IDisposable
{
    private readonly AppServices _services;
    private bool _loaded;
    private bool _disposed;
    public InstanceModsViewModel ViewModel { get; }
    protected override AutomationPeer OnCreateAutomationPeer() { return new FrameworkElementAutomationPeer(this); }
    internal InstanceModsPage(AppServices services, IPageNavigator navigator)
    {
        _services = services;
        ViewModel = new(services.Localization, services.Session.Client, services.Session.SelectedInstance ?? throw new InvalidOperationException("An instance must be selected."),
            () => services.Interaction.PickOpenFileAsync(".jar"), services.Interaction.ConfirmAsync,
            path => { services.Session.PendingFilePath = path; navigator.Navigate("PG-013"); }, services.Session.SelectedNode?.Status == "Online");
        InitializeComponent(); Loaded += OnLoaded; Unloaded += OnUnloaded;
    }
    private async void OnLoaded(object sender, RoutedEventArgs args) { _loaded = true; _services.Session.Client.Events.EventReceived += OnRealtimeEvent; await ViewModel.LoadAsync().ConfigureAwait(true); }
    private void OnUnloaded(object sender, RoutedEventArgs args) { Dispose(); }
    public Task<bool> CanNavigateAwayAsync() { return ViewModel.CanLeaveAsync(); }

    private void OnRealtimeEvent(object? sender, RealtimeEventArgs args) { _ = DispatcherQueue.TryEnqueue(() => { if (!_disposed) { ViewModel.ApplyRuntimeEvent(args.Value); } }); }
    public void Dispose()
    {
        if (_disposed) { return; }
        _disposed = true;
        _services.Session.Client.Events.EventReceived -= OnRealtimeEvent; ViewModel.Dispose(); GC.SuppressFinalize(this);
    }
    private async void OnKindChanged(object sender, SelectionChangedEventArgs args)
    {
        if (!_loaded || _disposed) { return; }
        await ViewModel.ChangeKindAsync(KindSelector.SelectedIndex).ConfigureAwait(true); KindSelector.SelectedIndex = ViewModel.KindIndex;
    }
    private async void OnModToggled(object sender, RoutedEventArgs args)
    {
        if (!_loaded || _disposed || sender is not ToggleSwitch { DataContext: ModRow row } toggle) { return; }
        await ViewModel.SetEnabledAsync(row, toggle.IsOn).ConfigureAwait(true);
    }
    private async void OnFindConfiguration(object sender, RoutedEventArgs args) { await ViewModel.FindConfigurationCommand.ExecuteAsync(null).ConfigureAwait(true); }
    private void OnOpenConfiguration(object sender, RoutedEventArgs args) { ConfigurationFlyout.Hide(); if (ViewModel.CanOpenConfiguration) { ViewModel.OpenConfigurationCommand.Execute(null); } }
    private void OnRefreshInvoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args) { if (ViewModel.CanRead) { ViewModel.RefreshCommand.Execute(null); args.Handled = true; } }
}
