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

internal sealed partial class InstanceSettingsPage : UserControl, INavigationGuard, IDisposable
{
    private readonly AppServices _services;
    private readonly IPageNavigator _navigator;
    private bool _disposed;
    public InstanceSettingsViewModel ViewModel { get; }
    protected override AutomationPeer OnCreateAutomationPeer() { return new FrameworkElementAutomationPeer(this); }
    internal InstanceSettingsPage(AppServices services, IPageNavigator navigator)
    {
        _services = services; _navigator = navigator;
        ViewModel = new(services.Localization, services.Session.Client, services.Session.SelectedInstance ?? throw new InvalidOperationException("An instance must be selected."),
            services.Session.Principal?.User.Role is "Owner" or "Administrator", services.Interaction.ConfirmAsync, services.Interaction.PromptAsync, services.Session.SelectedNode?.Status == "Online");
        InitializeComponent(); Loaded += OnLoaded; Unloaded += OnUnloaded;
    }
    private async void OnLoaded(object sender, RoutedEventArgs args)
    {
        ViewModel.Removed += OnRemoved; _services.Session.Client.Events.EventReceived += OnRealtimeEvent;
        await ViewModel.LoadAsync().ConfigureAwait(true);
    }
    private void OnUnloaded(object sender, RoutedEventArgs args) { Dispose(); }
    public Task<bool> CanNavigateAwayAsync() { return ViewModel.CanLeaveAsync(); }
    private void OnRemoved(object? sender, EventArgs args) { _services.Session.SelectedInstance = null; _navigator.Navigate("PG-010", false); }

    private void OnRealtimeEvent(object? sender, RealtimeEventArgs args)
    {
        _ = DispatcherQueue.TryEnqueue(async () => { if (!_disposed && ViewModel.ApplyRuntimeEvent(args.Value)) { await ViewModel.LoadAsync().ConfigureAwait(true); } });
    }
    public void Dispose()
    {
        if (_disposed) { return; }
        _disposed = true;
        _services.Session.Client.Events.EventReceived -= OnRealtimeEvent; ViewModel.Removed -= OnRemoved; ViewModel.Dispose(); GC.SuppressFinalize(this);
    }
    private void OnManageJava(object sender, RoutedEventArgs args) { _navigator.Navigate("PG-022"); }
    private void OnRemoveEnvironment(object sender, RoutedEventArgs args) { if (sender is Button { DataContext: LaunchEnvironmentVariable variable }) { ViewModel.Launch.RemoveEnvironmentCommand.Execute(variable); } }
    private void OnRemoveShortcut(object sender, RoutedEventArgs args) { if (sender is Button { DataContext: ShortcutCommandEditor shortcut }) { ViewModel.RemoveShortcutCommand.Execute(shortcut); } }
    private void OnSaveInvoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args) { if (ViewModel.CanSave) { ViewModel.SaveCommand.Execute(null); args.Handled = true; } }
}
