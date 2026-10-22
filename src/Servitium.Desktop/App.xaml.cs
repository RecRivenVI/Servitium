using System;
using System.Linq;
using System.Threading.Tasks;

using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;

using Servitium.Desktop.AppModel;
using Servitium.Desktop.Models;
using Servitium.Desktop.Services;

using Windows.Globalization;

namespace Servitium.Desktop;

/// <summary>Servitium WinUI 3 Desktop 应用。</summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability", "CA1515:Consider making public types internal", Justification = "WinUI generated activation code declares the Application class public.")]
public sealed partial class App : Application, IDisposable
{
    internal static bool IsIsolatedTest => Environment.GetCommandLineArgs().Any(value => value is "--isolated-test" or "--self-test");
    private MainWindow? _window;
    private AppServices? _services;

    /// <summary>初始化应用。</summary>
    public App()
    {
        InitializeComponent();
    }

    /// <inheritdoc />
    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _ = InitializeAsync();
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "The top-level activation boundary reports startup failures through the process exit code.")]
    private async Task InitializeAsync()
    {
        try
        {
            if (await SingleInstanceService.RedirectIfSecondaryAsync().ConfigureAwait(true))
            {
                Exit();
                return;
            }

            string[] arguments = Environment.GetCommandLineArgs();
            bool selfTest = arguments.Contains("--self-test", StringComparer.Ordinal);
            bool isolatedLaunch = selfTest || arguments.Contains("--isolated-test", StringComparer.Ordinal);
            bool uiLayoutTest = arguments.Contains("--ui-layout-test", StringComparer.Ordinal);
            int pageTestSwitch = Array.IndexOf(arguments, "--ui-page-test");
            string? uiPageTest = pageTestSwitch >= 0 && pageTestSwitch + 1 < arguments.Length
                ? arguments[pageTestSwitch + 1]
                : null;
            if ((uiLayoutTest || uiPageTest is not null) && !isolatedLaunch)
            {
                throw new InvalidOperationException("Desktop UI test switches require an isolated launch.");
            }

            if (uiPageTest is not null && !UiContractCatalog.All.Any(page => string.Equals(page.Id, uiPageTest, StringComparison.Ordinal)))
            {
                throw new InvalidOperationException("The Desktop UI page test page is not present in the UI contract.");
            }

            string? testDataDirectory = DesktopSelfTestOptions.ResolveDataDirectory(
                isolatedLaunch,
                Environment.GetEnvironmentVariable("SERVITIUM_DESKTOP_TEST_DATA_ROOT"));

            _services = new AppServices(testDataDirectory);
            await _services.Settings.LoadAsync(System.Threading.CancellationToken.None).ConfigureAwait(true);
            ApplyPrimaryLanguageOverride(_services.Settings.Current.Language);
            _services.Localization.Reload(_services.Settings.Current.Language);
            SingleInstanceService.RegisterPrimaryActivation();
            SingleInstanceService.Activated += OnSecondaryActivated;
            string? serverArgument = arguments.SkipWhile(value => !string.Equals(value, "--server", StringComparison.Ordinal)).Skip(1).FirstOrDefault();
            Uri server = Uri.TryCreate(serverArgument, UriKind.Absolute, out Uri? configured)
                ? configured
                : _services.Profiles.Active.Address;
            if (configured is not null)
            {
                _ = await _services.Profiles.AddOrSelectAsync(server, System.Threading.CancellationToken.None).ConfigureAwait(true);
            }

            _window = new MainWindow(_services, server);
            _services.WindowState.Restore(_window);
            _window.ApplyCurrentResponsiveLayout();
            if (isolatedLaunch) { WindowStateService.GetAppWindow(_window).Show(false); }
            else { _window.Activate(); }
            if (selfTest)
            {
                _ = CompleteSelfTestAsync(_window);
            }
            else
            {
                await _window.InitializeSessionAsync().ConfigureAwait(true);
                if (uiPageTest is not null)
                {
                    _window.Navigate(uiPageTest, false);
                }
            }
        }
        catch (Exception exception)
        {
            WriteSelfTestFailure(exception, Environment.GetCommandLineArgs());
            Environment.ExitCode = 1;
            Dispose();
            Exit();
        }
    }

    private void OnSecondaryActivated(object? sender, AppActivationArguments args)
    {
        _window?.HandleActivation(args);
    }

    private static void ApplyPrimaryLanguageOverride(string language)
    {
        try
        {
            ApplicationLanguages.PrimaryLanguageOverride = language;
        }
        catch (InvalidOperationException)
        {
            // 未打包的 Windows App SDK 进程没有应用语言覆盖状态；本地化服务仍按设置提供文案。
        }
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "The process-level self-test boundary converts any launch or connection failure into a nonzero exit code.")]
    private async Task CompleteSelfTestAsync(MainWindow window)
    {
        try
        {
            await window.RunConnectionSelfTestAsync().ConfigureAwait(true);
        }
        catch (Exception exception)
        {
            WriteSelfTestFailure(exception, Environment.GetCommandLineArgs());
            Environment.ExitCode = 1;
        }
        finally
        {
            Dispose();
            Exit();
        }
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "诊断写入不能覆盖原始自检失败状态。")]
    private static void WriteSelfTestFailure(Exception exception, string[] arguments)
    {
        if (!arguments.Contains("--self-test", StringComparer.Ordinal))
        {
            return;
        }

        string? dataDirectory = Environment.GetEnvironmentVariable("SERVITIUM_DESKTOP_TEST_DATA_ROOT");
        if (string.IsNullOrWhiteSpace(dataDirectory) || !System.IO.Path.IsPathFullyQualified(dataDirectory))
        {
            return;
        }

        try
        {
            _ = System.IO.Directory.CreateDirectory(dataDirectory);
            System.IO.File.WriteAllText(System.IO.Path.Combine(dataDirectory, "self-test-error.log"), exception.ToString());
        }
        catch
        {
            // 自检失败时不能让诊断写入异常掩盖原始退出状态。
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _window?.Dispose();
        _window = null;
        SingleInstanceService.Activated -= OnSecondaryActivated;
        _services?.Dispose();
        _services = null;
        GC.SuppressFinalize(this);
    }
}
