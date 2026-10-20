using System.IO;
using System.Linq;
using System.Reflection;

namespace Servitium.Domain.Tests;

internal static class RepositoryRootLocator
{
    internal static string Find()
    {
        string? root = typeof(RepositoryRootLocator).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .SingleOrDefault(item => item.Key == "RepositoryRoot")?.Value;
        return root is not null && File.Exists(Path.Combine(root, "Servitium.sln"))
            ? root
            : throw new DirectoryNotFoundException("The test assembly does not identify an existing repository.");
    }
}
