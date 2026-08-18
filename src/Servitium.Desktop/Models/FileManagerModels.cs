using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Servitium.Desktop.Models;

internal sealed record FileClipboardPlan(string Operation, IReadOnlyList<string> SourcePaths)
{
    internal IReadOnlyList<(string Source, string Destination)> Destinations(string directory)
    {
        string normalizedDirectory = directory.Replace('\\', '/').Trim('/');
        return [.. SourcePaths.Select(source =>
        {
            string normalizedSource = source.Replace('\\', '/').Trim('/');
            string name = Path.GetFileName(normalizedSource);
            if (name.Length == 0)
            {
                throw new InvalidOperationException("A clipboard source must name a file or directory.");
            }

            string destination = normalizedDirectory.Length == 0 ? name : normalizedDirectory + "/" + name;
            return (normalizedSource, destination);
        })];
    }
}

internal sealed record DirectoryTab(int Index, string Path, bool Active);
