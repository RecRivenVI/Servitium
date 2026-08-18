using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;

using Servitium.Contracts.Instances;
using Servitium.Domain.Common;

namespace Servitium.Infrastructure.Distribution;

/// <summary>归档中可物化的文件。</summary>
/// <param name="EntryName">ZIP 条目名。</param>
/// <param name="ReleasePath">Release 中的规范相对路径。</param>
/// <param name="Size">解压后大小。</param>
public sealed record HmclPlannedFile(string EntryName, string ReleasePath, long Size);

/// <summary>经过安全检查且不写入磁盘的归档计划。</summary>
/// <param name="Files">将进入 Release 的文件。</param>
/// <param name="ExcludedPaths">固定规则排除项。</param>
/// <param name="PrismPreview">Prism 元数据；普通归档为空。</param>
public sealed record HmclArchivePlan(
    IReadOnlyList<HmclPlannedFile> Files,
    IReadOnlyDictionary<string, string> ExcludedPaths,
    PrismInstancePreviewDto? PrismPreview);

/// <summary>以净室规则检查 HMCL 内容归档和 Prism 原生实例 ZIP。</summary>
public static class HmclArchiveProcessor
{
    private const int MaximumEntries = 20_000;
    private const int MaximumDepth = 32;
    private const long MaximumExpandedBytes = 8L * 1024 * 1024 * 1024;
    private const long MaximumSingleFileBytes = 8L * 1024 * 1024 * 1024;
    private const double MaximumCompressionRatio = 200d;

    private static readonly HashSet<string> ExactExcludedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "options.txt", "optionsof.txt", "optionsshaders.txt", "servers.dat", "servers.dat_old",
        "usercache.json", "usernamecache.json", "realms_persistence.json", "modpack.json",
        "manifest.json", "minecraftinstance.json", "modrinth.index.json", "instance-game-settings.json",
        "hmclversion.cfg", "launcher_profiles.json", "launcher_accounts.json", "server.properties",
        "ops.json", "whitelist.json", "banned-players.json", "banned-ips.json", "eula.txt",
    };

    private static readonly string[] ExcludedDirectoryPrefixes =
    [
        "saves/", "screenshots/", "logs/", "crash-reports/", ".fabric/", ".mixin.out/", ".optifine/",
        "versions/", "libraries/", "assets/", "world/", "world_nether/", "world_the_end/", "backups/",
    ];

    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    /// <summary>检查普通 HMCL 内容归档。</summary>
    public static HmclArchivePlan InspectContentArchive(string archivePath)
    {
        using ZipArchive archive = OpenAndValidate(archivePath);
        return BuildPlan(archive, string.Empty, null);
    }

    /// <summary>识别并预览 Prism Launcher 原生实例 ZIP。</summary>
    public static HmclArchivePlan InspectPrismArchive(string archivePath)
    {
        using ZipArchive archive = OpenAndValidate(archivePath);
        string[] instanceMarkers = [.. archive.Entries
            .Where(entry => string.Equals(Path.GetFileName(NormalizeEntryName(entry.FullName)), "instance.cfg", StringComparison.OrdinalIgnoreCase))
            .Select(entry => ParentPrefix(NormalizeEntryName(entry.FullName)))
            .Distinct(StringComparer.OrdinalIgnoreCase)];
        if (instanceMarkers.Length != 1)
        {
            throw new DomainException("unsupported-archive-format", "A Prism archive must contain exactly one instance.cfg marker.");
        }

        string instanceRoot = instanceMarkers[0];
        ZipArchiveEntry packEntry = FindRequiredEntry(archive, instanceRoot + "mmc-pack.json");
        PrismComponents components = ReadComponents(packEntry);
        bool hasMinecraft = HasFilesBelow(archive, instanceRoot + "minecraft/");
        bool hasDotMinecraft = HasFilesBelow(archive, instanceRoot + ".minecraft/");
        if (hasMinecraft == hasDotMinecraft)
        {
            throw new DomainException("unsupported-archive-format", "A Prism archive must contain exactly one supported game directory.");
        }

        if (HasFilesBelow(archive, instanceRoot + "patches/"))
        {
            throw new DomainException("unsupported-archive-format", "Prism custom component patches require manual resolution.");
        }

        string gameRoot = instanceRoot + (hasMinecraft ? "minecraft/" : ".minecraft/");
        string instanceName = ReadInstanceName(FindRequiredEntry(archive, instanceRoot + "instance.cfg"));
        string? loader = components.LoaderId is null ? null : components.LoaderId + " " + components.LoaderVersion;
        HmclArchivePlan preliminary = BuildPlan(archive, gameRoot, null);
        PrismInstancePreviewDto preview = new(
            instanceName,
            components.GameVersion,
            loader,
            [.. preliminary.Files.Select(file => file.ReleasePath)],
            preliminary.ExcludedPaths);
        return preliminary with { PrismPreview = preview };
    }

    private static ZipArchive OpenAndValidate(string archivePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(archivePath);
        FileStream stream = new(archivePath, FileMode.Open, FileAccess.Read, FileShare.Read, 256 * 1024, FileOptions.RandomAccess);
        try
        {
            ZipArchive archive = new(stream, ZipArchiveMode.Read, leaveOpen: false);
            ValidateArchive(archive);
            return archive;
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    private static void ValidateArchive(ZipArchive archive)
    {
        if (archive.Entries.Count is 0 or > MaximumEntries)
        {
            throw new DomainException("archive-limit-exceeded", "The archive entry count is outside the allowed range.");
        }

        long total = 0;
        HashSet<string> names = new(StringComparer.OrdinalIgnoreCase);
        foreach (ZipArchiveEntry entry in archive.Entries)
        {
            string name = NormalizeEntryName(entry.FullName);
            ValidateRelativePath(name, entry.Name.Length == 0);
            if (!names.Add(name))
            {
                throw new DomainException("archive-duplicate-path", "The archive contains duplicate paths ignoring case.");
            }

            int unixMode = entry.ExternalAttributes >> 16;
            if ((unixMode & 0xF000) is 0xA000 or 0x6000)
            {
                throw new DomainException("archive-link-entry", "The archive contains a link or device entry.");
            }

            if (entry.Length > MaximumSingleFileBytes)
            {
                throw new DomainException("archive-limit-exceeded", "An archive entry exceeds the single-file size limit.");
            }

            total = checked(total + entry.Length);
            if (total > MaximumExpandedBytes)
            {
                throw new DomainException("archive-limit-exceeded", "The archive expanded size exceeds the limit.");
            }

            if (entry.Length > 0 && (entry.CompressedLength == 0 || (double)entry.Length / entry.CompressedLength > MaximumCompressionRatio))
            {
                throw new DomainException("archive-compression-ratio-exceeded", "An archive entry exceeds the compression ratio limit.");
            }
        }
    }

    private static HmclArchivePlan BuildPlan(ZipArchive archive, string sourcePrefix, PrismInstancePreviewDto? preview)
    {
        List<HmclPlannedFile> files = [];
        SortedDictionary<string, string> excluded = new(StringComparer.Ordinal);
        HashSet<string> outputPaths = new(StringComparer.OrdinalIgnoreCase);
        foreach (ZipArchiveEntry entry in archive.Entries)
        {
            string name = NormalizeEntryName(entry.FullName);
            if (entry.Name.Length == 0 || !name.StartsWith(sourcePrefix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string relative = name[sourcePrefix.Length..];
            if (relative.Length == 0)
            {
                continue;
            }

            string? exclusion = GetExclusionReason(relative);
            if (exclusion is not null)
            {
                excluded[relative] = exclusion;
                continue;
            }

            ValidateReleasePath(relative);
            if (!outputPaths.Add(relative))
            {
                throw new DomainException("hmcl-release-validation-failed", "Release paths must be unique ignoring case.");
            }

            files.Add(new HmclPlannedFile(name, relative, entry.Length));
        }

        return files.Count == 0
            ? throw new DomainException("hmcl-release-validation-failed", "No distributable files remain after filtering.")
            : new HmclArchivePlan(
            [.. files.OrderBy(file => file.ReleasePath, StringComparer.Ordinal)],
            excluded,
            preview);
    }

    /// <summary>返回固定排除清单命中的原因；未命中时返回空。</summary>
    public static string? GetExclusionReason(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        string normalized = path.Replace('\\', '/');
        string fileName = Path.GetFileName(normalized);
        return ExactExcludedNames.Contains(normalized) || ExactExcludedNames.Contains(fileName)
            ? "fixed-exclusion"
            : ExcludedDirectoryPrefixes.Any(prefix => normalized.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            ? "fixed-directory-exclusion"
            : (normalized.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase) ||
            normalized.EndsWith(".old", StringComparison.OrdinalIgnoreCase) ||
            normalized.EndsWith(".log", StringComparison.OrdinalIgnoreCase) ||
            normalized.EndsWith(".log.gz", StringComparison.OrdinalIgnoreCase))
            ? "special-client-or-log-file"
            : null;
    }

    private static PrismComponents ReadComponents(ZipArchiveEntry entry)
    {
        if (entry.Length > 1024 * 1024)
        {
            throw new DomainException("unsupported-archive-format", "The Prism component manifest is too large.");
        }

        using Stream stream = entry.Open();
        using var document = JsonDocument.Parse(stream, new JsonDocumentOptions { MaxDepth = 16 });
        JsonElement root = document.RootElement;
        if (!root.TryGetProperty("formatVersion", out JsonElement format) || format.GetInt32() != 1 ||
            !root.TryGetProperty("components", out JsonElement components) || components.ValueKind != JsonValueKind.Array)
        {
            throw new DomainException("unsupported-archive-format", "The Prism component manifest format is unsupported.");
        }

        string? game = null;
        string? loaderId = null;
        string? loaderVersion = null;
        foreach (JsonElement component in components.EnumerateArray())
        {
            string uid = component.GetProperty("uid").GetString() ?? string.Empty;
            string? version = component.TryGetProperty("version", out JsonElement versionElement) ? versionElement.GetString() : null;
            bool disabled = component.TryGetProperty("disabled", out JsonElement disabledElement) && disabledElement.ValueKind == JsonValueKind.True;
            if (disabled)
            {
                continue;
            }

            if (string.Equals(uid, "net.minecraft", StringComparison.Ordinal))
            {
                game = RequireComponentVersion(uid, version);
            }
            else if (TryMapLoader(uid, out string? mapped))
            {
                if (loaderId is not null)
                {
                    throw new DomainException("unsupported-archive-format", "The Prism archive declares multiple active mod loaders.");
                }

                loaderId = mapped;
                loaderVersion = RequireComponentVersion(uid, version);
            }
            else if (component.TryGetProperty("important", out JsonElement important) && important.ValueKind == JsonValueKind.True)
            {
                throw new DomainException("unsupported-archive-format", "The Prism archive contains an unknown important component.");
            }
        }

        return new PrismComponents(
            game ?? throw new DomainException("unsupported-archive-format", "The Prism archive does not declare a Minecraft version."),
            loaderId,
            loaderVersion);
    }

    private static bool TryMapLoader(string uid, out string? loader)
    {
        loader = uid switch
        {
            "net.minecraftforge" => "forge",
            "net.neoforged" => "neoforge",
            "net.fabricmc.fabric-loader" => "fabric",
            "org.quiltmc.quilt-loader" => "quilt",
            _ => null,
        };
        return loader is not null;
    }

    private static string RequireComponentVersion(string uid, string? version)
    {
        return string.IsNullOrWhiteSpace(version)
            ? throw new DomainException("unsupported-archive-format", string.Create(CultureInfo.InvariantCulture, $"The Prism component {uid} has no version."))
            : version;
    }

    private static string ReadInstanceName(ZipArchiveEntry entry)
    {
        if (entry.Length > 1024 * 1024)
        {
            throw new DomainException("unsupported-archive-format", "The Prism instance configuration is too large.");
        }

        using Stream stream = entry.Open();
        using StreamReader reader = new(stream, detectEncodingFromByteOrderMarks: true, leaveOpen: false);
        while (reader.ReadLine() is string line)
        {
            int separator = line.IndexOf('=', StringComparison.Ordinal);
            if (separator > 0 && (line[..separator].Equals("name", StringComparison.OrdinalIgnoreCase) || line[..separator].Equals("InstanceName", StringComparison.OrdinalIgnoreCase)))
            {
                string value = line[(separator + 1)..].Trim();
                if (value.Length is > 0 and <= 128)
                {
                    return value;
                }
            }
        }

        return "Prism instance";
    }

    private static ZipArchiveEntry FindRequiredEntry(ZipArchive archive, string expected)
    {
        return archive.Entries.SingleOrDefault(entry => string.Equals(NormalizeEntryName(entry.FullName), expected, StringComparison.OrdinalIgnoreCase))
            ?? throw new DomainException("unsupported-archive-format", "The Prism archive is missing a required metadata file.");
    }

    private static bool HasFilesBelow(ZipArchive archive, string prefix)
    {
        return archive.Entries.Any(entry => entry.Name.Length > 0 && NormalizeEntryName(entry.FullName).StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
    }

    private static string ParentPrefix(string path)
    {
        int separator = path.LastIndexOf('/');
        return separator < 0 ? string.Empty : path[..(separator + 1)];
    }

    private static string NormalizeEntryName(string path)
    {
        return path.Replace('\\', '/');
    }

    private static void ValidateRelativePath(string path, bool directory)
    {
        if (path.Length == 0 || path.StartsWith('/') || path.Contains('\\', StringComparison.Ordinal) || path.Contains(':', StringComparison.Ordinal))
        {
            throw new DomainException("archive-unsafe-path", "The archive contains an absolute or device path.");
        }

        string candidate = directory ? path.TrimEnd('/') : path;
        string[] segments = candidate.Split('/');
        if (segments.Length > MaximumDepth || segments.Any(segment => segment is "" or "." or ".."))
        {
            throw new DomainException("archive-unsafe-path", "The archive contains an unsafe or overly deep path.");
        }

        foreach (string segment in segments)
        {
            string stem = segment.Split('.')[0];
            if (segment.EndsWith(' ') || segment.EndsWith('.') || ReservedNames.Contains(stem) || segment.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            {
                throw new DomainException("archive-unsafe-path", "The archive contains a Windows-invalid path segment.");
            }
        }
    }

    /// <summary>校验待分发相对路径。</summary>
    public static void ValidateReleasePath(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        ValidateRelativePath(path, directory: false);
        if (path.Length > 240)
        {
            throw new DomainException("hmcl-release-validation-failed", "A release path exceeds 240 characters.");
        }
    }

    private sealed record PrismComponents(string GameVersion, string? LoaderId, string? LoaderVersion);
}
