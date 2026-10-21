using System;
using System.ComponentModel;
using System.Threading.Tasks;

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

using Servitium.Client.Realtime;
using Servitium.Desktop.AppModel;
using Servitium.Desktop.Models;
using Servitium.Desktop.Services;
using Servitium.Desktop.Shell;
using Servitium.Desktop.ViewModels.Instances;

namespace Servitium.Desktop.Views.Instances;

internal sealed partial class InstanceConfigurationPage : UserControl, INavigationGuard, IDisposable
{
    private readonly AppServices _services;
    private RestrictedWebViewHost? _editor;
    private bool _synchronizing;
    private bool _disposed;
    public InstanceConfigurationViewModel ViewModel { get; }
    protected override AutomationPeer OnCreateAutomationPeer() { return new FrameworkElementAutomationPeer(this); }
    internal InstanceConfigurationPage(AppServices services, IPageNavigator navigator)
    {
        _services = services;
        ViewModel = new(services.Localization, services.Session.Client, services.Session.SelectedInstance ?? throw new InvalidOperationException("An instance must be selected."),
            services.Interaction.ConfirmAsync, path => { services.Session.PendingFilePath = path; navigator.Navigate("PG-013"); }, services.Session.SelectedNode?.Status == "Online");
        InitializeComponent(); Loaded += OnLoaded; Unloaded += OnUnloaded;
    }
    private async void OnLoaded(object sender, RoutedEventArgs args)
    {
        ViewModel.EditorLoaded += OnEditorLoaded; ViewModel.PropertyChanged += OnModelChanged;
        _services.Session.Client.Events.EventReceived += OnRealtimeEvent;
        await ViewModel.LoadAsync().ConfigureAwait(true);
    }
    private void OnUnloaded(object sender, RoutedEventArgs args) { Dispose(); }
    public Task<bool> CanNavigateAwayAsync() { return ViewModel.CanLeaveAsync(); }
    public void Dispose()
    {
        if (_disposed) { return; }
        _disposed = true;
        _services.Session.Client.Events.EventReceived -= OnRealtimeEvent;
        ViewModel.EditorLoaded -= OnEditorLoaded; ViewModel.PropertyChanged -= OnModelChanged;
        DisposeEditor(); ViewModel.Dispose(); GC.SuppressFinalize(this);
    }
    private void OnModelChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(ViewModel.IsRaw))
        {
            if (!ViewModel.IsRaw) { DisposeEditor(); }
            _synchronizing = true;
            ViewSelector.SelectedItem = ViewModel.IsRaw ? RawItem : StructuredItem;
            _synchronizing = false;
        }
        if (args.PropertyName == nameof(ViewModel.IsReadOnly) && ViewModel.IsRaw && _editor is not null) { OnEditorLoaded(this, EventArgs.Empty); }
    }
    private async void OnEditorLoaded(object? sender, EventArgs args)
    {
        if (_disposed || !ViewModel.IsRaw) { return; }
        _editor ??= new(_services, "editor") { MinHeight = 0 };
        _editor.EditorContentChanged -= OnEditorContentChanged; _editor.EditorContentChanged += OnEditorContentChanged;
        _editor.EditorSaveRequested -= OnEditorSaveRequested; _editor.EditorSaveRequested += OnEditorSaveRequested;
        EditorHost.Content = _editor;
        try { await _editor.LoadEditorAsync(ViewModel.RawText, ViewModel.EditorLanguage, ViewModel.IsReadOnly).ConfigureAwait(true); }
        catch (Exception exception) when (exception is System.Runtime.InteropServices.COMException or InvalidOperationException) { ViewModel.ReportInteractionError(exception); }
    }
    private void DisposeEditor()
    {
        if (_editor is null) { return; }
        _editor.EditorContentChanged -= OnEditorContentChanged; _editor.EditorSaveRequested -= OnEditorSaveRequested;
        EditorHost.Content = null; _editor.Dispose(); _editor = null;
    }
    private void OnEditorContentChanged(object? sender, EventArgs args) { if (_editor is not null) { ViewModel.RawText = _editor.EditorContent; } }
    private void OnEditorSaveRequested(object? sender, EventArgs args) { if (ViewModel.CanSave) { ViewModel.SaveCommand.Execute(null); } }
    private void OnRealtimeEvent(object? sender, RealtimeEventArgs args) { _ = DispatcherQueue.TryEnqueue(() => { if (!_disposed) { ViewModel.ApplyRuntimeEvent(args.Value); } }); }
    private async void OnFileSelected(object sender, SelectionChangedEventArgs args)
    {
        if (_synchronizing || FilesList.SelectedItem is not ConfigurationFileRow file) { return; }
        _synchronizing = true;
        try { await ViewModel.OpenAsync(file).ConfigureAwait(true); FilesList.SelectedItem = ViewModel.SelectedFile; if (FilesSplit.DisplayMode == SplitViewDisplayMode.Overlay) { FilesSplit.IsPaneOpen = false; } }
        finally { _synchronizing = false; }
    }
    private async void OnViewSelected(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        if (_synchronizing) { return; }
        _synchronizing = true;
        try { await ViewModel.SwitchViewAsync(sender.SelectedItem == RawItem).ConfigureAwait(true); sender.SelectedItem = ViewModel.IsRaw ? RawItem : StructuredItem; }
        finally { _synchronizing = false; }
    }
    private async void OnBackupsClick(object sender, RoutedEventArgs args) { await ViewModel.ViewBackupsCommand.ExecuteAsync(null).ConfigureAwait(true); }
    private async void OnRestoreBackup(object sender, RoutedEventArgs args) { BackupsFlyout.Hide(); await ViewModel.RestoreBackupCommand.ExecuteAsync(null).ConfigureAwait(true); }
    private void OnSaveInvoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args) { if (ViewModel.CanSave) { ViewModel.SaveCommand.Execute(null); args.Handled = true; } }
    private void OnShowFiles(object sender, RoutedEventArgs args) { FilesSplit.IsPaneOpen = !FilesSplit.IsPaneOpen; }
    private void OnLayoutSizeChanged(object sender, SizeChangedEventArgs args)
    {
        bool compact = WindowStateService.Breakpoint(args.NewSize.Width) != "expanded";
        FilesSplit.DisplayMode = compact ? SplitViewDisplayMode.Overlay : SplitViewDisplayMode.Inline;
        FilesSplit.OpenPaneLength = args.NewSize.Width * (compact ? 0.7 : 0.25);
        FilesSplit.IsPaneOpen = !compact;
        FilesButton.Visibility = compact ? Visibility.Visible : Visibility.Collapsed;
    }
}
