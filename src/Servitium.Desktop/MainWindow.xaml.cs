using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.Windows.AppLifecycle;
using Microsoft.Windows.AppNotifications;

using Servitium.Contracts.Identity;
using Servitium.Contracts.Events;
using Servitium.Contracts.Instances;
using Servitium.Client.Realtime;
using Servitium.Desktop.AppModel;
using Servitium.Desktop.Models;
using Servitium.Desktop.Services;
using Servitium.Desktop.Shell;
using Servitium.Desktop.Views;

using Windows.System;
using Windows.UI;
using Windows.ApplicationModel.Activation;
using Windows.Globalization;

namespace Servitium.Desktop;

#pragma warning disable IDE0051, IDE0060 // WinUI XAML 通过名称绑定事件处理器。
/// <summary>Servitium Desktop 正式主窗口与导航组合根。</summary>
internal sealed partial class MainWindow : Window, IPageNavigator, IDisposable
{
    private static readonly string[] AdministratorPages = ["PG-040", "PG-010", "PG-020", "PG-060", "PG-030", "PG-041"];
    private static readonly string[] UserPages = ["PG-011"];
    private readonly AppServices _services;
    private readonly Stack<string> _backStack = new();
    private readonly Dictionary<string, NavigationViewItem> _items = new(StringComparer.Ordinal);
    private readonly AppWindow _appWindow;
    private string? _currentPageId;
    private bool _disposed;
    private bool _exitRequested;
    private bool _closePromptPending;
    private bool _updatingServerProfiles;
    private bool _navigationPending;
    private string? _responsiveBreakpoint;
    private EventsConnection? _events;

    internal MainWindow(AppServices services, Uri serverAddress)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(serverAddress);
        _services = services;
        InitializeComponent();
        _services.AttachWindow(this);
        _appWindow = WindowStateService.GetAppWindow(this);
        Title = _services.Localization.Get("app.window-title");
        ApplicationTitle.Text = Title;
        ServerAddressBox.Header = _services.Localization.Get("shell.server-address");
        ServerAddressBox.Text = serverAddress.AbsoluteUri.TrimEnd('/');
        ServerProfileBox.Header = _services.Localization.Get("shell.server-profile");
        ConnectButton.Content = _services.Localization.Get("shell.connection");
        RenameServerButton.Content = _services.Localization.Get("action.rename");
        DeleteServerButton.Content = _services.Localization.Get("action.delete");
        ConnectionStatus.Text = _services.Localization.Get("shell.status-disconnected");
        Navigation.PaneTitle = _services.Localization.Get("shell.navigation");
        ConfigurePreferences();
        RefreshServerProfiles();
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(BackButton, _services.Localization.Get("shell.back"));
        ExtendsContentIntoTitleBar = true;
        SystemBackdrop = new MicaBackdrop();
        SetTitleBar(AppTitleBar);
        ConfigureWindow();
        _appWindow.Closing += OnAppWindowClosing;
        _services.Tray.ShowRequested += OnTrayShowRequested;
        _services.Tray.PauseRequested += OnTrayPauseRequested;
        _services.Tray.InstanceActionRequested += OnTrayInstanceActionRequested;
        _services.Tray.AboutRequested += OnTrayAboutRequested;
        _services.Tray.ExitRequested += OnTrayExitRequested;
        RebuildNavigation();
        SizeChanged += OnSizeChanged;
        Navigation.PaneOpening += Navigation_PaneOpening;
        Navigation.PaneClosing += Navigation_PaneClosing;
        RootGrid.Loaded += (_, _) => ApplyResponsiveLayout(RootGrid.ActualWidth);
        ApplyResponsiveLayout(_appWindow.Size.Width);
        Closed += OnClosed;
        RootGrid.KeyboardAccelerators.Add(CreateAccelerator(VirtualKey.Left, VirtualKeyModifiers.Menu, GoBack));
        RootGrid.KeyboardAccelerators.Add(CreateAccelerator(VirtualKey.F5, VirtualKeyModifiers.None, RefreshCurrentPage));
        _services.Session.PropertyChanged += OnSessionPropertyChanged;
    }

    internal async Task RunConnectionSelfTestAsync()
    {
        Uri address = ParseAddress();
        await _services.Session.ConnectAsync(address, CancellationToken.None).ConfigureAwait(true);
        _ = await _services.Session.GetAuthenticationStateAsync(CancellationToken.None).ConfigureAwait(true);
    }

    internal void ApplyCurrentResponsiveLayout()
    {
        ApplyResponsiveLayout(_appWindow.Size.Width);
    }

    internal async Task InitializeSessionAsync()
    {
        await ConnectAsync().ConfigureAwait(true);
        if (_services.Session.Principal is not null &&
            _services.Settings.Current.LastPageId is string lastPage &&
            UiContractCatalog.All.Any(page => page.Id == lastPage))
        {
            await NavigateAsync(lastPage, false).ConfigureAwait(true);
        }
    }

    internal void Navigate(DeepLinkTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        ApplyResourceSelection(target);
        Navigate(target.PageId);
    }

    internal void HandleActivation(AppActivationArguments arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        _ = RootGrid.DispatcherQueue.TryEnqueue(() =>
        {
            OnTrayShowRequested(this, EventArgs.Empty);
            if (arguments.Kind == ExtendedActivationKind.Protocol &&
                arguments.Data is ProtocolActivatedEventArgs protocol &&
                DeepLinkService.TryParse(protocol.Uri.AbsoluteUri, out DeepLinkTarget? target) &&
                target is not null)
            {
                Navigate(target);
            }
            else if (arguments.Kind == ExtendedActivationKind.AppNotification &&
                arguments.Data is AppNotificationActivatedEventArgs notification &&
                notification.Arguments.TryGetValue("page", out string? pageId))
            {
                Guid? resource = notification.Arguments.TryGetValue("resource", out string? value) && Guid.TryParse(value, out Guid parsed)
                    ? parsed
                    : null;
                Navigate(new DeepLinkTarget(pageId, resource, new Dictionary<string, string>(notification.Arguments, StringComparer.Ordinal)));
            }
        });
    }

    public void Navigate(string pageId, bool addToBackStack = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pageId);
        _ = NavigateAsync(pageId, addToBackStack);
    }

    private async Task NavigateAsync(string pageId, bool addToBackStack)
    {
        if (_navigationPending)
        {
            return;
        }

        _navigationPending = true;
        try
        {
            if (PageHost.Content is INavigationGuard guard &&
                !await guard.CanNavigateAwayAsync().ConfigureAwait(true))
            {
                if (_currentPageId is not null && _items.TryGetValue(_currentPageId, out NavigationViewItem? current))
                {
                    Navigation.SelectedItem = current;
                }

                return;
            }

            NavigateCore(pageId, addToBackStack);
        }
        finally
        {
            _navigationPending = false;
        }
    }

    private void NavigateCore(string pageId, bool addToBackStack)
    {
        if (_currentPageId == pageId)
        {
            PageHost.Content = ProductPageFactory.Create(pageId, _services, this);
            return;
        }

        if (addToBackStack && _currentPageId is not null)
        {
            _backStack.Push(_currentPageId);
        }

        _currentPageId = pageId;
        PageHost.Content = ProductPageFactory.Create(pageId, _services, this);
        BackButton.IsEnabled = _backStack.Count > 0;
        if (_items.TryGetValue(pageId, out NavigationViewItem? item))
        {
            Navigation.SelectedItem = item;
        }

        _ = TryUpdateSettingsAsync(current => current with { LastPageId = pageId }, "settings.page-save-failed");
    }

    private async void ConnectButton_Click(object sender, RoutedEventArgs e)
    {
        await ConnectAsync().ConfigureAwait(true);
    }

    private async void SignOutButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_services.Session.IsConnected)
        {
            return;
        }

        await _services.Session.SignOutAsync(CancellationToken.None).ConfigureAwait(true);
        RebuildNavigation();
        Navigate("PG-002", false);
        SetConnectionState("shell.status-signed-out", Colors.Orange);
    }

    private async void LanguageBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (LanguageBox.SelectedItem is not ComboBoxItem { Tag: string language } ||
            language == _services.Settings.Current.Language)
        {
            return;
        }

        try
        {
            ApplicationLanguages.PrimaryLanguageOverride = language;
        }
        catch (InvalidOperationException)
        {
            // 未打包的 Windows App SDK 进程没有应用语言覆盖状态。
        }

        _services.Localization.Reload(language);
        _ = await TryUpdateSettingsAsync(current => current with { Language = language }, "settings.language-save-failed").ConfigureAwait(true);
        RefreshLocalizedShell();
    }

    private async void ThemeBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ThemeBox.SelectedItem is not ComboBoxItem { Tag: string theme } ||
            theme == _services.Settings.Current.Theme)
        {
            return;
        }

        ApplyTheme(theme);
        _ = await TryUpdateSettingsAsync(current => current with { Theme = theme }, "settings.theme-save-failed").ConfigureAwait(true);
    }

    private async Task ConnectAsync()
    {
        SetConnectionState("shell.status-connecting", Colors.Orange);
        try
        {
            Uri address = ParseAddress();
            await _services.Session.ConnectAsync(address, CancellationToken.None).ConfigureAwait(true);
            AuthenticationStateDto state = await _services.Session.GetAuthenticationStateAsync(CancellationToken.None).ConfigureAwait(true);
            RefreshServerProfiles();
            ServerTitle.Text = _services.Profiles.Active.Name;
            _services.Notifications.Start();
            SubscribeRealtime();
            UpdateTrayInstances();
            SetConnectionState(_services.Session.Principal is null ? "shell.status-signed-out" : "shell.status-ready", Colors.LimeGreen);
            if (_services.Session.Principal is null && _services.Session.LocalAuthenticationErrorCode is string errorCode)
            {
                ShellStatus.Message = _services.Localization.Get("error." + errorCode);
                ShellStatus.Severity = InfoBarSeverity.Informational;
                ShellStatus.IsOpen = true;
            }
            RebuildNavigation();
            string target = !state.Initialized ? "PG-001" : _services.Session.Principal is null ? "PG-002" : DefaultPage();
            await NavigateAsync(target, false).ConfigureAwait(true);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or Client.ServitiumApiException or System.Net.Http.HttpRequestException)
        {
            SetConnectionState("shell.status-disconnected", Colors.IndianRed);
            ShowError(exception);
        }
    }

    private void Navigation_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItem is NavigationViewItem { Tag: string pageId } && pageId != _currentPageId)
        {
            Navigate(pageId);
        }
    }

    private async void ServerProfileBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updatingServerProfiles || ServerProfileBox.SelectedItem is not ComboBoxItem { Tag: Guid profileId })
        {
            return;
        }

        try
        {
            await _services.Profiles.SelectAsync(profileId, CancellationToken.None).ConfigureAwait(true);
            ServerAddressBox.Text = _services.Profiles.Active.Address.AbsoluteUri.TrimEnd('/');
            ServerTitle.Text = _services.Profiles.Active.Name;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            ShowError(exception);
        }
    }

    private async void RenameServerButton_Click(object sender, RoutedEventArgs e)
    {
        string? name = await _services.Interaction.PromptAsync(
            "action.rename", "field.name", _services.Profiles.Active.Name).ConfigureAwait(true);
        if (name is null)
        {
            return;
        }

        try
        {
            await _services.Profiles.RenameAsync(_services.Profiles.Active.Id, name, CancellationToken.None).ConfigureAwait(true);
            RefreshServerProfiles();
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            ShowError(exception);
        }
    }

    private async void DeleteServerButton_Click(object sender, RoutedEventArgs e)
    {
        ServerProfile profile = _services.Profiles.Active;
        if (!await _services.Interaction.ConfirmNamedAsync(
            "action.delete", profile.Name, _services.Localization.Get("serverprofile.delete-impact")).ConfigureAwait(true))
        {
            return;
        }

        try
        {
            await _services.Profiles.DeleteAsync(profile.Id, CancellationToken.None).ConfigureAwait(true);
            ServerAddressBox.Text = _services.Profiles.Active.Address.AbsoluteUri.TrimEnd('/');
            RefreshServerProfiles();
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            ShowError(exception);
        }
    }

    private void BackButton_Click(object sender, RoutedEventArgs e)
    {
        GoBack();
    }

    private void GoBack()
    {
        _ = GoBackAsync();
    }

    private async Task GoBackAsync()
    {
        if (_navigationPending || !_backStack.TryPeek(out string? pageId))
        {
            return;
        }

        _navigationPending = true;
        try
        {
            if (PageHost.Content is INavigationGuard guard &&
                !await guard.CanNavigateAwayAsync().ConfigureAwait(true))
            {
                return;
            }

            _ = _backStack.Pop();
            NavigateCore(pageId, false);
        }
        finally
        {
            _navigationPending = false;
        }
    }

    private void RefreshCurrentPage()
    {
        if (_currentPageId is not null)
        {
            Navigate(_currentPageId, false);
        }
    }

    private void RebuildNavigation()
    {
        Navigation.MenuItems.Clear();
        Navigation.FooterMenuItems.Clear();
        _items.Clear();
        string? role = _services.Session.Principal?.User.Role;
        IEnumerable<string> pages = role switch
        {
            "Owner" or "Administrator" => AdministratorPages,
            "User" => UserPages,
            _ => ["PG-050"],
        };
        foreach (string pageId in pages)
        {
            AddItem(Navigation.MenuItems, pageId);
        }

        if (role is not null)
        {
            AddItem(Navigation.FooterMenuItems, "PG-044");
            AddItem(Navigation.FooterMenuItems, "PG-032");
        }
    }

    private void ConfigurePreferences()
    {
        LanguageBox.Header = _services.Localization.Get("shell.language");
        LanguageBox.Items.Add(new ComboBoxItem { Content = _services.Localization.Get("language.zh-hans"), Tag = "zh-Hans" });
        LanguageBox.Items.Add(new ComboBoxItem { Content = _services.Localization.Get("language.en-us"), Tag = "en-US" });
        LanguageBox.SelectedItem = LanguageBox.Items.Cast<ComboBoxItem>()
            .FirstOrDefault(item => string.Equals(item.Tag as string, _services.Settings.Current.Language, StringComparison.Ordinal));
        ThemeBox.Header = _services.Localization.Get("shell.theme");
        ThemeBox.Items.Add(new ComboBoxItem { Content = _services.Localization.Get("theme.system"), Tag = "system" });
        ThemeBox.Items.Add(new ComboBoxItem { Content = _services.Localization.Get("theme.light"), Tag = "light" });
        ThemeBox.Items.Add(new ComboBoxItem { Content = _services.Localization.Get("theme.dark"), Tag = "dark" });
        ThemeBox.SelectedItem = ThemeBox.Items.Cast<ComboBoxItem>()
            .FirstOrDefault(item => string.Equals(item.Tag as string, _services.Settings.Current.Theme, StringComparison.Ordinal));
        CloseToTrayToggle.Header = _services.Localization.Get("shell.close-to-tray");
        CloseToTrayToggle.IsOn = _services.Settings.Current.CloseToTray;
        TerminalFontSizeLabel.Text = _services.Localization.Get("shell.terminal-font-size");
        TerminalFontSizeSlider.Value = _services.Settings.Current.TerminalFontSize;
        NotificationSettingsButton.Content = _services.Localization.Get("tray.notification-settings");
        SignOutButton.Content = _services.Localization.Get("shell.sign-out");
        ApplyTheme(_services.Settings.Current.Theme);
    }

    private void RefreshLocalizedShell()
    {
        Title = _services.Localization.Get("app.window-title");
        ApplicationTitle.Text = Title;
        ServerAddressBox.Header = _services.Localization.Get("shell.server-address");
        ServerProfileBox.Header = _services.Localization.Get("shell.server-profile");
        ConnectButton.Content = _services.Localization.Get("shell.connection");
        RenameServerButton.Content = _services.Localization.Get("action.rename");
        DeleteServerButton.Content = _services.Localization.Get("action.delete");
        Navigation.PaneTitle = _services.Localization.Get("shell.navigation");
        CloseToTrayToggle.Header = _services.Localization.Get("shell.close-to-tray");
        TerminalFontSizeLabel.Text = _services.Localization.Get("shell.terminal-font-size");
        NotificationSettingsButton.Content = _services.Localization.Get("tray.notification-settings");
        SignOutButton.Content = _services.Localization.Get("shell.sign-out");
        RebuildNavigation();
        RefreshCurrentPage();
    }

    private void RefreshServerProfiles()
    {
        _updatingServerProfiles = true;
        try
        {
            ServerProfileBox.Items.Clear();
            foreach (ServerProfile profile in _services.Profiles.Profiles.OrderByDescending(item => item.LastUsedAt))
            {
                ComboBoxItem item = new() { Content = profile.Name, Tag = profile.Id };
                ServerProfileBox.Items.Add(item);
                if (profile.Id == _services.Profiles.Active.Id)
                {
                    ServerProfileBox.SelectedItem = item;
                }
            }

            ServerTitle.Text = _services.Profiles.Active.Name;
            DeleteServerButton.IsEnabled = _services.Profiles.Profiles.Count > 1;
        }
        finally
        {
            _updatingServerProfiles = false;
        }
    }

    private void ApplyTheme(string theme)
    {
        RootGrid.RequestedTheme = theme switch
        {
            "light" => ElementTheme.Light,
            "dark" => ElementTheme.Dark,
            _ => ElementTheme.Default,
        };
    }

    private async void CloseToTrayToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (CloseToTrayToggle.IsOn == _services.Settings.Current.CloseToTray)
        {
            return;
        }

        _ = await TryUpdateSettingsAsync(current => current with { CloseToTray = CloseToTrayToggle.IsOn }, "settings.close-to-tray-save-failed").ConfigureAwait(true);
    }

    private async void TerminalFontSizeSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        double value = Math.Clamp(Math.Round(e.NewValue), 12, 20);
        if (Math.Abs(value - _services.Settings.Current.TerminalFontSize) < 0.1)
        {
            return;
        }

        _ = await TryUpdateSettingsAsync(current => current with { TerminalFontSize = value }, "settings.terminal-font-save-failed").ConfigureAwait(true);
    }

    private async void NotificationSettingsButton_Click(object sender, RoutedEventArgs e)
    {
        IReadOnlyDictionary<string, bool>? preferences = await _services.Interaction
            .ConfigureNotificationPreferencesAsync(_services.Settings.Current.NotificationPreferences)
            .ConfigureAwait(true);
        if (preferences is not null)
        {
            _ = await TryUpdateSettingsAsync(current => current with { NotificationPreferences = preferences }, "settings.notifications-save-failed").ConfigureAwait(true);
        }
    }

    private void AddItem(IList<object> target, string pageId)
    {
        UiPageContract contract = UiContractCatalog.Get(pageId);
        NavigationViewItem item = new()
        {
            Content = _services.Localization.Get(contract.TextKeys[0]),
            Tag = pageId,
            Icon = new SymbolIcon(Symbol.Document),
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(item, "navigation:" + pageId);
        target.Add(item);
        _items.Add(pageId, item);
    }

    private string DefaultPage()
    {
        return _services.Session.Principal?.User.Role == "User" ? "PG-011" : "PG-040";
    }

    private Uri ParseAddress()
    {
        return !Uri.TryCreate(ServerAddressBox.Text, UriKind.Absolute, out Uri? address) ||
            address.Scheme is not ("http" or "https")
            ? throw new ArgumentException(_services.Localization.Get("error.validation-failed"))
            : address;
    }

    private void SetConnectionState(string key, Color color)
    {
        ConnectionStatus.Text = _services.Localization.Get(key);
        ConnectionIndicator.Fill = new SolidColorBrush(color);
        _services.Tray.UpdateState(key == "shell.status-disconnected" ? "disconnected" : "normal");
    }

    private void SubscribeRealtime()
    {
        if (_events is not null)
        {
            _events.StateChanged -= OnRealtimeStateChanged;
            _events.EventReceived -= OnRealtimeEvent;
            _events.FullRefreshRequired -= OnFullRefreshRequired;
        }

        _events = _services.Session.Client.Events;
        _events.StateChanged += OnRealtimeStateChanged;
        _events.EventReceived += OnRealtimeEvent;
        _events.FullRefreshRequired += OnFullRefreshRequired;
    }

    private void OnRealtimeStateChanged(object? sender, ConnectionStateChangedEventArgs args)
    {
        _ = RootGrid.DispatcherQueue.TryEnqueue(() =>
            SetConnectionState(args.State == "connected" ? "shell.status-ready" : "shell.status-disconnected",
                args.State == "connected" ? Colors.LimeGreen : Colors.IndianRed));
    }

    private void OnRealtimeEvent(object? sender, RealtimeEventArgs args)
    {
        if (args.Value.Type is EventTypes.InstanceCrashed or EventTypes.NodeStatusChanged or EventTypes.HmclIntegrityAlert)
        {
            _services.Tray.UpdateState("warning");
        }

        if (args.Value.Type is EventTypes.AuthorizationChanged or EventTypes.SessionRevoked)
        {
            _ = RootGrid.DispatcherQueue.TryEnqueue(RefreshAuthorizationAsync);
        }

        if (args.Value.Type is EventTypes.InstanceCreated or EventTypes.InstanceUpdated or EventTypes.InstanceUnregistered or
            EventTypes.InstanceStatusChanged or EventTypes.InstanceCrashed)
        {
            _ = RootGrid.DispatcherQueue.TryEnqueue(RefreshTrayInstancesAsync);
        }
    }

    private void OnFullRefreshRequired(object? sender, EventArgs e)
    {
        _ = RootGrid.DispatcherQueue.TryEnqueue(RefreshCurrentPage);
    }

    private async void RefreshAuthorizationAsync()
    {
        try
        {
            _ = await _services.Session.GetAuthenticationStateAsync(CancellationToken.None).ConfigureAwait(true);
            RebuildNavigation();
            RefreshCurrentPage();
        }
        catch (Client.ServitiumApiException exception)
        {
            ShowError(exception);
            Navigate("PG-002", false);
        }
    }

    private void ShowError(Exception exception)
    {
        string message = exception is Client.ServitiumApiException api
            ? _services.Localization.Get("error." + api.Code) + (api.TraceId is null ? string.Empty : " · " + _services.Localization.Get("common.trace-id") + ": " + api.TraceId)
            : exception.Message;
        ShellStatus.Message = message;
        ShellStatus.Severity = InfoBarSeverity.Error;
        ShellStatus.IsOpen = true;
    }

    private void ConfigureWindow()
    {
        AppWindow appWindow = WindowStateService.GetAppWindow(this);
        if (appWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.SetBorderAndTitleBar(true, true);
            bool layoutTest = Environment.GetCommandLineArgs().Contains("--ui-layout-test", StringComparer.Ordinal);
            presenter.PreferredMinimumWidth = layoutTest ? 560 : 960;
            presenter.PreferredMinimumHeight = layoutTest ? 420 : 640;
        }

        if (AppWindowTitleBar.IsCustomizationSupported())
        {
            appWindow.TitleBar.ButtonBackgroundColor = Colors.Transparent;
            appWindow.TitleBar.ButtonInactiveBackgroundColor = Colors.Transparent;
        }
    }

    private void OnSizeChanged(object sender, WindowSizeChangedEventArgs args)
    {
        ApplyResponsiveLayout(args.Size.Width);
    }

    private void ApplyResponsiveLayout(double width)
    {
        string breakpoint = WindowStateService.Breakpoint(width);
        double availableHeight = Math.Max(0, RootGrid.ActualHeight - AppTitleBar.ActualHeight);
        if (availableHeight > 0)
        {
            Navigation.Height = availableHeight;
            PageContentGrid.Height = availableHeight;
        }

        bool breakpointChanged = !string.Equals(_responsiveBreakpoint, breakpoint, StringComparison.Ordinal);
        Navigation.PaneDisplayMode = breakpoint switch
        {
            "compact" => NavigationViewPaneDisplayMode.LeftMinimal,
            "medium" => NavigationViewPaneDisplayMode.LeftCompact,
            _ => NavigationViewPaneDisplayMode.Auto,
        };

        bool showTitleBarDetails = breakpoint is not "compact";
        ApplicationTitle.Visibility = showTitleBarDetails ? Visibility.Visible : Visibility.Collapsed;
        ServerTitle.Visibility = showTitleBarDetails ? Visibility.Visible : Visibility.Collapsed;
        ConnectionStatus.Visibility = showTitleBarDetails ? Visibility.Visible : Visibility.Collapsed;
        if (breakpointChanged && breakpoint != "expanded" && Navigation.IsPaneOpen)
        {
            Navigation.IsPaneOpen = false;
        }

        _responsiveBreakpoint = breakpoint;
        bool showPaneTools = breakpoint == "expanded" || Navigation.IsPaneOpen;
        ConnectionPane.Visibility = showPaneTools ? Visibility.Visible : Visibility.Collapsed;
        PreferencesPane.Visibility = showPaneTools ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Navigation_PaneOpening(NavigationView sender, object args)
    {
        ConnectionPane.Visibility = Visibility.Visible;
        PreferencesPane.Visibility = Visibility.Visible;
    }

    private void Navigation_PaneClosing(NavigationView sender, object args)
    {
        ApplyResponsiveLayout(RootGrid.ActualWidth);
    }

    private void OnAppWindowClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (!_exitRequested && _services.Settings.Current.CloseToTray)
        {
            args.Cancel = true;
            _ = HandleCloseToTrayAsync(sender);
        }
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "The close-to-tray boundary must contain local settings failures and keep the running client usable.")]
    private async Task HandleCloseToTrayAsync(AppWindow sender)
    {
        if (_closePromptPending)
        {
            return;
        }

        _closePromptPending = true;
        try
        {
            if (!_services.Settings.Current.CloseToTrayExplained)
            {
                await _services.Interaction.ShowAsync("shell.close-to-tray", _services.Localization.Get("tray.close-explanation")).ConfigureAwait(true);
                _ = await TryUpdateSettingsAsync(current => current with { CloseToTrayExplained = true }, "settings.close-explanation-save-failed").ConfigureAwait(true);
            }

            sender.Hide();
        }
        catch (Exception exception)
        {
            await _services.Log.WriteAsync("warning", "tray.close-failed", exception.GetType().Name).ConfigureAwait(true);
        }
        finally
        {
            _closePromptPending = false;
        }
    }

    private void OnTrayShowRequested(object? sender, EventArgs e)
    {
        _appWindow.Show();
        Activate();
    }

    private async void OnTrayPauseRequested(object? sender, NotificationPauseRequestedEventArgs e)
    {
        DateTimeOffset now = _services.TimeProvider.GetUtcNow();
        if (e.Mode == NotificationPauseMode.UntilRestart)
        {
            bool clearedPersistentPause = await TryUpdateSettingsAsync(
                current => current with { NotificationsPaused = false, NotificationsPausedUntil = null },
                "settings.pause-save-failed").ConfigureAwait(true);
            if (clearedPersistentPause)
            {
                _services.Notifications.PauseUntilRestart();
            }

            return;
        }

        bool paused = e.Mode is NotificationPauseMode.ThirtyMinutes or NotificationPauseMode.OneHour;
        DateTimeOffset? until = NotificationPauseState.PersistedDeadline(e.Mode, now);
        _services.Notifications.ClearProcessPause();
        bool saved = await TryUpdateSettingsAsync(
            current => current with { NotificationsPaused = paused, NotificationsPausedUntil = until },
            "settings.pause-save-failed").ConfigureAwait(true);
        if (saved && !paused)
        {
            _services.Notifications.ResumeAndFlush();
        }
    }

    private async void OnTrayInstanceActionRequested(object? sender, TrayInstanceActionEventArgs e)
    {
        InstanceDto? instance = _services.Session.Instances.FirstOrDefault(item => item.Id == e.InstanceId);
        if (instance is null)
        {
            return;
        }

        try
        {
            _services.Session.SelectedInstance = instance;
            if (e.Action == "open-console")
            {
                OnTrayShowRequested(this, EventArgs.Empty);
                Navigate("PG-012");
                return;
            }

            _ = e.Action switch
            {
                "start" => await _services.Session.Client.StartInstanceAsync(instance.Id, instance.NodeId, CancellationToken.None).ConfigureAwait(true),
                "stop" => await _services.Session.Client.StopInstanceAsync(instance.Id, instance.NodeId, CancellationToken.None).ConfigureAwait(true),
                "restart" => await _services.Session.Client.RestartInstanceAsync(instance.Id, instance.NodeId, CancellationToken.None).ConfigureAwait(true),
                _ => throw new ArgumentOutOfRangeException(nameof(e)),
            };
        }
        catch (Exception exception) when (exception is Client.ServitiumApiException or InvalidOperationException or ArgumentException)
        {
            OnTrayShowRequested(this, EventArgs.Empty);
            ShowError(exception);
        }
    }

    private async void OnTrayAboutRequested(object? sender, EventArgs e)
    {
        await _services.Interaction.ShowAsync("tray.about", _services.Localization.Get("app.description")).ConfigureAwait(true);
    }

    private async void OnTrayExitRequested(object? sender, EventArgs e)
    {
        if (!await _services.Interaction.ConfirmAsync("shell.exit", _services.Localization.Get("tray.exit-confirmation")).ConfigureAwait(true))
        {
            return;
        }

        _exitRequested = true;
        _services.Tray.Dispose();
        Close();
    }

    private async void OnSessionPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(SessionService.SelectedInstance) || _services.Session.SelectedInstance is not InstanceDto instance)
        {
            return;
        }

        bool saved = await TryUpdateSettingsAsync(
            current => current with { RecentInstanceIds = [instance.Id, .. current.RecentInstanceIds.Where(id => id != instance.Id).Take(9)] },
            "settings.recent-instance-save-failed").ConfigureAwait(true);
        if (saved)
        {
            UpdateTrayInstances();
        }
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "Local preference persistence failures are logged without terminating the WinUI event loop.")]
    private async Task<bool> TryUpdateSettingsAsync(Func<DesktopSettings, DesktopSettings> update, string eventName)
    {
        try
        {
            await _services.Settings.UpdateAsync(update, CancellationToken.None).ConfigureAwait(true);
            return true;
        }
        catch (Exception exception)
        {
            await _services.Log.WriteAsync("warning", eventName, exception.GetType().Name).ConfigureAwait(true);
            return false;
        }
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "Realtime tray refresh failures are recorded without terminating the Desktop event loop.")]
    private async void RefreshTrayInstancesAsync()
    {
        try
        {
            await _services.Session.RefreshSelectionsAsync(CancellationToken.None).ConfigureAwait(true);
            UpdateTrayInstances();
        }
        catch (Exception exception)
        {
            await _services.Log.WriteAsync("warning", "tray.instances-refresh-failed", exception.GetType().Name).ConfigureAwait(true);
        }
    }

    private void UpdateTrayInstances()
    {
        _services.Tray.UpdateInstances(_services.Session.Instances, _services.Settings.Current.RecentInstanceIds);
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "The window-close boundary must not prevent process shutdown if settings persistence fails.")]
    private async void OnClosed(object sender, WindowEventArgs args)
    {
        try
        {
            await _services.WindowState.SaveAsync(this, CancellationToken.None).ConfigureAwait(true);
        }
        catch (Exception exception)
        {
            await _services.Log.WriteAsync("warning", "window.state-save-failed", exception.GetType().Name).ConfigureAwait(true);
        }

        Dispose();
    }

    private static KeyboardAccelerator CreateAccelerator(VirtualKey key, VirtualKeyModifiers modifiers, Action action)
    {
        KeyboardAccelerator accelerator = new() { Key = key, Modifiers = modifiers };
        accelerator.Invoked += (_, args) =>
        {
            action();
            args.Handled = true;
        };
        return accelerator;
    }

    private void ApplyResourceSelection(DeepLinkTarget target)
    {
        Guid? id = target.ResourceId;
        if (id is null)
        {
            return;
        }

        _services.Session.SelectedInstance = _services.Session.Instances.FirstOrDefault(item => item.Id == id);
        _services.Session.SelectedNode = _services.Session.Nodes.FirstOrDefault(item => item.Id == id);
        _services.Session.SelectedChannel = _services.Session.Channels.FirstOrDefault(item => item.Id == id);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _exitRequested = true;
        SizeChanged -= OnSizeChanged;
        Closed -= OnClosed;
        _appWindow.Closing -= OnAppWindowClosing;
        _services.Tray.ShowRequested -= OnTrayShowRequested;
        _services.Tray.PauseRequested -= OnTrayPauseRequested;
        _services.Tray.InstanceActionRequested -= OnTrayInstanceActionRequested;
        _services.Tray.AboutRequested -= OnTrayAboutRequested;
        _services.Tray.ExitRequested -= OnTrayExitRequested;
        _services.Session.PropertyChanged -= OnSessionPropertyChanged;
        if (_events is not null)
        {
            _events.StateChanged -= OnRealtimeStateChanged;
            _events.EventReceived -= OnRealtimeEvent;
            _events.FullRefreshRequired -= OnFullRefreshRequired;
            _events = null;
        }
    }
}
#pragma warning restore IDE0051, IDE0060
