using System;
using System.Linq;
using System.Threading.Tasks;

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

using Servitium.Client.Realtime;
using Servitium.Contracts.Events;
using Servitium.Desktop.AppModel;
using Servitium.Desktop.Shell;
using Servitium.Desktop.Models;
using Servitium.Desktop.ViewModels.Instances;

namespace Servitium.Desktop.Views.Instances;

internal sealed partial class InstanceWorkspacePage : Page, INavigationGuard
{
    private readonly AppServices _services;
    private readonly IPageNavigator _navigator;
    private string _tab;
    private bool _synchronizing;
    private Guid? _visibleInstance;
    private readonly bool _showCreation;
    private bool _creationOpen;
    private bool _refreshing;
    private bool _refreshRequested;
    private bool _loadedOnce;
    public InstanceWorkspaceViewModel ViewModel { get; }

    internal InstanceWorkspacePage(AppServices services, IPageNavigator navigator, string tab = "PG-012", bool showCreation = false)
    {
        _services = services; _navigator = navigator; _tab = tab; _showCreation = showCreation;
        ViewModel = new(services.Localization, services.Session.Client, services.Session.Principal?.User.Role is "Owner" or "Administrator",
            services.Interaction.ConfirmAsync, value => services.Session.SelectedInstance = value);
        InitializeComponent();
        Loaded += OnLoaded; Unloaded += OnUnloaded;
        ViewModel.PropertyChanged += OnViewModelChanged;
    }

    private async void OnLoaded(object sender, RoutedEventArgs args)
    {
        _services.Session.Client.Events.EventReceived += OnRealtimeEvent;
        await RefreshAsync().ConfigureAwait(true);
        if (_showCreation) { await ShowCreationAsync().ConfigureAwait(true); }
    }

    private void OnUnloaded(object sender, RoutedEventArgs args)
    {
        _services.Session.Client.Events.EventReceived -= OnRealtimeEvent;
        ViewModel.PropertyChanged -= OnViewModelChanged;
        ViewModel.Dispose();
    }

    private async Task RefreshAsync()
    {
        _refreshRequested = true;
        if (_creationOpen || _refreshing || ViewModel.IsBusy) { return; }
        _refreshing = true;
        _synchronizing = true;
        try
        {
            do
            {
                _refreshRequested = false;
                var selectedIds = InstanceList.SelectedItems.OfType<InstanceRow>().Select(row => row.Value.Id).ToHashSet();
                Guid? previousFocus = _visibleInstance;
                await ViewModel.LoadAsync(_services.Session.SelectedInstance?.Id).ConfigureAwait(true);
                if (!_loadedOnce || previousFocus != ViewModel.Selected?.Value.Id)
                {
                    selectedIds.Clear();
                    if (ViewModel.Selected is InstanceRow focused) { _ = selectedIds.Add(focused.Value.Id); }
                }
                InstanceList.SelectedItems.Clear();
                foreach (InstanceRow row in ViewModel.Groups.SelectMany(group => group.Items).Where(row => selectedIds.Contains(row.Value.Id)))
                {
                    InstanceList.SelectedItems.Add(row);
                }
                ShowTab();
                if (!_loadedOnce && ViewModel.HasSelection && WorkspaceSplit.DisplayMode == SplitViewDisplayMode.Overlay) { WorkspaceSplit.IsPaneOpen = false; }
                _loadedOnce = true;
            } while (_refreshRequested && IsLoaded && !_creationOpen);
        }
        finally { _synchronizing = false; _refreshing = false; }
    }

    private void OnViewModelChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(ViewModel.IsBusy) && !ViewModel.IsBusy && _refreshRequested && !_refreshing && !_creationOpen)
        {
            _ = DispatcherQueue.TryEnqueue(async () => await RefreshAsync().ConfigureAwait(true));
        }
    }

    private void OnRealtimeEvent(object? sender, RealtimeEventArgs args)
    {
        if (args.Value.Type is EventTypes.InstanceCreated or EventTypes.InstanceUpdated or EventTypes.InstanceUnregistered or
            EventTypes.InstanceStatusChanged or EventTypes.InstanceCrashed or EventTypes.NodeStatusChanged)
        {
            _ = DispatcherQueue.TryEnqueue(async () => await RefreshAsync().ConfigureAwait(true));
        }
    }

    private async void OnInstanceSelectionChanged(object sender, SelectionChangedEventArgs args)
    {
        if (_synchronizing || ViewModel.IsBusy || InstanceList.SelectedItems.LastOrDefault() is not InstanceRow row || row.Value.Id == _visibleInstance) { return; }
        if (!await CanNavigateAwayAsync().ConfigureAwait(true))
        {
            _synchronizing = true; InstanceList.SelectedItem = ViewModel.Selected; _synchronizing = false; return;
        }
        await ViewModel.SelectAsync(row).ConfigureAwait(true); ShowTab();
        if (WorkspaceSplit.DisplayMode == SplitViewDisplayMode.Overlay) { WorkspaceSplit.IsPaneOpen = false; }
    }

    private async void OnTabSelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        if (_synchronizing || sender.SelectedItem?.Tag is not string pageId || pageId == _tab) { return; }
        if (!DesktopPageAuthorization.CanAccess(UiContractCatalog.Get(pageId).Permission, _services.Session.Principal, ViewModel.Selected?.Value.Id))
        {
            ViewModel.ShowPermissionDenied();
            _synchronizing = true; sender.SelectedItem = sender.Items.First(item => (item.Tag as string) == _tab); _synchronizing = false; return;
        }
        if (!await CanNavigateAwayAsync().ConfigureAwait(true))
        {
            _synchronizing = true; sender.SelectedItem = sender.Items.First(item => (item.Tag as string) == _tab); _synchronizing = false; return;
        }
        _tab = pageId; _visibleInstance = null; ShowTab();
    }

    private void ShowTab()
    {
        if (ViewModel.Selected is null) { TabHost.Content = null; _visibleInstance = null; return; }
        if (_visibleInstance == ViewModel.Selected.Value.Id) { return; }
        _visibleInstance = ViewModel.Selected.Value.Id;
        _services.Session.SelectedNode = ViewModel.SelectedNode;
        WorkspaceTabs.SelectedItem = WorkspaceTabs.Items.First(item => (item.Tag as string) == _tab);
        TabHost.Content = ProductPageFactory.CreateInstanceTab(_tab, _services, _navigator);
    }

    public Task<bool> CanNavigateAwayAsync()
    {
        return _creationOpen
            ? Task.FromResult(false)
            : TabHost.Content is INavigationGuard guard ? guard.CanNavigateAwayAsync() : Task.FromResult(true);
    }

    private async void OnRefreshClick(object sender, RoutedEventArgs args) { await RefreshAsync().ConfigureAwait(true); }
    private async void OnCreateClick(object sender, RoutedEventArgs args) { await ShowCreationAsync().ConfigureAwait(true); }
    private async Task ShowCreationAsync()
    {
        if (!ViewModel.IsAdministrator) { return; }
        if (!await CanNavigateAwayAsync().ConfigureAwait(true)) { return; }
        InstanceCreationDialog dialog = new(_services) { XamlRoot = XamlRoot };
        _creationOpen = true;
        try { _ = await dialog.ShowAsync(); }
        finally { _creationOpen = false; }
        await RefreshAsync().ConfigureAwait(true);
    }
    private void OnShowInstancesClick(object sender, RoutedEventArgs args) { WorkspaceSplit.IsPaneOpen = !WorkspaceSplit.IsPaneOpen; }
    private void OnWorkingDirectoryClick(object sender, RoutedEventArgs args)
    {
        _tab = "PG-013"; _visibleInstance = null; ShowTab();
    }
    private async void OnExportLogClick(object sender, RoutedEventArgs args)
    {
        await ViewModel.ExportLogAsync(() => _services.Interaction.PickSaveFileAsync("terminal.log", ".log")).ConfigureAwait(true);
    }
    private void OnWorkspaceSizeChanged(object sender, SizeChangedEventArgs args)
    {
        bool compact = Services.WindowStateService.Breakpoint(args.NewSize.Width) != "expanded";
        WorkspaceSplit.DisplayMode = compact ? SplitViewDisplayMode.Overlay : SplitViewDisplayMode.Inline;
        WorkspaceSplit.OpenPaneLength = args.NewSize.Width * (compact ? 0.8 : 0.32);
        WorkspaceSplit.IsPaneOpen = !compact;
        InstancesPaneButton.Visibility = compact ? Visibility.Visible : Visibility.Collapsed;
    }
}
