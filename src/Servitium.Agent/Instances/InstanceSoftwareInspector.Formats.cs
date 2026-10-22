using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

using Servitium.Contracts.Instances;

namespace Servitium.Agent.Instances;

internal sealed partial class InstanceSoftwareInspector
{
    private static Dictionary<string, string> ReadManifest(string content)
    {
        Dictionary<string, string> result = new(StringComparer.OrdinalIgnoreCase);
        string? key = null;
        foreach (string line in content.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            if (line.Length == 0) { break; } // Only the main section describes the launched JAR.
            if (line.StartsWith(' ') && key is not null) { result[key] += line[1..]; continue; }
            int separator = line.IndexOf(": ", StringComparison.Ordinal);
            if (separator < 1 || !result.TryAdd(line[..separator], line[(separator + 2)..]))
            {
                throw new InvalidDataException("Invalid or ambiguous manifest.");
            }
            key = line[..separator];
        }
        return result;
    }

    private InstanceSoftwareDto ReadFabric(string root, string targetDirectory, string classPath)
    {
        List<string> games = [];
        List<string> loaders = [];
        foreach (string entry in classPath.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            string[] parts = entry.Split('/');
            if (parts.Length < 5) { continue; }
            string artifact = parts[^3];
            if (artifact is not ("fabric-loader" or "intermediary") || parts[^5] != "net" || parts[^4] != "fabricmc") { continue; }
            string version = parts[^2];
            if (!ValidVersion(version) || parts[^1] != artifact + "-" + version + ".jar") { return Unknown("metadata-conflict"); }
            string relative = Path.GetRelativePath(root, Path.Combine(targetDirectory, entry));
            if (!File.Exists(ResolveFile(root, relative))) { return Unknown("metadata-dependency-missing"); }
            (artifact == "intermediary" ? games : loaders).Add(version);
        }
        string[] gameVersions = [.. games.Distinct(StringComparer.Ordinal)];
        string[] loaderVersions = [.. loaders.Distinct(StringComparer.Ordinal)];
        if (gameVersions.Length > 1 || loaderVersions.Length > 1) { return Unknown("metadata-conflict"); }
        return Result(gameVersions.SingleOrDefault(), "Fabric", loaderVersions.SingleOrDefault(), "fabric-launch-manifest",
            gameVersions.Length == 1 && loaderVersions.Length == 1 ? null : "version-missing");
    }

    private static InstanceSoftwareDto ReadArgumentFile(string content)
    {
        string[] lines = [.. content.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n')
            .Select(line => line.Trim()).Where(line => line.Length != 0 && !line.StartsWith('#'))];
        string? loader = lines.Contains("net.neoforged.fml.startup.Server", StringComparer.Ordinal) ? "NeoForge" : null;
        string? target = ArgumentValue(lines, "--launchTarget");
        if (lines.Contains("cpw.mods.bootstraplauncher.BootstrapLauncher", StringComparer.Ordinal))
        {
            loader = target == "neoforgeserver" ? "NeoForge" : target == "forgeserver" ? "Forge" : null;
        }
        if (loader is null) { return Unknown("unrecognized-launch-format"); }
        string? game = ArgumentValue(lines, "--fml.mcVersion");
        string? version = ArgumentValue(lines, loader == "NeoForge" ? "--fml.neoForgeVersion" : "--fml.forgeVersion");
        return Result(game, loader, version, "fml-argument-file", game is not null && version is not null ? null : "version-missing");
    }

    private static string? ArgumentValue(string[] lines, string name)
    {
        string[] candidates = [.. lines.Where(line => line.StartsWith(name + " ", StringComparison.Ordinal) || line.StartsWith(name + "\t", StringComparison.Ordinal))];
        if (candidates.Length != 1) { return null; }
        string value = candidates[0][name.Length..].Trim();
        if (value.Length >= 2 && value[0] == '"' && value[^1] == '"') { value = value[1..^1]; }
        return ValidVersion(value) ? value : null;
    }
}
