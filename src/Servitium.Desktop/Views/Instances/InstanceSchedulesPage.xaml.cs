using System;
using System.Threading.Tasks;

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

using Servitium.Client.Realtime;
using Servitium.Desktop.AppModel;
using Servitium.Desktop.Services;
using Servitium.Desktop.Shell;
using Servitium.Desktop.ViewModels.Instances;

namespace Servitium.Desktop.Views.Instances;

internal sealed partial class InstanceSchedulesPage : UserControl, INavigationGuard, IDisposable
{
    private bool _selecting;
    private readonly AppServices _services;
    private bool _loaded;
    private bool _disposed;

    public InstanceSchedulesViewModel ViewModel { get; }
    protected override AutomationPeer OnCreateAutomationPeer() { return new FrameworkElementAutomationPeer(this); }
    internal InstanceSchedulesPage(AppServices services)
    {
        _services = services;
        ViewModel = new(services.Localization, services.Session.Client, services.Session.SelectedInstance ?? throw new InvalidOperationException("An instance must be selected."),
            services.Interaction.ConfirmAsync, services.Session.SelectedNode?.Status == "Online");
        InitializeComponent(); Loaded += OnLoaded; Unloaded += OnUnloaded;
    }
    private async void OnLoaded(object sender, RoutedEventArgs args) { _loaded = true; _services.Session.Client.Events.EventReceived += OnRealtimeEvent; await ViewModel.LoadAsync().ConfigureAwait(true); }
    private void OnUnloaded(object sender, RoutedEventArgs args) { Dispose(); }
    public Task<bool> CanNavigateAwayAsync() { return ViewModel.CanLeaveAsync(); }

    private void OnRealtimeEvent(object? sender, RealtimeEventArgs args) { _ = DispatcherQueue.TryEnqueue(async () => { if (!_disposed) { ViewModel.ApplyRuntimeEvent(args.Value); await ViewModel.RefreshRunEventAsync(args.Value).ConfigureAwait(true); } }); }
    public void Dispose()
    {
        if (_disposed) { return; }
        _disposed = true;
        _services.Session.Client.Events.EventReceived -= OnRealtimeEvent; ViewModel.Dispose(); GC.SuppressFinalize(this);
    }
    private async void OnTaskSelected(object sender, SelectionChangedEventArgs args)
    {
        if (!_loaded || _selecting || TaskList.SelectedItem is not ScheduleRow row) { return; }
        _selecting = true;
        try { await ViewModel.OpenEditorAsync(row).ConfigureAwait(true); TaskList.SelectedItem = ViewModel.SelectedTask; }
        finally { _selecting = false; }
    }
    private async void OnTaskToggled(object sender, RoutedEventArgs args)
    {
        if (!_loaded || _disposed || sender is not ToggleSwitch { DataContext: ScheduleRow row } toggle) { return; }
        await ViewModel.SetEnabledAsync(row, toggle.IsOn).ConfigureAwait(true);
    }
    private async void OnRunNow(object sender, RoutedEventArgs args) { if (sender is Button { DataContext: ScheduleRow row }) { await ViewModel.RunNowAsync(row).ConfigureAwait(true); } }
    private void OnActionKindChanged(object sender, SelectionChangedEventArgs args)
    {
        if (sender is ComboBox { DataContext: ScheduleActionEditor action, SelectedValue: string kind }) { action.Kind = kind; }
    }
    private void OnSaveInvoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args) { if (ViewModel.CanSave) { ViewModel.SaveCommand.Execute(null); args.Handled = true; } }
    private void OnPaneClosing(SplitView sender, SplitViewPaneClosingEventArgs args)
    {
        if (!ViewModel.IsEditorOpen) { return; }
        args.Cancel = true;
        _ = DispatcherQueue.TryEnqueue(() => { if (!_disposed && ViewModel.CanCloseEditor) { ViewModel.CloseEditorCommand.Execute(null); } });
    }
    private void OnPaneOpened(SplitView sender, object args) { _ = TaskNameInput.Focus(FocusState.Keyboard); }
    private void OnLayoutSizeChanged(object sender, SizeChangedEventArgs args)
    {
        bool compact = WindowStateService.Breakpoint(args.NewSize.Width) != "expanded";
        EditorSplit.OpenPaneLength = args.NewSize.Width * (compact ? 1 : 0.45);
    }
}
