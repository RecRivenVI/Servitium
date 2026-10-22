using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using Servitium.Agent.Paths;
using Servitium.Contracts.Instances;
using Servitium.Domain.Primitives;

namespace Servitium.Agent.Instances;

/// <summary>只读取已登记启动目标内的明确元数据，不执行代码或按文件名猜测。</summary>
internal sealed partial class InstanceSoftwareInspector(IWindowsPathPolicy paths)
{
    private const int MaximumMetadataBytes = 64 * 1024;

    internal async Task<InstanceSoftwareDto> InspectAsync(string root, StructuredLaunchRequest launch, CancellationToken cancellationToken)
    {
        if (launch.AdvancedMode) { return Unknown("advanced-launch"); }
        if (launch.ServerArguments.Any(argument => argument.StartsWith("--fml.", StringComparison.Ordinal))) { return Unknown("metadata-conflict"); }
        try
        {
            string target = ResolveFile(root, launch.Target);
            if (!File.Exists(target)) { return Unknown("launch-target-missing"); }
            return launch.TargetKind == "jar"
                ? await ReadJarAsync(root, target, cancellationToken).ConfigureAwait(false)
                : launch.TargetKind == "argfile"
                    ? ReadArgumentFile(await ReadFileAsync(target, cancellationToken).ConfigureAwait(false))
                    : Unknown("unrecognized-launch-format");
        }
        catch (DomainException) { return Unknown("unsafe-metadata-path"); }
        catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or DecoderFallbackException)
        {
            return Unknown("metadata-unreadable");
        }
    }

    private async Task<InstanceSoftwareDto> ReadJarAsync(string root, string target, CancellationToken cancellationToken)
    {
        using FileStream file = new(target, FileMode.Open, FileAccess.Read, FileShare.Read);
        using ZipArchive jar = new(file, ZipArchiveMode.Read);
        string? manifest = await ReadEntryAsync(jar, "META-INF/MANIFEST.MF", cancellationToken).ConfigureAwait(false);
        Dictionary<string, string> attributes = ReadManifest(manifest ?? "");
        string? main = attributes.GetValueOrDefault("Main-Class");
        if (main is "net.fabricmc.loader.impl.launch.server.FabricServerLauncher" or "net.fabricmc.loader.launch.server.FabricServerLauncher")
        {
            return ReadFabric(root, Path.GetDirectoryName(target)!, attributes.GetValueOrDefault("Class-Path") ?? "");
        }
        if (main is not ("net.minecraft.bundler.Main" or "net.minecraft.server.Main" or "net.minecraft.server.MinecraftServer"))
        {
            return Unknown("unrecognized-launch-format");
        }

        string? version = await ReadEntryAsync(jar, "version.json", cancellationToken).ConfigureAwait(false);
        if (version is null) { return Result(null, "Vanilla", null, "minecraft-jar", "version-missing"); }
        using JsonDocument document = JsonDocument.Parse(version);
        if (document.RootElement.ValueKind != JsonValueKind.Object) { throw new InvalidDataException("Invalid version metadata."); }
        JsonElement[] ids = [.. document.RootElement.EnumerateObject().Where(property => property.NameEquals("id")).Select(property => property.Value)];
        string? game = ids.Length == 1 && ids[0].ValueKind == JsonValueKind.String ? ids[0].GetString() : null;
        return Result(ValidVersion(game) ? game : null, "Vanilla", null, "minecraft-jar", ValidVersion(game) ? null : "version-missing");
    }

    private string ResolveFile(string root, string relative)
    {
        string path = paths.ResolveRelativePath(root, relative);
        WindowsPathPolicy.EnsureNoReparsePoint(root);
        WindowsPathPolicy.EnsureNoReparsePoint(Path.GetDirectoryName(path)!);
        if (File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        {
            throw new DomainException("path-reparse-point", "Metadata cannot be read through a reparse point.");
        }
        return path;
    }

    private static async Task<string> ReadFileAsync(string path, CancellationToken cancellationToken)
    {
        using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return await ReadTextAsync(stream, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<string?> ReadEntryAsync(ZipArchive jar, string name, CancellationToken cancellationToken)
    {
        ZipArchiveEntry[] entries = [.. jar.Entries.Where(entry => entry.FullName == name)];
        if (entries.Length == 0) { return null; }
        if (entries.Length != 1 || entries[0].Length > MaximumMetadataBytes) { throw new InvalidDataException("Ambiguous or oversized metadata."); }
        using Stream stream = await entries[0].OpenAsync(cancellationToken).ConfigureAwait(false);
        return await ReadTextAsync(stream, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<string> ReadTextAsync(Stream stream, CancellationToken cancellationToken)
    {
        byte[] buffer = new byte[MaximumMetadataBytes + 1];
        int count = await stream.ReadAtLeastAsync(buffer, buffer.Length, throwOnEndOfStream: false, cancellationToken).ConfigureAwait(false);
        if (count > MaximumMetadataBytes) { throw new InvalidDataException("Metadata exceeds its size limit."); }
        return new UTF8Encoding(false, true).GetString(buffer, 0, count).TrimStart('\uFEFF');
    }

    private static InstanceSoftwareDto Unknown(string issue) => Result(null, null, null, "unknown", issue);
    private static InstanceSoftwareDto Result(string? game, string? loader, string? loaderVersion, string source, string? issue = null) =>
        new(game, loader, loaderVersion, source, issue, DateTimeOffset.UtcNow);

    private static bool ValidVersion(string? version) => version is { Length: > 0 and <= 80 } &&
        version.All(character => char.IsAsciiLetterOrDigit(character) || character is '.' or '-' or '_' or '+');
}
