using System;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Automation.Peers;

using Servitium.Client.Realtime;
using Servitium.Contracts.Events;
using Servitium.Contracts.Instances;
using Servitium.Desktop.AppModel;
using Servitium.Contracts.Nodes;
using Servitium.Desktop.Services;
using Servitium.Desktop.Shell;
using Servitium.Desktop.ViewModels.Instances;

namespace Servitium.Desktop.Views.Instances;

internal sealed partial class InstanceConsolePage : UserControl, IDisposable
{
    private bool _inspectorDialogOpen;
    private bool _compact;
    private readonly AppServices _services;
    private readonly IPageNavigator _navigator;
    private readonly InstanceDto _instance;
    private bool _disposed;
    private RestrictedWebViewHost? _terminal;
    private readonly DispatcherTimer _clock = new() { Interval = TimeSpan.FromSeconds(30) };
    public InstanceConsoleViewModel ViewModel { get; }
    protected override AutomationPeer OnCreateAutomationPeer() { return new FrameworkElementAutomationPeer(this); }
    internal InstanceConsolePage(AppServices services, IPageNavigator navigator)
    {
        _services = services; _navigator = navigator;
        _instance = services.Session.SelectedInstance ?? throw new InvalidOperationException("An instance must be selected.");
        NodeDto? node = services.Session.SelectedNode?.Id == _instance.NodeId ? services.Session.SelectedNode : services.Session.Nodes.FirstOrDefault(value => value.Id == _instance.NodeId);
        bool reachable = node?.Status == "Online" && _instance.Status != "Unreachable";
        ViewModel = new(services.Localization, services.Session.Client, _instance, node, reachable,
            services.Session.Principal?.User.Role is "Owner" or "Administrator", services.Session.Channels)
        {
            Command = services.Session.PendingConsoleCommand ?? string.Empty
        };
        services.Session.PendingConsoleCommand = null;
        InitializeComponent(); Loaded += OnLoaded; Unloaded += OnUnloaded;
    }
    private async void OnLoaded(object sender, RoutedEventArgs args)
    {
        ViewModel.PropertyChanged += OnModelChanged;
        _services.Session.Client.Events.EventReceived += OnEvent;
        _clock.Tick += OnTick; _clock.Start();
        await ViewModel.LoadAsync().ConfigureAwait(true);
    }
    private void OnTick(object? sender, object args) { ViewModel.Tick(); }
    private void OnUnloaded(object sender, RoutedEventArgs args)
    {
        Dispose();
    }
    public void Dispose()
    {
        if (_disposed) { return; }
        _disposed = true;
        _clock.Stop(); _clock.Tick -= OnTick;
        _services.Session.Client.Events.EventReceived -= OnEvent;
        ViewModel.PropertyChanged -= OnModelChanged;
        _terminal?.Dispose(); _terminal = null; ViewModel.Dispose();
        GC.SuppressFinalize(this);
    }
    private void OnModelChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName != nameof(ViewModel.IsRunning)) { return; }
        if (ViewModel.IsRunning && _terminal is null)
        {
            _terminal = new(_services, "terminal", _instance, _services.Session.Client.CreateTerminalConnection()) { MinHeight = 0 };
            TerminalHost.Content = _terminal;
        }
        else if (!ViewModel.IsRunning && _terminal is not null)
        {
            TerminalHost.Content = null; _terminal.Dispose(); _terminal = null;
        }
    }
    private void OnEvent(object? sender, RealtimeEventArgs args)
    {
        _ = DispatcherQueue.TryEnqueue(async () =>
        {
            if (!_disposed && ViewModel.ApplyEvent(args.Value)) { await ViewModel.LoadAsync().ConfigureAwait(true); }
        });
    }
    private void OnSendInvoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (CommandBox.FocusState == FocusState.Unfocused || !ViewModel.CanSend) { return; }
        ViewModel.SendCommand.Execute(null); args.Handled = true;
    }
    private void OnShortcutClick(object sender, RoutedEventArgs args)
    {
        if (sender is Button { DataContext: ShortcutCommandRequest shortcut } && ViewModel.SendShortcutCommand.CanExecute(shortcut)) { ViewModel.SendShortcutCommand.Execute(shortcut); }
    }
    private void OnWorkingDirectoryClick(object sender, RoutedEventArgs args) { _navigator.Navigate("PG-013"); }
    private async void OnInspectorClick(object sender, RoutedEventArgs args)
    {
        if (_inspectorDialogOpen) { return; }
        if (!_compact)
        {
            Inspector.Visibility = Inspector.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
            InspectorColumn.Width = new GridLength(Inspector.Visibility == Visibility.Visible ? 1 : 0, GridUnitType.Star);
            return;
        }
        object content = Inspector.Content;
        Inspector.Content = null;
        ContentDialog dialog = new() { Title = ViewModel.Text("desktop.console-inspector"), Content = content, XamlRoot = XamlRoot, CloseButtonText = ViewModel.Text("common.close") };
        _inspectorDialogOpen = true;
        try { _ = await dialog.ShowAsync(); }
        finally { dialog.Content = null; Inspector.Content = content; _inspectorDialogOpen = false; }
    }
    private void OnLayoutChanged(object sender, SizeChangedEventArgs args)
    {
        _compact = WindowStateService.Breakpoint(args.NewSize.Width) == "compact";
        Inspector.Visibility = _compact ? Visibility.Collapsed : Visibility.Visible;
        InspectorColumn.Width = new GridLength(_compact ? 0 : 1, GridUnitType.Star);
    }
}
