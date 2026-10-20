using System;
using System.Threading.Tasks;

using Microsoft.Windows.AppLifecycle;

namespace Servitium.Desktop.Services;

internal static class SingleInstanceService
{
    internal static event EventHandler<AppActivationArguments>? Activated;

    internal static async Task<bool> RedirectIfSecondaryAsync()
    {
        var current = AppInstance.GetCurrent();
        var registered = AppInstance.FindOrRegisterForKey("Servitium.Desktop");
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
}
