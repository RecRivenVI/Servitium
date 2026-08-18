using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.EntityFrameworkCore;

using Servitium.Agent.Instances;
using Servitium.Agent.Paths;
using Servitium.Agent.Storage;
using Servitium.Application.Abstractions;
using Servitium.Contracts.Configurations;
using Servitium.Domain.Common;
using Servitium.Infrastructure.Persistence;

namespace Servitium.Agent.Configurations;

internal sealed class InstanceConfigurationService(
    InstanceRegistry instances,
    AgentStorageOptions options,
    IDbContextFactory<AgentDbContext>? contextFactory = null,
    SqliteWriteCoordinator? writeCoordinator = null,
    IClock? clock = null)
{
    private static readonly (string Key, string Pattern, string Format)[] KnownConfigurations =
    [
        ("server-properties", "server.properties", "properties"),
        ("eula", "eula.txt", "properties"),
        ("operators", "ops.json", "json"),
        ("whitelist", "whitelist.json", "json"),
        ("banned-players", "banned-players.json", "json"),
        ("banned-ips", "banned-ips.json", "json"),
        ("bukkit", "bukkit.yml", "yaml"),
        ("spigot", "spigot.yml", "yaml"),
        ("paper", "paper.yml", "yaml"),
        ("paper-global", "config/paper-global.yml", "yaml"),
        ("paper-global", "paper-global.yml", "yaml"),
        ("paper-world-defaults", "config/paper-world-defaults.yml", "yaml"),
        ("purpur", "purpur.yml", "yaml"),
        ("pufferfish", "pufferfish.yml", "yaml"),
        ("velocity", "velocity.toml", "toml"),
    ];

    internal async Task<IReadOnlyList<ConfigurationDto>> ListAsync(Guid instanceId, CancellationToken cancellationToken)
    {
        LocalInstanceSnapshot instance = await RequireInstanceAsync(instanceId, cancellationToken).ConfigureAwait(false);
        List<ConfigurationDto> result = [];
        foreach ((string key, string pattern, string format) in KnownConfigurations)
        {
            string path = Path.Combine(instance.WorkingDirectory, pattern.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(path) && !IsReparsePoint(path))
            {
                result.Add(new(key, pattern, format, false, [pattern], File.GetLastWriteTimeUtc(path)));
            }
        }

        AddGlobConfigurations(instance.WorkingDirectory, "config/fabric", "*.json", "fabric", "json", result);
        AddGlobConfigurations(instance.WorkingDirectory, "config", "*.toml", "forge", "toml", result);
        return [.. result
            .GroupBy(item => item.Key, StringComparer.Ordinal)
            .Select(group =>
            {
                ConfigurationDto selected = group.OrderBy(item => item.RelativePath, StringComparer.OrdinalIgnoreCase).First();
                string[] candidates = [.. group.Select(item => item.RelativePath).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase)];
                return selected with { HasConflict = candidates.Length > 1, Candidates = candidates };
            })
            .OrderBy(item => item.RelativePath, StringComparer.OrdinalIgnoreCase)];
    }

    internal async Task<ConfigurationDetailsDto> ReadAsync(Guid instanceId, string configKey, CancellationToken cancellationToken)
    {
        LocalInstanceSnapshot instance = await RequireInstanceAsync(instanceId, cancellationToken).ConfigureAwait(false);
        ConfigurationDto configuration = await ResolveAsync(instanceId, configKey, cancellationToken).ConfigureAwait(false);
        string path = ResolvePath(instance.WorkingDirectory, configuration.RelativePath);
        byte[] bytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
        IReadOnlyDictionary<string, string?> values = ReadValues(configuration.Format, bytes);
        string raw = DecodeUtf8(bytes);
        HashSet<string> known = string.Equals(configuration.Key, "server-properties", StringComparison.Ordinal)
            ? ServerPropertiesSchema.Fields.Select(field => field.Key).ToHashSet(StringComparer.Ordinal)
            : new HashSet<string>(StringComparer.Ordinal);
        string[] unknown = known.Count == 0 ? [] : [.. values.Keys.Where(key => !known.Contains(key)).Order(StringComparer.Ordinal)];
        return new(
            configuration.Key,
            configuration.RelativePath,
            configuration.Format,
            values,
            raw,
            Revision(bytes),
            File.GetLastWriteTimeUtc(path),
            bytes.AsSpan().StartsWith(Encoding.UTF8.Preamble) ? "utf-8-bom" : "utf-8",
            DetectLineEnding(raw),
            unknown,
            configuration.HasConflict ? ["configuration-candidates-conflict"] : []);
    }

    internal async Task<ConfigurationDetailsDto> WriteAsync(
        Guid instanceId,
        string configKey,
        WriteConfigurationRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        LocalInstanceSnapshot instance = await RequireStoppedInstanceAsync(instanceId, cancellationToken).ConfigureAwait(false);
        ConfigurationDto configuration = await ResolveAsync(instanceId, configKey, cancellationToken).ConfigureAwait(false);
        if (configuration.HasConflict)
        {
            throw new DomainException("conflict", "The logical configuration has multiple candidates and cannot be written without an explicit selection.");
        }
        string path = ResolvePath(instance.WorkingDirectory, configuration.RelativePath);
        byte[] original = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
        if (!FixedHashEquals(Revision(original), request.ExpectedRevision))
        {
            throw new DomainException("concurrency-conflict", "The configuration changed after it was read.");
        }

        byte[] updated;
        if (request.RawText is not null)
        {
            updated = string.Equals(request.RawText, DecodeUtf8(original), StringComparison.Ordinal)
                ? original
                : EncodeUtf8(request.RawText, original.AsSpan().StartsWith(Encoding.UTF8.Preamble));
            _ = ReadValues(configuration.Format, updated);
        }
        else
        {
            updated = request.Values is not null
                ? ApplyValues(configuration.Format, original, request.Values)
                : throw new DomainException("validation-failed", "Either raw text or structured values are required.");
        }

        _ = ReadValues(configuration.Format, updated);
        await BackupAsync(instanceId, configuration.RelativePath, original, cancellationToken).ConfigureAwait(false);
        await AtomicWriteAsync(path, updated, cancellationToken).ConfigureAwait(false);
        return await ReadAsync(instanceId, configKey, cancellationToken).ConfigureAwait(false);
    }

    internal async Task<IReadOnlyList<ConfigurationBackupDto>> ListBackupsAsync(Guid instanceId, string configKey, CancellationToken cancellationToken)
    {
        ConfigurationDto configuration = await ResolveAsync(instanceId, configKey, cancellationToken).ConfigureAwait(false);
        if (contextFactory is not null)
        {
            using AgentDbContext context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            LocalConfigBackupRow[] rows = await context.ConfigBackups.AsNoTracking()
                .Where(row => row.InstanceId == instanceId && row.RelativePath == configuration.RelativePath)
                .OrderByDescending(row => row.CreatedAtUnixMilliseconds)
                .Take(10)
                .ToArrayAsync(cancellationToken).ConfigureAwait(false);
            return [.. rows.Select(row => new ConfigurationBackupDto(
                row.Id,
                DateTimeOffset.FromUnixTimeMilliseconds(row.CreatedAtUnixMilliseconds),
                row.Size,
                row.Sha256))];
        }

        string directory = BackupDirectory(instanceId, configuration.RelativePath);
        if (!Directory.Exists(directory))
        {
            return [];
        }

        List<ConfigurationBackupDto> result = [];
        foreach (string path in Directory.EnumerateFiles(directory, "*.bak", SearchOption.TopDirectoryOnly))
        {
            string name = Path.GetFileNameWithoutExtension(path);
            string[] parts = name.Split('-', 2);
            if (parts.Length == 2 && DateTimeOffset.TryParseExact(parts[0], "yyyyMMddHHmmssfffffff", null, System.Globalization.DateTimeStyles.AssumeUniversal, out DateTimeOffset created) && Guid.TryParseExact(parts[1], "N", out Guid id))
            {
                byte[] bytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
                result.Add(new(id, created.ToUniversalTime(), bytes.LongLength, Revision(bytes)));
            }
        }

        return [.. result.OrderByDescending(item => item.CreatedAt).Take(10)];
    }

    internal async Task<ConfigurationDetailsDto> RestoreBackupAsync(Guid instanceId, string configKey, Guid backupId, CancellationToken cancellationToken)
    {
        LocalInstanceSnapshot instance = await RequireStoppedInstanceAsync(instanceId, cancellationToken).ConfigureAwait(false);
        ConfigurationDto configuration = await ResolveAsync(instanceId, configKey, cancellationToken).ConfigureAwait(false);
        if (configuration.HasConflict)
        {
            throw new DomainException("conflict", "The logical configuration has multiple candidates and cannot be restored without an explicit selection.");
        }
        string? backup;
        if (contextFactory is not null)
        {
            using AgentDbContext context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            backup = await context.ConfigBackups.AsNoTracking()
                .Where(row => row.Id == backupId && row.InstanceId == instanceId && row.RelativePath == configuration.RelativePath)
                .Select(row => row.StoragePath)
                .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
            if (backup is not null)
            {
                EnsureBackupPath(backup);
            }
        }
        else
        {
            string directory = BackupDirectory(instanceId, configuration.RelativePath);
            backup = Directory.Exists(directory)
                ? Directory.EnumerateFiles(directory, "*-" + backupId.ToString("N") + ".bak", SearchOption.TopDirectoryOnly).SingleOrDefault()
                : null;
        }
        if (backup is null)
        {
            throw new DomainException("not-found", "The configuration backup does not exist.");
        }

        string target = ResolvePath(instance.WorkingDirectory, configuration.RelativePath);
        byte[] current = await File.ReadAllBytesAsync(target, cancellationToken).ConfigureAwait(false);
        byte[] restored = await File.ReadAllBytesAsync(backup, cancellationToken).ConfigureAwait(false);
        _ = ReadValues(configuration.Format, restored);
        await BackupAsync(instanceId, configuration.RelativePath, current, cancellationToken).ConfigureAwait(false);
        await AtomicWriteAsync(target, restored, cancellationToken).ConfigureAwait(false);
        return await ReadAsync(instanceId, configKey, cancellationToken).ConfigureAwait(false);
    }

    internal static IReadOnlyList<ConfigurationFieldDto> GetSchema(string configKey)
    {
        return !string.Equals(configKey, "server-properties", StringComparison.Ordinal) ? [] : ServerPropertiesSchema.Fields;
    }

    private async Task<ConfigurationDto> ResolveAsync(Guid instanceId, string configKey, CancellationToken cancellationToken)
    {
        return (await ListAsync(instanceId, cancellationToken).ConfigureAwait(false)).SingleOrDefault(item => string.Equals(item.Key, configKey, StringComparison.Ordinal))
            ?? throw new DomainException("not-found", "The configuration file is not recognized.");
    }

    private async Task BackupAsync(Guid instanceId, string relativePath, byte[] bytes, CancellationToken cancellationToken)
    {
        string directory = BackupDirectory(instanceId, relativePath);
        _ = Directory.CreateDirectory(directory);
        DateTimeOffset createdAt = clock?.UtcNow ?? DateTimeOffset.UtcNow;
        var id = Guid.NewGuid();
        string path = Path.Combine(directory, createdAt.ToString("yyyyMMddHHmmssfffffff", System.Globalization.CultureInfo.InvariantCulture) + "-" + id.ToString("N") + ".bak");
        using (FileStream stream = new(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.Asynchronous | FileOptions.WriteThrough))
        {
            await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        if (contextFactory is not null && writeCoordinator is not null)
        {
            try
            {
                await writeCoordinator.ExecuteAsync(async () =>
                {
                    using AgentDbContext context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
                    _ = context.ConfigBackups.Add(new LocalConfigBackupRow
                    {
                        Id = id,
                        InstanceId = instanceId,
                        RelativePath = relativePath,
                        StoragePath = path,
                        CreatedAtUnixMilliseconds = createdAt.ToUnixTimeMilliseconds(),
                        Size = bytes.LongLength,
                        Sha256 = Revision(bytes),
                    });
                    _ = await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                    LocalConfigBackupRow[] excessRows = await context.ConfigBackups
                        .Where(row => row.InstanceId == instanceId && row.RelativePath == relativePath)
                        .OrderByDescending(row => row.CreatedAtUnixMilliseconds)
                        .ThenByDescending(row => row.Id)
                        .Skip(10)
                        .ToArrayAsync(cancellationToken).ConfigureAwait(false);
                    foreach (LocalConfigBackupRow old in excessRows)
                    {
                        EnsureBackupPath(old.StoragePath);
                        if (File.Exists(old.StoragePath))
                        {
                            File.Delete(old.StoragePath);
                        }
                    }

                    context.ConfigBackups.RemoveRange(excessRows);
                    _ = await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                }, cancellationToken).ConfigureAwait(false);
                return;
            }
            catch
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }

                throw;
            }
        }

        string[] excess = [.. Directory.EnumerateFiles(directory, "*.bak").OrderByDescending(candidate => Path.GetFileName(candidate), StringComparer.Ordinal).Skip(10)];
        foreach (string old in excess)
        {
            File.Delete(old);
        }
    }

    private string BackupDirectory(Guid instanceId, string relativePath)
    {
        string escaped = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(relativePath.ToUpperInvariant())));
        return Path.Combine(options.DataRoot, "backups", instanceId.ToString("N"), escaped);
    }

    private void EnsureBackupPath(string path)
    {
        string fullPath = Path.GetFullPath(path);
        string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.Combine(options.DataRoot, "backups")));
        if (!fullPath.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Refusing to access a configuration backup outside the Agent backup root.");
        }
    }

    private static IReadOnlyDictionary<string, string?> ReadValues(string format, byte[] bytes)
    {
        if (format == "properties")
        {
            var document = PreservingPropertiesDocument.Parse(bytes);
            return document.GetValues().ToDictionary(pair => pair.Key, pair => (string?)pair.Value, StringComparer.Ordinal);
        }

        return format == "text"
            ? new Dictionary<string, string?>(StringComparer.Ordinal)
            : PreservingStructuredDocument.Parse(format, bytes).GetValues();
    }

    private static byte[] ApplyValues(string format, byte[] original, IReadOnlyDictionary<string, string?> values)
    {
        if (format == "properties")
        {
            var document = PreservingPropertiesDocument.Parse(original);
            foreach ((string key, string? value) in values)
            {
                document.SetValue(key, value ?? string.Empty);
            }

            return document.Write();
        }

        var structured = PreservingStructuredDocument.Parse(format, original);
        structured.SetValues(values);
        return structured.Write();
    }

    private static void AddGlobConfigurations(string root, string directory, string pattern, string prefix, string format, List<ConfigurationDto> target)
    {
        string fullDirectory = Path.Combine(root, directory.Replace('/', Path.DirectorySeparatorChar));
        if (!Directory.Exists(fullDirectory) || IsReparsePoint(fullDirectory))
        {
            return;
        }

        foreach (string file in Directory.EnumerateFiles(fullDirectory, pattern, SearchOption.TopDirectoryOnly).Where(path => !IsReparsePoint(path)))
        {
            string relative = Path.GetRelativePath(root, file).Replace('\\', '/');
            target.Add(new(prefix + "-" + Path.GetFileNameWithoutExtension(file), relative, format, false, [relative], File.GetLastWriteTimeUtc(file)));
        }
    }

    private static async Task AtomicWriteAsync(string path, byte[] bytes, CancellationToken cancellationToken)
    {
        string temporary = path + ".servitium-" + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (FileStream stream = new(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            File.Move(temporary, path, true);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    private async Task<LocalInstanceSnapshot> RequireInstanceAsync(Guid instanceId, CancellationToken cancellationToken)
    {
        LocalInstanceSnapshot instance = await instances.FindAsync(instanceId, cancellationToken).ConfigureAwait(false)
            ?? throw new DomainException("not-found", "The instance registration does not exist.");
        WindowsPathPolicy.EnsureNoReparsePoint(instance.WorkingDirectory);
        return instance;
    }

    private async Task<LocalInstanceSnapshot> RequireStoppedInstanceAsync(Guid instanceId, CancellationToken cancellationToken)
    {
        LocalInstanceSnapshot instance = await RequireInstanceAsync(instanceId, cancellationToken).ConfigureAwait(false);
        return instance.Status is "Stopped" or "Crashed"
            ? instance
            : throw new DomainException("instance-not-stopped", "Configuration changes require a stopped instance.");
    }

    private static string ResolvePath(string root, string relativePath)
    {
        string path = Path.GetFullPath(Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        string normalizedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        if (!path.StartsWith(normalizedRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || IsReparsePoint(path))
        {
            throw new DomainException("path-escapes-root", "The configuration path escapes the instance root.");
        }

        WindowsPathPolicy.EnsureNoReparsePoint(Path.GetDirectoryName(path)!);
        return path;
    }

    private static bool IsReparsePoint(string path)
    {
        return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
    }

    private static string DecodeUtf8(byte[] bytes)
    {
        try
        {
            bool bom = bytes.AsSpan().StartsWith(Encoding.UTF8.Preamble);
            return new UTF8Encoding(false, true).GetString(bom ? bytes.AsSpan(Encoding.UTF8.Preamble.Length) : bytes);
        }
        catch (DecoderFallbackException exception)
        {
            throw new DomainException("config-parse-failed", exception.Message);
        }
    }

    private static byte[] EncodeUtf8(string value, bool bom)
    {
        byte[] content = new UTF8Encoding(false, true).GetBytes(value);
        if (!bom)
        {
            return content;
        }

        byte[] result = new byte[Encoding.UTF8.Preamble.Length + content.Length];
        Encoding.UTF8.Preamble.CopyTo(result.AsSpan());
        content.CopyTo(result.AsSpan(Encoding.UTF8.Preamble.Length));
        return result;
    }

    private static string DetectLineEnding(string value)
    {
        bool crlf = value.Contains("\r\n", StringComparison.Ordinal);
        bool lf = value.Replace("\r\n", string.Empty, StringComparison.Ordinal).Contains('\n', StringComparison.Ordinal);
        bool cr = value.Replace("\r\n", string.Empty, StringComparison.Ordinal).Contains('\r', StringComparison.Ordinal);
        int kinds = (crlf ? 1 : 0) + (lf ? 1 : 0) + (cr ? 1 : 0);
        return kinds > 1 ? "mixed" : crlf ? "crlf" : lf ? "lf" : cr ? "cr" : "none";
    }

    private static string Revision(byte[] bytes)
    {
        return Convert.ToHexString(SHA256.HashData(bytes));
    }

    private static bool FixedHashEquals(string expected, string actual)
    {
        return expected.Length == actual.Length && CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(expected), Encoding.ASCII.GetBytes(actual));
    }
}
