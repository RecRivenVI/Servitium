using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.Windows.AppLifecycle;
using Microsoft.Windows.AppNotifications;

using Servitium.Client.Realtime;
using Servitium.Desktop.AppModel;
using Servitium.Desktop.Models;
using Servitium.Desktop.Services;
using Servitium.Desktop.Shell;
using Servitium.Desktop.ViewModels;
using Servitium.Desktop.Views;

using Windows.ApplicationModel.Activation;
using Windows.System;

namespace Servitium.Desktop;

internal sealed partial class MainWindow : Window, IPageNavigator, IServerConnectionNavigator, IDisposable
{
    private readonly AppServices _services;
    private readonly AppWindow _appWindow;
    private readonly Stack<NavigationEntry> _backStack = new();
    private NavigationEntry? _currentEntry;
    private string? _currentPageId;
    private bool _disposed;
    private bool _exitRequested;
    private bool _closePromptPending;
    private bool _navigationPending;
    private EventsConnection? _events;
    private CancellationTokenSource? _search;
    private CancellationTokenSource _connectionLifetime = new();
    private bool _connecting;
    private bool _synchronizingNavigation;

    public ShellViewModel ViewModel { get; }

    internal MainWindow(AppServices services, Uri serverAddress)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(serverAddress);
        _services = services;
        ViewModel = new(services.Localization);
        ViewModel.SetConnection(services.Profiles.Active.Name, serverAddress, "shell.status-disconnected");
        InitializeComponent();
        services.AttachWindow(this);
        _appWindow = WindowStateService.GetAppWindow(this);
        Title = services.Localization.Get("app.window-title");
        AppTitleBar.Title = string.Empty;
        ExtendsContentIntoTitleBar = true;
        SystemBackdrop = new MicaBackdrop();
        SetTitleBar(AppTitleBar);
        ApplyTheme(services.Settings.Current.Theme);
        _appWindow.Closing += OnAppWindowClosing;
        services.Tray.ShowRequested += OnTrayShowRequested;
        services.Tray.PauseRequested += OnTrayPauseRequested;
        services.Tray.InstanceActionRequested += OnTrayInstanceActionRequested;
        services.Tray.AboutRequested += OnTrayAboutRequested;
        services.Tray.ExitRequested += OnTrayExitRequested;
        services.Session.PropertyChanged += OnSessionPropertyChanged;
        services.Settings.Changed += OnLocalSettingsChanged;
        RootGrid.SizeChanged += OnRootSizeChanged;
        AppTitleBar.SizeChanged += OnTitleBarSizeChanged;
        RootGrid.Loaded += OnRootLoaded;
        Closed += OnClosed;
        RootGrid.KeyboardAccelerators.Add(CreateAccelerator(VirtualKey.Left, VirtualKeyModifiers.Menu, GoBack));
        RootGrid.KeyboardAccelerators.Add(CreateAccelerator(VirtualKey.F5, VirtualKeyModifiers.None, RefreshCurrentPage));
        RootGrid.KeyboardAccelerators.Add(CreateAccelerator(VirtualKey.K, VirtualKeyModifiers.Control, FocusGlobalSearch));
    }

    internal async Task RunConnectionSelfTestAsync()
    {
        await _services.Session.ConnectAsync(_services.Profiles.Active.Address, CancellationToken.None).ConfigureAwait(true);
        _ = await _services.Session.GetAuthenticationStateAsync(CancellationToken.None).ConfigureAwait(true);
    }

    internal void ApplyCurrentResponsiveLayout()
    {
        ApplyResponsiveLayout(RootGrid.ActualWidth, RootGrid.ActualHeight);
    }

    internal Task InitializeSessionAsync()
    {
        return ConnectAsync(_services.Profiles.Active.Address);
    }

    internal void Navigate(DeepLinkTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        _ = NavigateAsync(target.PageId, true, () => ApplyResourceSelection(target));
    }

    internal void HandleActivation(AppActivationArguments arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        _ = RootGrid.DispatcherQueue.TryEnqueue(() =>
        {
            OnTrayShowRequested(this, EventArgs.Empty);
            if (arguments.Kind == ExtendedActivationKind.Protocol && arguments.Data is ProtocolActivatedEventArgs protocol &&
                DeepLinkService.TryParse(protocol.Uri.AbsoluteUri, out DeepLinkTarget? target) && target is not null)
            {
                Navigate(target);
            }
            else if (arguments.Kind == ExtendedActivationKind.AppNotification &&
                arguments.Data is AppNotificationActivatedEventArgs notification && notification.Arguments.TryGetValue("page", out string? pageId))
            {
                Guid? resource = notification.Arguments.TryGetValue("resource", out string? value) && Guid.TryParse(value, out Guid id) ? id : null;
                Navigate(new DeepLinkTarget(pageId, resource, new Dictionary<string, string>(notification.Arguments, StringComparer.Ordinal)));
            }
        });
    }

    public void Navigate(string pageId, bool addToBackStack = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pageId);
        _ = NavigateAsync(pageId, addToBackStack);
    }

    private async Task NavigateAsync(string pageId, bool addToBackStack, Action? applySelection = null)
    {
        if (_navigationPending) { return; }
        _navigationPending = true;
        try
        {
            if (PageHost.Content is INavigationGuard guard && !await guard.CanNavigateAwayAsync().ConfigureAwait(true)) { return; }
            applySelection?.Invoke();
            NavigateCore(pageId, addToBackStack);
        }
        finally { _navigationPending = false; }
    }

    private void NavigateCore(string pageId, bool addToBackStack)
    {
        pageId = pageId == "PG-040" ? InstanceHome() : pageId;
        UiPageContract contract = UiContractCatalog.Get(pageId);
        if (!DesktopPageAuthorization.CanAccess(contract.Permission, _services.Session.Principal, _services.Session.SelectedInstance?.Id))
        {
            ShellStatus.Message = _services.Localization.Get("desktop.permission-error");
            ShellStatus.Severity = InfoBarSeverity.Warning;
            ShellStatus.IsOpen = true;
            pageId = InstanceHome();
        }

        var next = NavigationEntry.Capture(pageId, _services.Session);
        if (addToBackStack && _currentEntry is not null && _currentEntry != next) { _backStack.Push(_currentEntry); }
        _currentPageId = pageId;
        _currentEntry = next;
        PageHost.Content = ProductPageFactory.Create(pageId, _services, this);
        AppTitleBar.IsBackButtonEnabled = _backStack.Count > 0;
        AppTitleBar.IsBackButtonVisible = _services.Session.Principal is not null;
        _synchronizingNavigation = true;
        try
        {
            string navigationPage = pageId switch
            {
                "PG-010" or "PG-011" or "PG-012" or "PG-013" or "PG-014" or "PG-015" or "PG-016" or "PG-017" or "PG-018" or "PG-019" => "instances",
                "PG-021" or "PG-022" => "PG-020",
                "PG-061" or "PG-062" or "PG-063" => "PG-060",
                "PG-031" => "PG-030",
                "PG-032" or "PG-041" => "settings",
                "PG-042" or "PG-043" => "PG-044",
                _ => pageId,
            };
            Navigation.SelectedItem = Navigation.MenuItems.Concat(Navigation.FooterMenuItems).OfType<NavigationViewItem>().FirstOrDefault(item => (item.Tag as string) == navigationPage);
        }
        finally { _synchronizingNavigation = false; }
    }

    private void TitleBar_BackRequested(TitleBar sender, object args) { GoBack(); }

    private void GoBack() { _ = GoBackAsync(); }

    private async Task GoBackAsync()
    {
        if (_navigationPending || !_backStack.TryPeek(out NavigationEntry? entry)) { return; }
        if (PageHost.Content is INavigationGuard guard && !await guard.CanNavigateAwayAsync().ConfigureAwait(true)) { return; }
        _ = _backStack.Pop();
        entry.Restore(_services.Session);
        NavigateCore(entry.PageId, false);
    }

    private void RefreshCurrentPage()
    {
        if (_currentPageId is not null) { Navigate(_currentPageId, false); }
        _ = RefreshShellAsync();
    }

    private void Navigation_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (_synchronizingNavigation || args.SelectedItem is not NavigationViewItem { Tag: string target }) { return; }
        string pageId = target switch
        {
            "instances" => InstanceHome(),
            "settings" => _services.Session.Principal?.User.Role is "Owner" or "Administrator" ? "PG-041" : "PG-032",
            _ => target,
        };
        if (pageId != _currentPageId) { Navigate(pageId); }
    }

    private void RebuildNavigation()
    {
        ViewModel.ApplyPrincipal(_services.Session.Principal, ViewModel.Destinations.Any(item => item.PageId == "PG-030"));
        _ = RefreshShellAsync();
    }

    private string InstanceHome()
    {
        return _services.Session.Principal?.User.Role switch { "Owner" or "Administrator" => "PG-010", "User" => "PG-011", _ => "PG-002" };
    }

    private void ApplyTheme(string theme)
    {
        WindowThemeService.Apply(RootGrid, _appWindow, theme);
    }

    private void OnLocalSettingsChanged(object? sender, EventArgs args)
    {
        _ = RootGrid.DispatcherQueue.TryEnqueue(() =>
        {
            ApplyTheme(_services.Settings.Current.Theme);
            ViewModel.ConnectionTitle = _services.Profiles.Active.Name;
            ViewModel.ConnectionAddress = _services.Profiles.Active.Address.AbsoluteUri.TrimEnd('/');
        });
    }

    private void OnRootSizeChanged(object sender, SizeChangedEventArgs args) { ApplyResponsiveLayout(args.NewSize.Width, args.NewSize.Height); }
    private void OnTitleBarSizeChanged(object sender, SizeChangedEventArgs args) { ApplyResponsiveLayout(RootGrid.ActualWidth, RootGrid.ActualHeight); }

    private void OnRootLoaded(object sender, RoutedEventArgs args) { ApplyResponsiveLayout(RootGrid.ActualWidth, RootGrid.ActualHeight); }

    private void ApplyResponsiveLayout(double width, double height)
    {
        bool compact = WindowStateService.Breakpoint(width) == "compact";
        double availableHeight = height - AppTitleBar.ActualHeight;
        if (availableHeight > 0)
        {
            Navigation.Height = availableHeight;
            PageContentGrid.Height = availableHeight;
        }
        ViewModel.IsNarrow = compact;
        ViewModel.IsCompact = WindowStateService.Breakpoint(width) != "expanded";
        ConnectionButton.MaxWidth = width * 0.32;
        Navigation.PaneDisplayMode = compact ? NavigationViewPaneDisplayMode.LeftMinimal : NavigationViewPaneDisplayMode.LeftCompact;
    }

    private void TitleBar_PaneToggleRequested(TitleBar sender, object args)
    {
        Navigation.IsPaneOpen = !Navigation.IsPaneOpen;
    }

    private void FocusGlobalSearch()
    {
        if (!ViewModel.IsAuthenticated) { return; }
        if (ViewModel.IsNarrow) { CompactSearchFlyout.ShowAt(CompactSearchButton); }
        else { _ = GlobalSearch.Focus(FocusState.Keyboard); }
    }

    private void CompactSearchFlyout_Opened(object sender, object args)
    {
        _ = CompactSearch.Focus(FocusState.Keyboard);
    }

    private void OperationsButton_Click(object sender, RoutedEventArgs e) { Navigate("PG-044"); }

    private void ShowError(Exception exception)
    {
        ShellStatus.Message = DesktopErrorPresentation.Describe(exception, _services.Localization);
        ShellStatus.Severity = InfoBarSeverity.Error;
        ShellStatus.IsOpen = true;
    }

    private static KeyboardAccelerator CreateAccelerator(VirtualKey key, VirtualKeyModifiers modifiers, Action action)
    {
        KeyboardAccelerator accelerator = new() { Key = key, Modifiers = modifiers };
        accelerator.Invoked += (_, args) => { action(); args.Handled = true; };
        return accelerator;
    }

    private void ApplyResourceSelection(DeepLinkTarget target)
    {
        if (target.ResourceId is not Guid id) { return; }
        _services.Session.SelectedInstance = _services.Session.Instances.FirstOrDefault(item => item.Id == id);
        _services.Session.SelectedNode = _services.Session.Nodes.FirstOrDefault(item => item.Id == id);
        _services.Session.SelectedChannel = _services.Session.Channels.FirstOrDefault(item => item.Id == id);
    }

    public void Dispose()
    {
        if (_disposed) { return; }
        _disposed = true;
        _exitRequested = true;
        RootGrid.SizeChanged -= OnRootSizeChanged;
        AppTitleBar.SizeChanged -= OnTitleBarSizeChanged;
        RootGrid.Loaded -= OnRootLoaded;
        Closed -= OnClosed;
        _appWindow.Closing -= OnAppWindowClosing;
        _services.Tray.ShowRequested -= OnTrayShowRequested;
        _services.Tray.PauseRequested -= OnTrayPauseRequested;
        _services.Tray.InstanceActionRequested -= OnTrayInstanceActionRequested;
        _services.Tray.AboutRequested -= OnTrayAboutRequested;
        _services.Tray.ExitRequested -= OnTrayExitRequested;
        _services.Session.PropertyChanged -= OnSessionPropertyChanged;
        _services.Settings.Changed -= OnLocalSettingsChanged;
        UnsubscribeRealtime();
        _search?.Cancel();
        _search?.Dispose();
        _connectionLifetime.Cancel();
        _connectionLifetime.Dispose();
    }
}
