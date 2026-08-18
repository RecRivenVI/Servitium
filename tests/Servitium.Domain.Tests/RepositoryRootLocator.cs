using System;
using System.IO;

namespace Servitium.Domain.Tests;

internal static class RepositoryRootLocator
{
    internal static string Find()
    {
        DirectoryInfo? current = new(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "AGENTS.md")) &&
                File.Exists(Path.Combine(current.FullName, "Servitium.sln")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate the Servitium repository root.");
    }
}
