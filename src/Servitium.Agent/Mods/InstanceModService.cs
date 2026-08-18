using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

using Servitium.Agent.Instances;
using Servitium.Agent.Paths;
using Servitium.Contracts.Mods;
using Servitium.Domain.Common;

namespace Servitium.Agent.Mods;

internal sealed partial class InstanceModService(InstanceRegistry instances)
{
    private static readonly (string Path, string Loader)[] MetadataPriority =
    [
        ("fabric.mod.json", "fabric"),
        ("quilt.mod.json", "quilt"),
        ("META-INF/neoforge.mods.toml", "neoforge"),
        ("META-INF/mods.toml", "forge"),
        ("mcmod.info", "forge-legacy"),
        ("paper-plugin.yml", "paper"),
        ("plugin.yml", "bukkit"),
    ];

    internal async Task<IReadOnlyList<ModDetailsDto>> ListAsync(Guid instanceId, string kind, CancellationToken cancellationToken)
    {
        LocalInstanceSnapshot instance = await RequireInstanceAsync(instanceId, cancellationToken).ConfigureAwait(false);
        string directory = ResolveDirectory(instance.WorkingDirectory, kind);
        if (!Directory.Exists(directory))
        {
            return [];
        }

        WindowsPathPolicy.EnsureNoReparsePoint(directory);
        List<ModDetailsDto> result = [];
        foreach (string file in Directory.EnumerateFiles(directory, "*.jar*", SearchOption.TopDirectoryOnly)
            .Where(IsJarOrDisabled)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0)
            {
                throw new DomainException("path-reparse-point", "The mod directory contains a reparse point.");
            }

            result.Add(Parse(instance.WorkingDirectory, file));
        }

        return result;
    }

    internal async Task<string> PrepareUploadAsync(Guid instanceId, string kind, string fileName, CancellationToken cancellationToken)
    {
        LocalInstanceSnapshot instance = await RequireInstanceAsync(instanceId, cancellationToken).ConfigureAwait(false);
        if (instance.Status is not ("Stopped" or "Crashed") || string.IsNullOrWhiteSpace(fileName) ||
            !fileName.EndsWith(".jar", StringComparison.OrdinalIgnoreCase) || fileName != Path.GetFileName(fileName) ||
            fileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            throw new DomainException("validation-failed", "The mod upload request is invalid or the instance is running.");
        }

        string directory = ResolveDirectory(instance.WorkingDirectory, kind);
        _ = Directory.CreateDirectory(directory);
        WindowsPathPolicy.EnsureNoReparsePoint(directory);
        return kind + "/" + fileName;
    }

    internal async Task<ModDetailsDto> GetAsync(Guid instanceId, string modId, string kind, CancellationToken cancellationToken)
    {
        return (await ListAsync(instanceId, kind, cancellationToken).ConfigureAwait(false)).SingleOrDefault(item => FixedIdEquals(item.Id, modId))
            ?? throw new DomainException("not-found", "The mod or plugin does not exist.");
    }

    internal async Task<ModDetailsDto> SetEnabledAsync(Guid instanceId, string modId, string kind, bool enabled, CancellationToken cancellationToken)
    {
        LocalInstanceSnapshot instance = await RequireInstanceAsync(instanceId, cancellationToken).ConfigureAwait(false);
        ModDetailsDto item = await GetAsync(instanceId, modId, kind, cancellationToken).ConfigureAwait(false);
        string source = ResolveFile(instance.WorkingDirectory, item.RelativePath);
        if (item.Enabled == enabled)
        {
            return item;
        }

        string destination = enabled ? source[..^".disabled".Length] : source + ".disabled";
        try
        {
            File.Move(source, destination, false);
        }
        catch (IOException exception)
        {
            throw new DomainException("file-locked", exception.Message);
        }

        return Parse(instance.WorkingDirectory, destination);
    }

    internal async Task DeleteAsync(Guid instanceId, string modId, string kind, CancellationToken cancellationToken)
    {
        LocalInstanceSnapshot instance = await RequireInstanceAsync(instanceId, cancellationToken).ConfigureAwait(false);
        if (instance.Status is not ("Stopped" or "Crashed"))
        {
            throw new DomainException("instance-not-stopped", "Deleting a mod requires a stopped instance.");
        }

        ModDetailsDto item = await GetAsync(instanceId, modId, kind, cancellationToken).ConfigureAwait(false);
        try
        {
            File.Delete(ResolveFile(instance.WorkingDirectory, item.RelativePath));
        }
        catch (IOException exception)
        {
            throw new DomainException("file-locked", exception.Message);
        }
    }

    internal async Task<IReadOnlyList<string>> ListConfigurationFilesAsync(Guid instanceId, string modId, string kind, CancellationToken cancellationToken)
    {
        LocalInstanceSnapshot instance = await RequireInstanceAsync(instanceId, cancellationToken).ConfigureAwait(false);
        ModDetailsDto item = await GetAsync(instanceId, modId, kind, cancellationToken).ConfigureAwait(false);
        string directory = Path.Combine(instance.WorkingDirectory, "config");
        if (!Directory.Exists(directory) || (File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
        {
            return [];
        }

        string normalizedName = NormalizeStem(item.Identifier ?? Path.GetFileNameWithoutExtension(item.Enabled ? item.RelativePath : item.RelativePath[..^".disabled".Length]));
        return [.. Directory.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly)
            .Where(path => (File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0)
            .Where(path => NormalizeStem(Path.GetFileNameWithoutExtension(path)).StartsWith(normalizedName, StringComparison.OrdinalIgnoreCase))
            .Select(path => Path.GetRelativePath(instance.WorkingDirectory, path).Replace('\\', '/'))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)];
    }

    private static ModDetailsDto Parse(string root, string path)
    {
        string relative = Path.GetRelativePath(root, path).Replace('\\', '/');
        bool enabled = !relative.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase);
        string jarName = enabled ? Path.GetFileNameWithoutExtension(path) : Path.GetFileNameWithoutExtension(Path.GetFileNameWithoutExtension(path));
        string stableRelative = enabled ? relative : relative[..^".disabled".Length];
        string id = StableId(stableRelative);
        FileInfo file = new(path);
        try
        {
            using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using ZipArchive archive = new(stream, ZipArchiveMode.Read, false);
            foreach ((string metadataPath, string loader) in MetadataPriority)
            {
                ZipArchiveEntry? entry = archive.Entries.SingleOrDefault(candidate => string.Equals(candidate.FullName, metadataPath, StringComparison.OrdinalIgnoreCase));
                if (entry is null || entry.Length > 1024 * 1024)
                {
                    continue;
                }

                using StreamReader reader = new(entry.Open(), new UTF8Encoding(false, true), true, 4096, false);
                string content = reader.ReadToEnd();
                Metadata metadata = metadataPath.EndsWith(".json", StringComparison.OrdinalIgnoreCase) || metadataPath.EndsWith(".info", StringComparison.OrdinalIgnoreCase)
                    ? ParseJsonMetadata(content, jarName)
                    : metadataPath.EndsWith(".toml", StringComparison.OrdinalIgnoreCase)
                        ? ParseTomlMetadata(content, jarName)
                        : ParseYamlMetadata(content, jarName);
                return new(
                    id,
                    relative,
                    metadata.Name,
                    metadata.Version,
                    metadata.Description,
                    metadata.Authors,
                    loader,
                    enabled,
                    stream.Length,
                    file.LastWriteTimeUtc,
                    metadata.Dependencies,
                    false,
                    metadata.Identifier);
            }

            return new(id, relative, jarName, null, null, [], null, enabled, stream.Length, file.LastWriteTimeUtc, [], true, null);
        }
        catch (Exception exception) when (exception is InvalidDataException or JsonException or DecoderFallbackException or IOException)
        {
            return new(id, relative, jarName, null, null, [], null, enabled, file.Length, file.LastWriteTimeUtc, [], true, null);
        }
    }

    private static Metadata ParseJsonMetadata(string content, string fallbackName)
    {
        using var document = JsonDocument.Parse(content, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
        JsonElement root = document.RootElement;
        if (root.ValueKind == JsonValueKind.Array && root.GetArrayLength() > 0)
        {
            root = root[0];
        }

        string? identifier = GetString(root, "id") ?? GetString(root, "modid");
        string? version = GetString(root, "version");
        IReadOnlyList<string> dependencies = ReadJsonDependencies(root);
        if (root.TryGetProperty("quilt_loader", out JsonElement quilt))
        {
            identifier = GetString(quilt, "id") ?? identifier;
            version = GetString(quilt, "version") ?? version;
            dependencies = ReadJsonDependencies(quilt);
            if (quilt.TryGetProperty("metadata", out JsonElement quiltMetadata))
            {
                root = quiltMetadata;
            }
        }

        string name = GetString(root, "name") ?? GetString(root, "id") ?? GetString(root, "modid") ?? fallbackName;
        identifier ??= GetString(root, "id") ?? GetString(root, "modid");
        version ??= GetString(root, "version");
        string? description = GetString(root, "description");
        List<string> authors = [];
        if (root.TryGetProperty("authors", out JsonElement authorElement))
        {
            if (authorElement.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement author in authorElement.EnumerateArray())
                {
                    string? value = author.ValueKind == JsonValueKind.String ? author.GetString() : GetString(author, "name");
                    if (!string.IsNullOrWhiteSpace(value))
                    {
                        authors.Add(value);
                    }
                }
            }
            else if (authorElement.ValueKind == JsonValueKind.String && authorElement.GetString() is { } author)
            {
                authors.Add(author);
            }
        }

        return new(identifier, name, version, description, authors, dependencies);
    }

    private static Metadata ParseTomlMetadata(string content, string fallbackName)
    {
        string? identifier = FindScalar(content, "modId");
        string[] declaredIds = FindScalars(content, "modId");
        return new(
            identifier,
            FindScalar(content, "displayName") ?? identifier ?? fallbackName,
            FindScalar(content, "version"),
            FindScalar(content, "description"),
            SplitAuthors(FindScalar(content, "authors")),
            [.. declaredIds.Where(value => !string.Equals(value, identifier, StringComparison.OrdinalIgnoreCase)).Distinct(StringComparer.OrdinalIgnoreCase)]);
    }

    private static Metadata ParseYamlMetadata(string content, string fallbackName)
    {
        string? name = FindYamlScalar(content, "name");
        return new(
            name,
            name ?? fallbackName,
            FindYamlScalar(content, "version"),
            FindYamlScalar(content, "description"),
            SplitAuthors(FindYamlScalar(content, "authors") ?? FindYamlScalar(content, "author")),
            SplitAuthors(FindYamlScalar(content, "depend")));
    }

    private static IReadOnlyList<string> ReadJsonDependencies(JsonElement root)
    {
        return !root.TryGetProperty("depends", out JsonElement dependencies)
            ? []
            : dependencies.ValueKind switch
            {
                JsonValueKind.Object => [.. dependencies.EnumerateObject().Select(property => property.Name).OrderBy(value => value, StringComparer.Ordinal)],
                JsonValueKind.Array => [.. dependencies.EnumerateArray()
                .Select(item => item.ValueKind == JsonValueKind.String ? item.GetString() : GetString(item, "id"))
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(value => value!)],
                JsonValueKind.Undefined or JsonValueKind.String or JsonValueKind.Number or JsonValueKind.True or
                    JsonValueKind.False or JsonValueKind.Null => [],
                _ => [],
            };
    }

    private static string? GetString(JsonElement element, string propertyName)
    {
        return element.ValueKind == JsonValueKind.Object && element.TryGetProperty(propertyName, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }

    private static string? FindScalar(string content, string key)
    {
        Match match = Regex.Match(content, "(?m)^\\s*" + Regex.Escape(key) + "\\s*=\\s*[\"']?(?<value>[^\"'\\r\\n#]+)", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
        return match.Success ? match.Groups["value"].Value.Trim() : null;
    }

    private static string[] FindScalars(string content, string key)
    {
        return [.. Regex.Matches(content, "(?m)^\\s*" + Regex.Escape(key) + "\\s*=\\s*[\"']?(?<value>[^\"'\\r\\n#]+)", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1))
            .Select(match => match.Groups["value"].Value.Trim())
            .Where(value => value.Length > 0)];
    }

    private static string? FindYamlScalar(string content, string key)
    {
        Match match = Regex.Match(content, "(?m)^\\s*" + Regex.Escape(key) + "\\s*:\\s*[\"']?(?<value>[^\"'\\r\\n#]+)", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
        return match.Success ? match.Groups["value"].Value.Trim() : null;
    }

    private static string[] SplitAuthors(string? authors)
    {
        return string.IsNullOrWhiteSpace(authors)
            ? []
            : [.. authors.Trim('[', ']').Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(author => author.Trim('"', '\''))
                .Where(author => author.Length > 0)];
    }

    private async Task<LocalInstanceSnapshot> RequireInstanceAsync(Guid instanceId, CancellationToken cancellationToken)
    {
        LocalInstanceSnapshot instance = await instances.FindAsync(instanceId, cancellationToken).ConfigureAwait(false)
            ?? throw new DomainException("not-found", "The instance registration does not exist.");
        WindowsPathPolicy.EnsureNoReparsePoint(instance.WorkingDirectory);
        return instance;
    }

    private static string ResolveDirectory(string root, string kind)
    {
        string name = kind switch
        {
            "mods" => "mods",
            "plugins" => "plugins",
            _ => throw new DomainException("validation-failed", "The mod kind is invalid."),
        };
        return Path.Combine(root, name);
    }

    private static string ResolveFile(string root, string relativePath)
    {
        string full = Path.GetFullPath(Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        string normalizedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        if (!full.StartsWith(normalizedRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            throw new DomainException("path-escapes-root", "The mod path escapes the instance root.");
        }

        WindowsPathPolicy.EnsureNoReparsePoint(Path.GetDirectoryName(full)!);
        return full;
    }

    private static bool IsJarOrDisabled(string path)
    {
        return path.EndsWith(".jar", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".jar.disabled", StringComparison.OrdinalIgnoreCase);
    }

    private static string StableId(string relativePath)
    {
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(relativePath.ToUpperInvariant())))[..24];
    }

    private static bool FixedIdEquals(string expected, string actual)
    {
        return expected.Length == actual.Length && CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(expected), Encoding.ASCII.GetBytes(actual));
    }

    private static string NormalizeStem(string value)
    {
        return NonIdentifier().Replace(value, string.Empty).ToUpperInvariant();
    }

    [GeneratedRegex("[^A-Za-z0-9_.-]", RegexOptions.CultureInvariant, 1000)]
    private static partial Regex NonIdentifier();

    private sealed record Metadata(
        string? Identifier,
        string Name,
        string? Version,
        string? Description,
        IReadOnlyList<string> Authors,
        IReadOnlyList<string> Dependencies);
}
