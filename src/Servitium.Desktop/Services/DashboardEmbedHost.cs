using System;
using System.Diagnostics.CodeAnalysis;
using System.Threading.Tasks;

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;

using Servitium.Desktop.AppModel;

namespace Servitium.Desktop.Services;

/// <summary>只允许在已批准来源内导航的仪表盘嵌入卡片。</summary>
internal sealed partial class DashboardEmbedHost : Grid, IDisposable
{
    private readonly AppServices _services;
    private readonly Uri _source;
    private readonly string _origin;
    private readonly WebView2 _webView = new();
    private readonly TextBlock _error = new() { Visibility = Visibility.Collapsed, TextWrapping = TextWrapping.Wrap };
    private bool _disposed;

    internal DashboardEmbedHost(AppServices services, Uri source)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(source);
        if (source.Scheme != Uri.UriSchemeHttps || source.UserInfo.Length != 0)
        {
            throw new ArgumentException("The dashboard embed URI must use HTTPS without user information.", nameof(source));
        }

        _services = services;
        _source = source;
        _origin = source.GetLeftPart(UriPartial.Authority).TrimEnd('/');
        MinHeight = 240;
        Children.Add(_webView);
        Children.Add(_error);
        Loaded += OnLoaded;
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "A remote embed or WebView2 runtime failure is contained inside its card.")]
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
            await _services.Log.WriteAsync("error", "dashboard.embed-failed", exception.GetType().Name).ConfigureAwait(true);
        }
    }

    private async Task InitializeAsync()
    {
        if (_disposed || _webView.CoreWebView2 is not null)
        {
            return;
        }

        await _webView.EnsureCoreWebView2Async().AsTask().ConfigureAwait(true);
        CoreWebView2 core = _webView.CoreWebView2
            ?? throw new InvalidOperationException("WebView2 initialization did not create a CoreWebView2 instance.");
        core.Settings.AreHostObjectsAllowed = false;
        core.Settings.IsWebMessageEnabled = false;
        core.Settings.AreDefaultContextMenusEnabled = false;
        core.Settings.IsStatusBarEnabled = false;
        core.Settings.IsPasswordAutosaveEnabled = false;
        core.Settings.IsGeneralAutofillEnabled = false;
#if !DEBUG
        core.Settings.AreDevToolsEnabled = false;
#endif
        core.NavigationStarting += OnNavigationStarting;
        core.NewWindowRequested += OnNewWindowRequested;
        core.PermissionRequested += OnPermissionRequested;
        core.Navigate(_source.AbsoluteUri);
    }

    private void OnNavigationStarting(CoreWebView2 sender, CoreWebView2NavigationStartingEventArgs args)
    {
        args.Cancel = !Uri.TryCreate(args.Uri, UriKind.Absolute, out Uri? target) ||
            target.Scheme != Uri.UriSchemeHttps ||
            !string.Equals(target.GetLeftPart(UriPartial.Authority).TrimEnd('/'), _origin, StringComparison.OrdinalIgnoreCase);
    }

    private static void OnNewWindowRequested(CoreWebView2 sender, CoreWebView2NewWindowRequestedEventArgs args)
    {
        args.Handled = true;
    }

    private static void OnPermissionRequested(CoreWebView2 sender, CoreWebView2PermissionRequestedEventArgs args)
    {
        args.State = CoreWebView2PermissionState.Deny;
        args.Handled = true;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Loaded -= OnLoaded;
        if (_webView.CoreWebView2 is CoreWebView2 core)
        {
            core.NavigationStarting -= OnNavigationStarting;
            core.NewWindowRequested -= OnNewWindowRequested;
            core.PermissionRequested -= OnPermissionRequested;
        }

        _webView.Close();
    }
}
