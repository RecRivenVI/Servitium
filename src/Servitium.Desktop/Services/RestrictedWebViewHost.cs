using System;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;

using Servitium.Client.Realtime;
using Servitium.Contracts.Instances;
using Servitium.Desktop.AppModel;

namespace Servitium.Desktop.Services;

internal sealed partial class RestrictedWebViewHost : Grid, IDisposable
{
    private const string VirtualHost = "servitium.local";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly AppServices _services;
    private readonly string _kind;
    private readonly InstanceDto? _instance;
    private readonly bool _ownsTerminal;
    private readonly WebView2 _webView = new();
    private readonly TextBlock _error = new() { Visibility = Visibility.Collapsed, TextWrapping = TextWrapping.Wrap };
    private TerminalConnection? _terminal;
    private bool _ready;
    private bool _disposed;

    internal RestrictedWebViewHost(
        AppServices services,
        string kind,
        InstanceDto? instance = null,
        TerminalConnection? terminal = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(kind);
        if (kind is not ("terminal" or "editor"))
        {
            throw new ArgumentOutOfRangeException(nameof(kind));
        }

        _services = services;
        _kind = kind;
        _instance = instance;
        _terminal = terminal;
        _ownsTerminal = terminal is not null;
        MinHeight = kind == "terminal" ? 360 : 440;
        Children.Add(_webView);
        Children.Add(_error);
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    internal string EditorContent { get; private set; } = string.Empty;

    internal bool IsEditorDirty { get; private set; }

    internal event EventHandler? EditorSaveRequested;

    internal event EventHandler? EditorDirtyChanged;

    internal async Task LoadEditorAsync(string content, string language = "plaintext", bool readOnly = false)
    {
        EditorContent = content ?? string.Empty;
        if (_ready && _kind == "editor")
        {
            await PostAsync("editor.load", new { content = EditorContent, language, readOnly }).ConfigureAwait(true);
        }
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "A WebView2 runtime or local asset failure must remain contained in the page and be recorded.")]
    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            await InitializeAsync().ConfigureAwait(true);
        }
        catch (Exception exception)
        {
            _webView.Visibility = Visibility.Collapsed;
            _error.Text = _services.Localization.Get("error.webview-unavailable");
            _error.Visibility = Visibility.Visible;
            await _services.Log.WriteAsync("error", "webview.initialization-failed", exception.GetType().Name).ConfigureAwait(true);
        }
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "The WebView unload boundary must not crash Desktop while the Server disconnects.")]
    private async void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (_kind != "terminal")
        {
            return;
        }

        try
        {
            if (_terminal is not null)
            {
                _terminal.OutputReceived -= OnTerminalOutput;
                await _terminal.DetachAsync(CancellationToken.None).ConfigureAwait(true);
                if (_ownsTerminal)
                {
                    _terminal.Dispose();
                }

                _terminal = null;
            }
        }
        catch
        {
            // 卸载时连接可能已经关闭；此处仅确保 UI 生命周期不阻塞应用退出。
        }
    }

    private async Task InitializeAsync()
    {
        if (_ready || _disposed)
        {
            return;
        }

        await _webView.EnsureCoreWebView2Async().AsTask().ConfigureAwait(true);
        CoreWebView2 core = _webView.CoreWebView2;
        string assets = Path.Combine(AppContext.BaseDirectory, "WebAssets");
        core.SetVirtualHostNameToFolderMapping(VirtualHost, assets, CoreWebView2HostResourceAccessKind.DenyCors);
        core.Settings.AreHostObjectsAllowed = false;
        core.Settings.IsWebMessageEnabled = true;
        core.Settings.AreDefaultContextMenusEnabled = false;
        core.Settings.IsStatusBarEnabled = false;
#if !DEBUG
        core.Settings.AreDevToolsEnabled = false;
#endif
        core.NavigationStarting += OnNavigationStarting;
        core.NewWindowRequested += OnNewWindowRequested;
        core.WebMessageReceived += OnWebMessageReceived;
        TaskCompletionSource loaded = new(TaskCreationOptions.RunContinuationsAsynchronously);
        void Completed(object? _, CoreWebView2NavigationCompletedEventArgs args)
        {
            core.NavigationCompleted -= Completed;
            if (args.IsSuccess)
            {
                loaded.TrySetResult();
            }
            else
            {
                loaded.TrySetException(new InvalidOperationException("WebView2 local resource navigation failed."));
            }
        }

        core.NavigationCompleted += Completed;
        core.Navigate($"https://{VirtualHost}/{_kind}.html");
        await loaded.Task.ConfigureAwait(true);
        _ready = true;
        await PostAsync("theme.changed", new { theme = ActualTheme == ElementTheme.Dark ? "dark" : "light" }).ConfigureAwait(true);
        if (_kind == "terminal")
        {
            await PostAsync("terminal.settings", new { fontSize = _services.Settings.Current.TerminalFontSize }).ConfigureAwait(true);
        }
        if (_kind == "editor")
        {
            await LoadEditorAsync(EditorContent).ConfigureAwait(true);
        }
        else if ((_instance ?? _services.Session.SelectedInstance) is InstanceDto instance)
        {
            _terminal ??= _services.Session.Client.Terminal;
            _terminal.OutputReceived += OnTerminalOutput;
            _ = await _terminal.AttachAsync(instance.Id, 164, 40, CancellationToken.None).ConfigureAwait(true);
        }
    }

    private void OnNavigationStarting(CoreWebView2 sender, CoreWebView2NavigationStartingEventArgs args)
    {
        args.Cancel = !Uri.TryCreate(args.Uri, UriKind.Absolute, out Uri? uri) ||
            uri.Scheme != Uri.UriSchemeHttps ||
            !string.Equals(uri.Host, VirtualHost, StringComparison.OrdinalIgnoreCase);
    }

    private static void OnNewWindowRequested(CoreWebView2 sender, CoreWebView2NewWindowRequestedEventArgs args)
    {
        args.Handled = true;
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "Malformed or stale WebView messages are contained at the trust boundary and reported through the page status.")]
    private async void OnWebMessageReceived(CoreWebView2 sender, CoreWebView2WebMessageReceivedEventArgs args)
    {
        try
        {
            using var document = JsonDocument.Parse(args.WebMessageAsJson);
            JsonElement root = document.RootElement;
            string? type = root.TryGetProperty("type", out JsonElement typeValue) ? typeValue.GetString() : null;
            if (type == "terminal.input" && root.TryGetProperty("text", out JsonElement text))
            {
                await RequireTerminal().SendInputAsync(text.GetString() ?? string.Empty, CancellationToken.None).ConfigureAwait(true);
            }
            else if (type == "terminal.resize" &&
                root.TryGetProperty("columns", out JsonElement columns) &&
                root.TryGetProperty("rows", out JsonElement rows))
            {
                await RequireTerminal().ResizeAsync(columns.GetInt32(), rows.GetInt32(), CancellationToken.None).ConfigureAwait(true);
            }
            else if (type is "editor.dirty" or "editor.save" && root.TryGetProperty("content", out JsonElement content))
            {
                EditorContent = content.GetString() ?? string.Empty;
                bool nextDirty = root.TryGetProperty("dirty", out JsonElement dirty) && dirty.ValueKind == JsonValueKind.True;
                if (nextDirty != IsEditorDirty)
                {
                    IsEditorDirty = nextDirty;
                    EditorDirtyChanged?.Invoke(this, EventArgs.Empty);
                }

                if (type == "editor.save")
                {
                    EditorSaveRequested?.Invoke(this, EventArgs.Empty);
                }
            }
            else if (type == "terminal.export" && root.TryGetProperty("content", out JsonElement terminalContent))
            {
                await ExportTerminalAsync(terminalContent.GetString() ?? string.Empty).ConfigureAwait(true);
            }
        }
        catch
        {
            // WebView 消息是受限输入；无效消息不得越过白名单通道或导致进程终止。
        }
    }

    private void OnTerminalOutput(object? sender, TerminalOutputEventArgs args)
    {
        _ = DispatcherQueue.TryEnqueue(async () =>
            await PostAsync("terminal.output", new
            {
                text = args.Value.Text,
                sequence = args.Value.Sequence,
                snapshot = args.Value.Snapshot,
                truncated = args.Value.Truncated,
            }).ConfigureAwait(true));
    }

    private Task PostAsync(string type, object payload)
    {
        string message = JsonSerializer.Serialize(new { type, payload }, JsonOptions);
        _webView.CoreWebView2.PostWebMessageAsJson(message);
        return Task.CompletedTask;
    }

    private TerminalConnection RequireTerminal()
    {
        return _terminal ?? throw new InvalidOperationException("The terminal is not connected.");
    }

    private async Task ExportTerminalAsync(string content)
    {
        const int MaximumExportCharacters = 8 * 1024 * 1024;
        if (content.Length > MaximumExportCharacters)
        {
            throw new InvalidOperationException(_services.Localization.Get("error.file-too-large"));
        }

        string suggested = "servitium-terminal-" + _services.TimeProvider.GetLocalNow().ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture);
        string? path = await _services.Interaction.PickSaveFileAsync(suggested, ".log").ConfigureAwait(true);
        if (path is not null)
        {
            await File.WriteAllTextAsync(path, content, new UTF8Encoding(false), CancellationToken.None).ConfigureAwait(true);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Loaded -= OnLoaded;
        Unloaded -= OnUnloaded;
        if (_webView.CoreWebView2 is CoreWebView2 core)
        {
            core.NavigationStarting -= OnNavigationStarting;
            core.NewWindowRequested -= OnNewWindowRequested;
            core.WebMessageReceived -= OnWebMessageReceived;
        }

        if (_terminal is not null)
        {
            _terminal.OutputReceived -= OnTerminalOutput;
            if (_ownsTerminal)
            {
                _terminal.Dispose();
            }

            _terminal = null;
        }
        _webView.Close();
    }
}
