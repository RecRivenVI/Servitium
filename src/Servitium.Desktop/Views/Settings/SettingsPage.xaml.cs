using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Servitium.Desktop.AppModel;
using Servitium.Desktop.Services;
using Servitium.Desktop.Shell;
using Servitium.Desktop.ViewModels.Settings;
namespace Servitium.Desktop.Views.Settings;

internal sealed partial class SettingsPage : Page, INavigationGuard
{
    private readonly AppServices _services;
    private readonly IPageNavigator _navigator;
    private string? _category;
    private bool _synchronizing;
    private bool _disposed;
    public SettingsNavigationViewModel ViewModel { get; }
    internal SettingsPage(AppServices services, IPageNavigator navigator, bool account = false)
    {
        _services = services; _navigator = navigator;
        ViewModel = new(services.Localization, services.Session.Principal?.User.Role is "Owner" or "Administrator", services.Profiles.Active.Name);
        InitializeComponent(); Unloaded += (_, _) => Dispose();
        OpenCategory(account ? "account" : "appearance");
        services.Settings.Changed += OnSettingsChanged;
    }
    private void OpenCategory(string category)
    {
        if (!ViewModel.CanOpen(category)) { return; }
        if (CategoryHost.Content is IDisposable previous) { previous.Dispose(); }
        _category = category;
        _synchronizing = true;
        CategoriesList.SelectedItem = CategoriesList.Items.OfType<ListViewItem>().Single(item => (item.Tag as string) == category);
        CategoryTitle.Text = ViewModel.Text("desktop.settings-category-" + category.Replace("server-", string.Empty, StringComparison.Ordinal));
        _synchronizing = false;
        CategoryHost.Content = category switch
        {
            "account" => new AccountSettingsPage(_services, _navigator),
            "connections" => new ConnectionsView(new(_services.Localization, _services.Profiles,
                address => ((IServerConnectionNavigator)_navigator).SwitchConnectionAsync(address))),
            "server-general" or "server-security" or "server-identity" or "server-about" => new ServerSettingsPage(new(_services.Localization,
                _services.Session.Client, category, _services.Session.Principal?.User.Role, _services.Interaction.ConfirmAsync,
                _services.Interaction.PickSaveFileAsync, _services.TimeProvider), category),
            _ => new LocalPreferencesPage(new(_services.Localization, _services.Settings, category, _services.Interaction.ConfirmAsync,
                ApplyPause, _services.TimeProvider, _services.Notifications.IsCurrentlyPaused()), category),
        };
        if (CategoriesSplit.DisplayMode == SplitViewDisplayMode.Overlay) { CategoriesSplit.IsPaneOpen = false; }
    }
    private void ApplyPause(int index)
    {
        _services.Notifications.ClearProcessPause();
        if (index == 3) { _services.Notifications.PauseUntilRestart(); }
        else if (index == 0) { _services.Notifications.ResumeAndFlush(); }
    }

    public Task<bool> CanNavigateAwayAsync()
    {
        return CategoryHost.Content is INavigationGuard guard ? guard.CanNavigateAwayAsync() : Task.FromResult(true);
    }

    private void OnSettingsChanged(object? sender, EventArgs args)
    {
        _ = DispatcherQueue.TryEnqueue(() => { if (!_disposed) { ViewModel.ConnectionName = _services.Profiles.Active.Name; } });
    }
    private void Dispose()
    {
        if (_disposed) { return; }
        _disposed = true;
        _services.Settings.Changed -= OnSettingsChanged;
        if (CategoryHost.Content is IDisposable page) { page.Dispose(); }
    }

    private async void OnCategorySelected(object sender, SelectionChangedEventArgs args)
    {
        if (_synchronizing || !(CategoriesList.SelectedItem is ListViewItem { Tag: var tag }) || !(tag is string category) || category == _category)
        {
            return;
        }
        _synchronizing = true;
        bool flag = await CanNavigateAwayAsync().ConfigureAwait(true);
        if (!flag)
        {
            CategoriesList.SelectedItem = CategoriesList.Items.OfType<ListViewItem>().Single((ListViewItem item) => item.Tag as string == _category);
        }
        _synchronizing = false;
        if (flag)
        {
            OpenCategory(category);
        }
    }

    private void OnShowCategories(object sender, RoutedEventArgs args)
    {
        CategoriesSplit.IsPaneOpen = !CategoriesSplit.IsPaneOpen;
    }

    private void OnLayoutSizeChanged(object sender, SizeChangedEventArgs args)
    {
        bool flag = WindowStateService.Breakpoint(args.NewSize.Width) != "expanded";
        CategoriesSplit.DisplayMode = ((!flag) ? SplitViewDisplayMode.Inline : SplitViewDisplayMode.Overlay);
        CategoriesSplit.OpenPaneLength = args.NewSize.Width * (flag ? 0.7 : 0.25);
        CategoriesSplit.IsPaneOpen = !flag;
        CategoriesButton.Visibility = ((!flag) ? Visibility.Collapsed : Visibility.Visible);
    }
}
