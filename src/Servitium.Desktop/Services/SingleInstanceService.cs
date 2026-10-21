using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;

using Microsoft.Windows.AppLifecycle;

namespace Servitium.Desktop.Services;

internal static class SingleInstanceService
{
    internal static event EventHandler<AppActivationArguments>? Activated;

    internal static async Task<bool> RedirectIfSecondaryAsync()
    {
        var current = AppInstance.GetCurrent();
        bool isolated = Environment.GetCommandLineArgs().Any(value => value is "--isolated-test" or "--self-test");
        string key = RegistrationKey(isolated, Environment.GetEnvironmentVariable("SERVITIUM_DESKTOP_TEST_DATA_ROOT"));
        var registered = AppInstance.FindOrRegisterForKey(key);
        if (registered.IsCurrent)
        {
            return false;
        }

        await registered.RedirectActivationToAsync(current.GetActivatedEventArgs());
        return true;
    }

    internal static void RegisterPrimaryActivation()
    {
        AppInstance.GetCurrent().Activated += (_, args) => Activated?.Invoke(null, args);
    }

    internal static string RegistrationKey(bool isolated, string? dataDirectory)
    {
        if (!isolated) { return "Servitium.Desktop"; }
        if (string.IsNullOrWhiteSpace(dataDirectory) || !Path.IsPathFullyQualified(dataDirectory))
        {
            throw new InvalidOperationException("An isolated Desktop launch requires its own absolute data directory.");
        }
        string normalized = Path.GetFullPath(dataDirectory).TrimEnd(Path.DirectorySeparatorChar).ToUpperInvariant();
        return "Servitium.Desktop.Test." + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)));
    }
}
