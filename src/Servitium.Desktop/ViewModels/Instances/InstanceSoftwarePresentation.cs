using System;
using System.Linq;

using Servitium.Contracts.Instances;
using Servitium.Desktop.Services;

namespace Servitium.Desktop.ViewModels.Instances;

internal static class InstanceSoftwarePresentation
{
    internal static string Summary(InstanceSoftwareDto? software, LocalizationService localization)
    {
        if (software is null) { return localization.Get("desktop.version-not-reported"); }
        string? loader = software.Loader == "Vanilla" ? localization.Get("desktop.software-vanilla") : software.Loader;
        string[] parts = [.. new[] { software.GameVersion, loader, software.LoaderVersion }.Where(value => !string.IsNullOrEmpty(value)).Select(value => value!)];
        return parts.Length == 0 ? localization.Get("desktop.version-not-reported") : string.Join(" · ", parts);
    }

    internal static string Note(InstanceSoftwareDto? software, bool reachable, LocalizationService localization)
    {
        string issue = localization.Get(software?.Issue switch
        {
            null when software is not null => "desktop.software-observed",
            "advanced-launch" => "desktop.software-advanced",
            "launch-target-missing" => "desktop.software-target-missing",
            "metadata-conflict" => "desktop.software-conflict",
            "unsafe-metadata-path" => "desktop.software-unsafe",
            "metadata-unreadable" => "desktop.software-unreadable",
            "metadata-dependency-missing" => "desktop.software-dependency-missing",
            "version-missing" => "desktop.software-version-missing",
            _ => "desktop.software-unknown",
        });
        return !reachable && software is not null
            ? localization.Format("desktop.software-offline", software.ObservedAt.ToLocalTime().ToString("g", System.Globalization.CultureInfo.CurrentCulture)) + " " + issue
            : issue;
    }
}
