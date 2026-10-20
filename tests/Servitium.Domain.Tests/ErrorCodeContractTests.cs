using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;

using Servitium.Contracts;

using Xunit;

namespace Servitium.Domain.Tests;

public sealed partial class ErrorCodeContractTests
{
    [Fact]
    [Trait("Category", "Architecture")]
    [Trait("TestId", "ARC-010")]
    public void ErrorCodes_常量与清单及协议表_完全一致()
    {
        string repositoryRoot = RepositoryRootLocator.Find();
        string manifestPath = Path.Combine(repositoryRoot, "src", "Servitium.Contracts", "error-codes.json");
        string protocolsPath = Path.Combine(repositoryRoot, "documents", "reference", "protocol-control_plane.md");

        var constants = typeof(ErrorCodes)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(field => field.IsLiteral && field.FieldType == typeof(string))
            .Select(field => (string)field.GetRawConstantValue()!)
            .ToHashSet(StringComparer.Ordinal);
        string[] manifestValues = JsonSerializer.Deserialize<string[]>(File.ReadAllText(manifestPath)) ?? [];
        HashSet<string> manifest = manifestValues.ToHashSet(StringComparer.Ordinal);
        HashSet<string> documented = ReadDocumentedErrorCodes(protocolsPath);

        Assert.True(constants.SetEquals(ErrorCodes.All));
        Assert.True(constants.SetEquals(manifest));
        Assert.True(constants.SetEquals(documented));
        Assert.Equal(manifestValues.Length, manifest.Count);
    }

    private static HashSet<string> ReadDocumentedErrorCodes(string protocolsPath)
    {
        string markdown = File.ReadAllText(protocolsPath);
        int sectionStart = markdown.IndexOf("### 2.5 稳定错误码", StringComparison.Ordinal);
        int sectionEnd = markdown.IndexOf("### 2.6 幂等", sectionStart, StringComparison.Ordinal);
        Assert.True(sectionStart >= 0 && sectionEnd > sectionStart, "Could not locate the stable error-code table.");

        string section = markdown[sectionStart..sectionEnd];
        return ErrorCodeRowRegex()
            .Matches(section)
            .Select(match => match.Groups[1].Value)
            .ToHashSet(StringComparer.Ordinal);
    }

    [GeneratedRegex(@"^\| `([a-z0-9-]+)` \|", RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex ErrorCodeRowRegex();
}
