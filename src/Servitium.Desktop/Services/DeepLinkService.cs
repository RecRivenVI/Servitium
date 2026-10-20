using System;
using System.Collections.Generic;

using Servitium.Desktop.Models;

namespace Servitium.Desktop.Services;

internal static class DeepLinkService
{
    private static readonly Dictionary<string, string> Routes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["dashboard"] = "PG-040",
        ["instances"] = "PG-010",
        ["instance"] = "PG-012",
        ["nodes"] = "PG-020",
        ["node"] = "PG-021",
        ["operations"] = "PG-044",
        ["distribution"] = "PG-060",
        ["channel"] = "PG-061",
        ["release"] = "PG-063",
        ["account"] = "PG-032",
        ["settings"] = "PG-041",
    };

    internal static bool TryParse(string? value, out DeepLinkTarget? target)
    {
        target = null;
        if (!Uri.TryCreate(value, UriKind.Absolute, out Uri? uri) ||
            !string.Equals(uri.Scheme, "servitium", StringComparison.OrdinalIgnoreCase) ||
            !Routes.TryGetValue(uri.Host, out string? pageId))
        {
            return false;
        }

        string segment = uri.AbsolutePath.Trim('/');
        Guid? id = segment.Length == 0 ? null : Guid.TryParse(segment, out Guid parsed) ? parsed : null;
        if (segment.Length > 0 && id is null)
        {
            return false;
        }

        Dictionary<string, string> parameters = new(StringComparer.Ordinal);
        string query = uri.Query.TrimStart('?');
        foreach (string part in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            string[] pair = part.Split('=', 2);
            string key = Uri.UnescapeDataString(pair[0]);
            string item = pair.Length == 2 ? Uri.UnescapeDataString(pair[1]) : string.Empty;
            if (key.Length is > 0 and <= 64 && item.Length <= 1024)
            {
                parameters[key] = item;
            }
        }

        target = new DeepLinkTarget(pageId, id, parameters);
        return true;
    }
}
