using System;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

using Servitium.Client.Realtime;
using Servitium.Contracts.Events;
using Servitium.Desktop.AppModel;
using Servitium.Desktop.Services;
using Servitium.Desktop.Shell;
using Servitium.Desktop.ViewModels.Instances;

using Windows.ApplicationModel.DataTransfer;

namespace Servitium.Desktop.Views.Instances;

internal sealed partial class InstanceFilesPage : UserControl, INavigationGuard, IDisposable
{
    private bool _synchronizingTabs;
    private readonly AppServices _services;
    private RestrictedWebViewHost? _editor;
    private bool _disposed;

    public InstanceFilesViewModel ViewModel { get; }
    protected override AutomationPeer OnCreateAutomationPeer() { return new FrameworkElementAutomationPeer(this); }
    internal InstanceFilesPage(AppServices services)
    {
        _services = services;
        ViewModel = new(services.Localization, services.Session.Client, services.Session.SelectedInstance ?? throw new InvalidOperationException("An instance must be selected."),
            services.Interaction.PromptAsync, services.Interaction.ConfirmAsync,
            () => services.Interaction.PickOpenFileAsync("*"), services.Interaction.PickSaveFileAsync, services.Interaction.ShowImageAsync,
            services.Session.SelectedNode?.Status == "Online");
        InitializeComponent(); Loaded += OnLoaded; Unloaded += OnUnloaded;
    }
    private async void OnLoaded(object sender, RoutedEventArgs args)
    {
        ViewModel.EditorLoaded += OnEditorLoaded;
        ViewModel.PropertyChanged += OnModelChanged;
        _services.Session.Client.Events.EventReceived += OnRealtimeEvent;
        string? pending = _services.Session.PendingFilePath; _services.Session.PendingFilePath = null;
        string? directory = pending is null ? null : System.IO.Path.GetDirectoryName(pending)?.Replace('\\', '/') ?? string.Empty;
        await ViewModel.LoadAsync(directory).ConfigureAwait(true);
        if (pending is not null) { FileList.SelectedItem = ViewModel.Files.FirstOrDefault(row => row.Value.Path == pending); }
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
        if (args.PropertyName == nameof(ViewModel.IsEditing) && !ViewModel.IsEditing) { DisposeEditor(); }
    }
    private async void OnEditorLoaded(object? sender, EventArgs args)
    {
        if (_disposed) { return; }
        _editor ??= new(_services, "editor") { MinHeight = 0 };
        _editor.EditorContentChanged -= OnEditorContentChanged; _editor.EditorContentChanged += OnEditorContentChanged;
        _editor.EditorSaveRequested -= OnEditorSaveRequested; _editor.EditorSaveRequested += OnEditorSaveRequested;
        EditorHost.Content = _editor;
        try { await _editor.LoadEditorAsync(ViewModel.EditorText, ViewModel.EditorLanguage).ConfigureAwait(true); }
        catch (Exception exception) when (exception is System.Runtime.InteropServices.COMException or InvalidOperationException) { ViewModel.ReportInteractionError(exception); }
    }
    private void DisposeEditor()
    {
        if (_editor is null) { return; }
        _editor.EditorContentChanged -= OnEditorContentChanged; _editor.EditorSaveRequested -= OnEditorSaveRequested;
        EditorHost.Content = null; _editor.Dispose(); _editor = null;
    }
    private void OnEditorContentChanged(object? sender, EventArgs args) { if (_editor is not null) { ViewModel.EditorText = _editor.EditorContent; } }
    private void OnEditorSaveRequested(object? sender, EventArgs args) { if (ViewModel.CanSave) { ViewModel.SaveTextCommand.Execute(null); } }
    private void OnRealtimeEvent(object? sender, RealtimeEventArgs args)
    {
        _ = DispatcherQueue.TryEnqueue(() => { if (!_disposed) { ViewModel.ApplyRuntimeEvent(args.Value); } });
    }

    private async Task OpenSelectedAsync()
    {
        if (!ViewModel.CanUseOne) { return; }
        FileRow file = ViewModel.SelectedFiles[0];
        if (file.Value.IsDirectory) { await ViewModel.NavigateAsync(file.Value.Path).ConfigureAwait(true); }
        else if (ViewModel.CanPreviewImage) { await ViewModel.PreviewCommand.ExecuteAsync(null).ConfigureAwait(true); }
        else { await ViewModel.EditTextCommand.ExecuteAsync(null).ConfigureAwait(true); }
    }
    private void OnFileSelectionChanged(object sender, SelectionChangedEventArgs args) { ViewModel.Select([.. FileList.SelectedItems.OfType<FileRow>()]); }
    private async void OnFileDoubleTapped(object sender, DoubleTappedRoutedEventArgs args) { await OpenSelectedAsync().ConfigureAwait(true); }
    private async void OnBreadcrumbClick(BreadcrumbBar sender, BreadcrumbBarItemClickedEventArgs args)
    {
        if (args.Item is FileBreadcrumb value) { await ViewModel.NavigateAsync(value.Path).ConfigureAwait(true); }
    }
    private async void OnTabSelectionChanged(object sender, SelectionChangedEventArgs args)
    {
        if (_synchronizingTabs) { return; }
        FileDirectoryTab? tab = DirectoryTabs.SelectedItem as FileDirectoryTab ?? (DirectoryTabs.SelectedItem as TabViewItem)?.DataContext as FileDirectoryTab;
        if (tab is null) { return; }
        _synchronizingTabs = true;
        try { await ViewModel.ChangeTabAsync(tab).ConfigureAwait(true); DirectoryTabs.SelectedItem = ViewModel.SelectedTab; }
        finally { _synchronizingTabs = false; }
    }
    private void OnAddTabClick(TabView sender, object args) { if (ViewModel.NewTabCommand.CanExecute(null)) { ViewModel.NewTabCommand.Execute(null); } }
    private async void OnTabCloseRequested(TabView sender, TabViewTabCloseRequestedEventArgs args)
    {
        FileDirectoryTab? tab = args.Item as FileDirectoryTab ?? args.Tab.DataContext as FileDirectoryTab;
        if (tab is null || !ViewModel.CanCloseTab) { return; }
        await ViewModel.ChangeTabAsync(tab).ConfigureAwait(true);
        if (ReferenceEquals(ViewModel.SelectedTab, tab)) { await ViewModel.CloseTabCommand.ExecuteAsync(null).ConfigureAwait(true); }
    }
    private void OnSaveInvoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (ViewModel.CanSave) { ViewModel.SaveTextCommand.Execute(null); args.Handled = true; }
    }
    private void OnCloseTabInvoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (ViewModel.CanCloseTab) { ViewModel.CloseTabCommand.Execute(null); args.Handled = true; }
    }
    private async void OnOpenInvoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (ViewModel.IsEditing || FocusManager.GetFocusedElement(XamlRoot) is TextBox) { return; }
        if (ViewModel.CanUseOne) { args.Handled = true; await OpenSelectedAsync().ConfigureAwait(true); }
    }
    private void OnOperationClick(object sender, RoutedEventArgs args) { CompressFlyout.Hide(); ExtractFlyout.Hide(); DownloadUrlFlyout.Hide(); }
    private void OnLayoutChanged(object sender, SizeChangedEventArgs args) { OptionsScroll.MaxHeight = args.NewSize.Height * 0.7; }
    private void OnDragOver(object sender, DragEventArgs args)
    {
        args.AcceptedOperation = ViewModel.CanMutate && args.DataView.Contains(StandardDataFormats.StorageItems) ? DataPackageOperation.Copy : DataPackageOperation.None;
        args.Handled = true;
    }
    private async void OnDrop(object sender, DragEventArgs args)
    {
        args.Handled = true;
        if (!ViewModel.CanMutate || !args.DataView.Contains(StandardDataFormats.StorageItems)) { return; }
        try
        {
            var items = await args.DataView.GetStorageItemsAsync();
            await ViewModel.UploadPathsAsync([.. items.Select(item => item.Path)]).ConfigureAwait(true);
        }
        catch (Exception exception) when (exception is System.Runtime.InteropServices.COMException or System.IO.IOException or UnauthorizedAccessException) { ViewModel.ReportInteractionError(exception); }
    }
}
