using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml.Controls;

using Servitium.Client;
using Servitium.Client.Realtime;
using Servitium.Contracts.Events;
using Servitium.Contracts.Identity;
using Servitium.Desktop.Models;
using Servitium.Desktop.ViewModels;

namespace Servitium.Desktop;

internal sealed partial class MainWindow
{
    public Task SwitchConnectionAsync(Uri address)
    {
        return ConnectAsync(address);
    }

    private async Task ConnectAsync(Uri address)
    {
        if (_connecting) { return; }
        _connecting = true;
        ConnectionButton.IsEnabled = false;
        try
        {
            if (PageHost.Content is Shell.INavigationGuard guard && !await guard.CanNavigateAwayAsync().ConfigureAwait(true)) { return; }
            UnsubscribeRealtime();
            await _connectionLifetime.CancelAsync().ConfigureAwait(true);
            _connectionLifetime.Dispose();
            _connectionLifetime = new CancellationTokenSource();
            ViewModel.ApplyPrincipal(null, false);
            PageHost.Content = null;
            await ClearSearchAsync().ConfigureAwait(true);
            ViewModel.SetConnection(_services.Profiles.Active.Name, address, "shell.status-connecting");
            await _services.Session.ConnectAsync(address, CancellationToken.None).ConfigureAwait(true);
            AuthenticationStateDto state = await _services.Session.GetAuthenticationStateAsync(CancellationToken.None).ConfigureAwait(true);
            _backStack.Clear();
            _currentEntry = null;
            _services.Notifications.Start();
            SubscribeRealtime();
            UpdateTrayInstances();
            ViewModel.SetConnection(_services.Profiles.Active.Name, address, _services.Session.Principal is null ? "shell.status-signed-out" : "shell.status-ready");
            await RefreshShellAsync().ConfigureAwait(true);
            await NavigateAsync(!state.Initialized ? "PG-001" : ViewModel.DefaultPage, false).ConfigureAwait(true);
        }
        catch (Exception exception) when (exception is ServitiumApiException or System.Net.Http.HttpRequestException or ArgumentException or InvalidOperationException or System.IO.IOException or UnauthorizedAccessException)
        {
            ViewModel.SetConnection(_services.Profiles.Active.Name, address, "shell.status-disconnected");
            NavigateCore("PG-002", false);
            ShowError(exception);
        }
        finally { _connecting = false; ConnectionButton.IsEnabled = true; }
    }

    private async Task RefreshShellAsync()
    {
        if (!_services.Session.IsConnected) { return; }
        CancellationToken token = _connectionLifetime.Token;
        try
        {
            await ViewModel.RefreshAsync(_services.Session.Client, _services.Session.Principal, token).ConfigureAwait(true);
        }
        catch (Exception exception) when (exception is ServitiumApiException or System.Net.Http.HttpRequestException)
        {
            ShowError(exception);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }

    private void ConnectionsMenu_Opening(object sender, object args)
    {
        ConnectionsMenu.Items.Clear();
        foreach (ServerProfile profile in _services.Profiles.Profiles)
        {
            MenuFlyoutItem item = new() { Text = profile.Name + " · " + profile.Address.AbsoluteUri.TrimEnd('/') };
            item.Click += async (_, _) =>
            {
                await ConnectAsync(profile.Address).ConfigureAwait(true);
            };
            ConnectionsMenu.Items.Add(item);
        }
        ConnectionsMenu.Items.Add(new MenuFlyoutSeparator());
        MenuFlyoutItem manage = new() { Text = ViewModel.ManageConnectionsText };
        manage.Click += async (_, _) => await ManageConnectionsAsync().ConfigureAwait(true);
        ConnectionsMenu.Items.Add(manage);
    }

    private async Task ManageConnectionsAsync()
    {
        Uri? selected = null;
        ContentDialog dialog = new()
        {
            XamlRoot = RootGrid.XamlRoot,
            RequestedTheme = RootGrid.RequestedTheme,
            Title = _services.Localization.Get("desktop.settings-category-connections"),
            CloseButtonText = _services.Localization.Get("common.close"),
        };
        ViewModels.Settings.ConnectionsViewModel model = new(_services.Localization, _services.Profiles,
            address => { selected = address; dialog.Hide(); return Task.CompletedTask; });
        using Views.Settings.ConnectionsView content = new(model);
        dialog.Content = content;
        _ = await dialog.ShowAsync();
        if (selected is not null) { await ConnectAsync(selected).ConfigureAwait(true); }
    }

    private async Task ClearSearchAsync()
    {
        if (_search is not null) { await _search.CancelAsync().ConfigureAwait(true); }
        _search?.Dispose();
        _search = null;
        GlobalSearch.Text = string.Empty;
        CompactSearch.Text = string.Empty;
        GlobalSearch.ItemsSource = null;
        CompactSearch.ItemsSource = null;
        CompactSearchFlyout.Hide();
    }

    private async void GlobalSearch_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (args.Reason != AutoSuggestionBoxTextChangeReason.UserInput) { return; }
        if (_search is not null) { await _search.CancelAsync().ConfigureAwait(true); }
        _search?.Dispose();
        _search = new CancellationTokenSource();
        CancellationToken token = _search.Token;
        if (_services.Session.Principal is not PrincipalDto principal) { return; }
        try
        {
            IReadOnlyList<ShellSearchResult> results = await ViewModel.SearchAsync(_services.Session.Client, principal, _services.Session.Instances,
                _services.Session.SelectedInstance, sender.Text, token).ConfigureAwait(true);
            if (token.IsCancellationRequested) { return; }
            sender.ItemsSource = results;
            if (ViewModel.SearchMessage.Length > 0)
            {
                ShellStatus.Message = ViewModel.SearchMessage;
                ShellStatus.Severity = InfoBarSeverity.Warning;
                ShellStatus.IsOpen = true;
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }

    private void GlobalSearch_QuerySubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
    {
        if (args.ChosenSuggestion is not ShellSearchResult result) { return; }
        _ = NavigateAsync(result.FilePath is null ? "PG-012" : "PG-013", true, () =>
        {
            _services.Session.SelectedInstance = result.Instance;
            _services.Session.PendingFilePath = result.FilePath;
            _services.Session.PendingConsoleCommand = result.Command;
            _ = ClearSearchAsync();
        });
    }

    private void SubscribeRealtime()
    {
        UnsubscribeRealtime();
        _events = _services.Session.Client.Events;
        _events.StateChanged += OnRealtimeStateChanged;
        _events.EventReceived += OnRealtimeEvent;
        _events.FullRefreshRequired += OnFullRefreshRequired;
        _events.AuthorizationChanged += OnAuthorizationChanged;
    }

    private void UnsubscribeRealtime()
    {
        if (_events is null) { return; }
        _events.StateChanged -= OnRealtimeStateChanged;
        _events.EventReceived -= OnRealtimeEvent;
        _events.FullRefreshRequired -= OnFullRefreshRequired;
        _events.AuthorizationChanged -= OnAuthorizationChanged;
        _events = null;
    }

    private void OnRealtimeStateChanged(object? sender, ConnectionStateChangedEventArgs args)
    {
        _ = RootGrid.DispatcherQueue.TryEnqueue(() =>
        {
            if (!ReferenceEquals(sender, _events)) { return; }
            ViewModel.SetConnection(_services.Profiles.Active.Name, _services.Profiles.Active.Address,
                args.State == "connected" ? "shell.status-ready" : "shell.status-disconnected");
            ViewModel.ApplyRealtimeState(args.State);
        });
    }

    private void OnRealtimeEvent(object? sender, RealtimeEventArgs args)
    {
        if (args.Value.Type is EventTypes.AuthorizationChanged or EventTypes.SessionRevoked)
        {
            _ = RootGrid.DispatcherQueue.TryEnqueue(RefreshAuthorizationAsync);
        }
        if (args.Value.Type is EventTypes.InstanceCreated or EventTypes.InstanceUpdated or EventTypes.InstanceUnregistered or EventTypes.InstanceStatusChanged or EventTypes.InstanceCrashed)
        {
            _ = RootGrid.DispatcherQueue.TryEnqueue(RefreshTrayInstancesAsync);
        }
        if (args.Value.Type is EventTypes.OperationStarted or EventTypes.OperationCompleted)
        {
            _ = RootGrid.DispatcherQueue.TryEnqueue(async () => await RefreshShellAsync().ConfigureAwait(true));
        }
    }

    private void OnFullRefreshRequired(object? sender, EventArgs args) { _ = RootGrid.DispatcherQueue.TryEnqueue(RefreshCurrentPage); }

    private void OnAuthorizationChanged(object? sender, EventArgs args)
    {
        _ = RootGrid.DispatcherQueue.TryEnqueue(() =>
        {
            if (ReferenceEquals(sender, _events)) { RefreshAuthorizationAsync(); }
        });
    }

    private async void RefreshAuthorizationAsync()
    {
        CancellationToken token = _connectionLifetime.Token;
        PrincipalDto? previous = _services.Session.Principal;
        try
        {
            _ = await _services.Session.GetAuthenticationStateAsync(token).ConfigureAwait(true);
            token.ThrowIfCancellationRequested();
            await RefreshShellAsync().ConfigureAwait(true);
            token.ThrowIfCancellationRequested();
            PrincipalDto? current = _services.Session.Principal;
            if (ShellViewModel.HasSameAuthorization(previous, current)) { return; }
            await ClearSearchAsync().ConfigureAwait(true);
            // 权限撤回必须立即卸载旧视图，不能由未保存提示取消；其他用户的权限事件不打断当前编辑。
            _backStack.Clear();
            NavigateCore(current is null ? "PG-002" : InstanceHome(), false);
            ShellStatus.Message = _services.Localization.Get("desktop.authorization-changed");
            ShellStatus.Severity = InfoBarSeverity.Warning;
            ShellStatus.IsOpen = true;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (ServitiumApiException exception)
        {
            ShowError(exception);
            if (exception.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden)
            {
                _services.Session.ClearAuthenticationState();
                _backStack.Clear();
                NavigateCore("PG-002", false);
            }
        }
        catch (Exception exception) when (exception is System.Net.Http.HttpRequestException or System.IO.IOException)
        {
            ShowError(exception);
        }
    }
}
