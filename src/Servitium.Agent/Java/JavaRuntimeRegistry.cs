using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.EntityFrameworkCore;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

using Servitium.Agent.Archives;
using Servitium.Agent.Paths;
using Servitium.Agent.Storage;
using Servitium.Application.Abstractions;
using Servitium.Contracts.Agent;
using Servitium.Domain.Common;
using Servitium.Infrastructure.Persistence;

namespace Servitium.Agent.Java;

internal sealed partial class JavaRuntimeRegistry(
    IDbContextFactory<AgentDbContext> contextFactory,
    SqliteWriteCoordinator writeCoordinator,
    IGuidFactory guidFactory,
    IClock clock,
    AgentStorageOptions? storageOptions = null,
    SafeZipExtractor? archives = null,
    SafeTarGzipExtractor? tarArchives = null,
    Hosting.AgentNodeConfigurationStore? nodeConfiguration = null)
{
    internal async Task<IReadOnlyList<AgentJavaRuntimeResponse>> ListAsync(CancellationToken cancellationToken)
    {
        using AgentDbContext context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        LocalJavaRuntimeRow[] rows = await context.JavaRuntimes.AsNoTracking()
            .OrderBy(row => row.JavaExecutablePath)
            .ToArrayAsync(cancellationToken)
            .ConfigureAwait(false);
        return [.. rows.Select(ToResponse)];
    }

    internal async Task<AgentJavaRuntimeResponse?> FindAsync(Guid id, CancellationToken cancellationToken)
    {
        using AgentDbContext context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        LocalJavaRuntimeRow? row = await context.JavaRuntimes.AsNoTracking()
            .SingleOrDefaultAsync(candidate => candidate.Id == id, cancellationToken)
            .ConfigureAwait(false);
        return row is null ? null : ToResponse(row);
    }

    internal async Task<IReadOnlyList<AgentJavaRuntimeResponse>> DiscoverAsync(CancellationToken cancellationToken)
    {
        HashSet<string> candidates = new(StringComparer.OrdinalIgnoreCase);
        string? javaHome = Environment.GetEnvironmentVariable("JAVA_HOME");
        if (!string.IsNullOrWhiteSpace(javaHome))
        {
            _ = candidates.Add(Path.Combine(javaHome, "bin", "java.exe"));
        }

        string? path = Environment.GetEnvironmentVariable("PATH");
        if (!string.IsNullOrWhiteSpace(path))
        {
            foreach (string directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (Path.IsPathFullyQualified(directory))
                {
                    _ = candidates.Add(Path.Combine(directory, "java.exe"));
                }
            }
        }

        AddCommonDirectoryCandidates(candidates);
        AddRegistryCandidates(candidates);

        IReadOnlyList<AgentJavaRuntimeResponse> registered = await ListAsync(cancellationToken).ConfigureAwait(false);
        var registeredByPath = registered.ToDictionary(item => item.ExecutablePath, StringComparer.OrdinalIgnoreCase);
        Dictionary<string, AgentJavaRuntimeResponse> discovered = new(StringComparer.OrdinalIgnoreCase);
        foreach (string candidate in candidates.Where(File.Exists).OrderBy(value => value, StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                JavaProbe probe = await ProbeAsync(candidate, cancellationToken).ConfigureAwait(false);
                discovered[probe.ExecutablePath] = registeredByPath.TryGetValue(probe.ExecutablePath, out AgentJavaRuntimeResponse? existing)
                    ? existing
                    : new(
                        Guid.Empty,
                        probe.ExecutablePath,
                        probe.Version,
                        probe.Vendor,
                        probe.Architecture,
                        "Discovered",
                        false,
                        default,
                        probe.VmName);
            }
            catch (DomainException exception) when (exception.Code == "java-runtime-invalid")
            {
            }
        }

        return [.. discovered.Values.OrderBy(item => item.ExecutablePath, StringComparer.OrdinalIgnoreCase)];
    }

    internal Task<AgentJavaRuntimeResponse> RegisterAsync(string executablePath, CancellationToken cancellationToken)
    {
        return RegisterCoreAsync(executablePath, "Manual", cancellationToken);
    }

    internal async Task<AgentJavaRuntimeResponse> ImportAsync(string archivePath, CancellationToken cancellationToken)
    {
        Guid importId = guidFactory.Create();
        AgentStorageOptions activeStorage = storageOptions ?? throw new InvalidOperationException("Java import storage is not configured.");
        string javaBase = nodeConfiguration?.Current.JavaDataDirectory is { Length: > 0 } configured
            ? Path.GetFullPath(configured)
            : activeStorage.JavaRoot;
        _ = Directory.CreateDirectory(javaBase);
        WindowsPathPolicy.EnsureNoReparsePoint(javaBase);
        string target = Path.Combine(javaBase, importId.ToString("N"));
        try
        {
            SafeArchivePlan plan = await ExtractJavaArchiveAsync(archivePath, target, cancellationToken).ConfigureAwait(false);
            string[] candidates = [.. plan.Entries
                .Where(entry => !entry.IsDirectory && (entry.RelativePath.Equals("bin/java.exe", StringComparison.OrdinalIgnoreCase) || entry.RelativePath.EndsWith("/bin/java.exe", StringComparison.OrdinalIgnoreCase)))
                .Select(entry => Path.Combine(target, entry.RelativePath.Replace('/', Path.DirectorySeparatorChar)))
                .OrderBy(path => path.Count(character => character == Path.DirectorySeparatorChar))];
            if (candidates.Length == 0)
            {
                throw new DomainException("java-runtime-invalid", "The imported archive contains no bin\\java.exe.");
            }

            JavaProbe probe = await ProbeAsync(candidates[0], cancellationToken).ConfigureAwait(false);
            return await PersistAsync(probe, "Imported", imported: true, target, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            if (Directory.Exists(target))
            {
                SafeZipExtractor.DeleteCreatedTree(target);
            }

            throw;
        }
    }

    internal async Task UnregisterAsync(Guid id, bool deleteFiles, bool force, CancellationToken cancellationToken)
    {
        await writeCoordinator.ExecuteAsync(
            async () =>
            {
                using AgentDbContext context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
                string[] launchConfigurations = await context.Instances.AsNoTracking()
                    .Select(row => row.LaunchJson)
                    .ToArrayAsync(cancellationToken)
                    .ConfigureAwait(false);
                if (!force && launchConfigurations.Any(value => value.Contains(id.ToString(), StringComparison.OrdinalIgnoreCase)))
                {
                    throw new DomainException("java-runtime-in-use", "The Java runtime is referenced by an instance.");
                }

                LocalJavaRuntimeRow? runtime = await context.JavaRuntimes.SingleOrDefaultAsync(row => row.Id == id, cancellationToken).ConfigureAwait(false) ?? throw new DomainException("not-found", "The Java runtime registration does not exist.");
                if (deleteFiles && !runtime.Imported)
                {
                    throw new DomainException("java-runtime-not-imported", "Only imported Java runtimes own files.");
                }

                if (deleteFiles)
                {
                    if (string.IsNullOrWhiteSpace(runtime.ImportedRoot))
                    {
                        throw new DomainException("java-runtime-not-imported", "The imported Java runtime is outside its owned root.");
                    }

                    string runtimeDirectory = Path.GetFullPath(runtime.ImportedRoot);
                    string executable = Path.GetFullPath(runtime.JavaExecutablePath);
                    if (!executable.StartsWith(Path.TrimEndingDirectorySeparator(runtimeDirectory) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    {
                        throw new DomainException("java-runtime-not-imported", "The imported Java runtime is outside its owned root.");
                    }

                    SafeZipExtractor.DeleteCreatedTree(runtimeDirectory);
                }

                _ = context.JavaRuntimes.Remove(runtime);
                _ = await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            },
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<AgentJavaRuntimeResponse> RegisterCoreAsync(
        string executablePath,
        string source,
        CancellationToken cancellationToken)
    {
        JavaProbe probe = await ProbeAsync(executablePath, cancellationToken).ConfigureAwait(false);
        return await PersistAsync(probe, source, imported: false, null, cancellationToken).ConfigureAwait(false);
    }

    private Task<AgentJavaRuntimeResponse> PersistAsync(JavaProbe probe, string source, bool imported, string? importedRoot, CancellationToken cancellationToken)
    {
        return writeCoordinator.ExecuteAsync(
            async () =>
            {
                using AgentDbContext context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
                LocalJavaRuntimeRow? existing = await context.JavaRuntimes
                    .SingleOrDefaultAsync(row => row.JavaExecutablePath == probe.ExecutablePath, cancellationToken)
                    .ConfigureAwait(false);
                if (existing is not null)
                {
                    existing.Version = probe.Version;
                    existing.Vendor = probe.Vendor;
                    existing.Architecture = probe.Architecture;
                    existing.VmName = probe.VmName;
                    existing.Imported |= imported;
                    existing.ImportedRoot = importedRoot ?? existing.ImportedRoot;
                    existing.Source = imported ? "Imported" : existing.Source;
                    _ = await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                    return ToResponse(existing);
                }

                LocalJavaRuntimeRow row = new()
                {
                    Id = guidFactory.Create(),
                    JavaExecutablePath = probe.ExecutablePath,
                    Version = probe.Version,
                    Vendor = probe.Vendor,
                    Architecture = probe.Architecture,
                    VmName = probe.VmName,
                    Source = source,
                    Imported = imported,
                    ImportedRoot = importedRoot,
                    RegisteredAt = clock.UtcNow,
                };
                _ = context.JavaRuntimes.Add(row);
                _ = await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                return ToResponse(row);
            },
            cancellationToken);
    }

    private static async Task<JavaProbe> ProbeAsync(string executablePath, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(executablePath) || !Path.IsPathFullyQualified(executablePath))
        {
            throw new DomainException("java-runtime-invalid", "The Java executable path must be absolute.");
        }

        string fullPath = Path.GetFullPath(executablePath);
        if (!string.Equals(Path.GetFileName(fullPath), "java.exe", StringComparison.OrdinalIgnoreCase) || !File.Exists(fullPath))
        {
            throw new DomainException("java-runtime-invalid", "The Java executable does not exist or is not named java.exe.");
        }

        fullPath = ResolveRealPath(fullPath);
        WindowsPathPolicy.EnsureNoReparsePoint(Path.GetDirectoryName(fullPath)!);

        using Process process = new();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = fullPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        process.StartInfo.ArgumentList.Add("-XshowSettings:properties");
        process.StartInfo.ArgumentList.Add("-version");
        try
        {
            if (!process.Start())
            {
                throw new DomainException("java-runtime-invalid", "The Java runtime probe did not start.");
            }

            Task<string> outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            Task<string> errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            string output = await outputTask.ConfigureAwait(false) + Environment.NewLine + await errorTask.ConfigureAwait(false);
            if (process.ExitCode != 0)
            {
                throw new DomainException("java-runtime-invalid", "The Java runtime probe failed.");
            }

            string version = ReadProperty(output, "java.version");
            string vendor = ReadProperty(output, "java.vendor");
            string vmName = ReadProperty(output, "java.vm.name");
            string rawArchitecture = ReadProperty(output, "os.arch");
            string architecture = rawArchitecture.ToUpperInvariant() switch
            {
                "AMD64" or "X86_64" => "x64",
                "AARCH64" or "ARM64" => "arm64",
                _ => throw new DomainException("java-runtime-invalid", "The Java runtime architecture is unsupported."),
            };
            return new JavaProbe(fullPath, version, vendor, architecture, vmName);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }

            throw new DomainException("java-runtime-invalid", "The Java runtime probe timed out.");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            throw new DomainException("java-runtime-invalid", "The Java runtime could not be executed.");
        }
    }

    private static string ReadProperty(string output, string property)
    {
        string prefix = property + " =";
        string? line = output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(value => value.Trim())
            .FirstOrDefault(value => value.StartsWith(prefix, StringComparison.Ordinal));
        return line is null || string.IsNullOrWhiteSpace(line[prefix.Length..])
            ? throw new DomainException("java-runtime-invalid", "The Java runtime did not report required properties.")
            : line[prefix.Length..].Trim();
    }

    private async Task<SafeArchivePlan> ExtractJavaArchiveAsync(string archivePath, string target, CancellationToken cancellationToken)
    {
        byte[] signature = new byte[4];
        FileStream stream = new(archivePath, FileMode.Open, FileAccess.Read, FileShare.Read, 4, FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using (stream.ConfigureAwait(false))
        {
            int read = await stream.ReadAsync(signature, cancellationToken).ConfigureAwait(false);
            if (read < 2)
            {
                throw new DomainException("archive-unsafe", "The Java archive is empty or truncated.");
            }
        }

        if (signature[0] == (byte)'P' && signature[1] == (byte)'K')
        {
            SafeZipExtractor extractor = archives ?? throw new InvalidOperationException("Java ZIP extraction is not configured.");
            return await extractor.ExtractToNewDirectoryAsync(archivePath, target, cancellationToken).ConfigureAwait(false);
        }

        if (signature[0] == 0x1f && signature[1] == 0x8b)
        {
            SafeTarGzipExtractor extractor = tarArchives ?? throw new InvalidOperationException("Java TAR gzip extraction is not configured.");
            return await extractor.ExtractToNewDirectoryAsync(archivePath, target, cancellationToken).ConfigureAwait(false);
        }

        throw new DomainException("archive-unsafe", "Only ZIP and gzip-compressed TAR Java archives are supported.");
    }

    private static void AddCommonDirectoryCandidates(HashSet<string> candidates)
    {
        string[] roots =
        [
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
        ];
        string[] vendors = ["Java", "Eclipse Adoptium", "Microsoft", "BellSoft", "Amazon Corretto", "Zulu", "Semeru"];
        foreach (string root in roots.Where(value => !string.IsNullOrWhiteSpace(value)))
        {
            foreach (string vendor in vendors)
            {
                AddJavaBelow(Path.Combine(root, vendor), candidates, 3);
            }

            _ = candidates.Add(Path.Combine(root, "Common Files", "Oracle", "Java", "javapath", "java.exe"));
        }

        string localPrograms = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs");
        AddJavaBelow(localPrograms, candidates, 3);
    }

    private static void AddJavaBelow(string root, HashSet<string> candidates, int remainingDepth)
    {
        if (!Directory.Exists(root) || remainingDepth < 0)
        {
            return;
        }

        string direct = Path.Combine(root, "bin", "java.exe");
        if (File.Exists(direct))
        {
            _ = candidates.Add(direct);
        }

        if (remainingDepth == 0)
        {
            return;
        }

        try
        {
            foreach (string child in Directory.EnumerateDirectories(root, "*", SearchOption.TopDirectoryOnly).Take(256))
            {
                AddJavaBelow(child, candidates, remainingDepth - 1);
            }
        }
        catch (UnauthorizedAccessException)
        {
        }
        catch (IOException)
        {
        }
    }

    private static void AddRegistryCandidates(HashSet<string> candidates)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        string[] roots =
        [
            @"SOFTWARE\JavaSoft\Java Runtime Environment",
            @"SOFTWARE\JavaSoft\JDK",
            @"SOFTWARE\JavaSoft\Java Development Kit",
            @"SOFTWARE\Eclipse Adoptium",
            @"SOFTWARE\BellSoft",
        ];
        foreach (RegistryView view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            using var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
            foreach (string root in roots)
            {
                using RegistryKey? key = machine.OpenSubKey(root, writable: false);
                if (key is not null)
                {
                    AddRegistryKeyCandidates(key, candidates, 3);
                }
            }
        }
    }

    [SupportedOSPlatform("windows")]
    private static void AddRegistryKeyCandidates(RegistryKey key, HashSet<string> candidates, int remainingDepth)
    {
        foreach (string valueName in new[] { "JavaHome", "Path", "InstallationPath" })
        {
            if (key.GetValue(valueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames) is string value && Path.IsPathFullyQualified(value))
            {
                _ = candidates.Add(Path.Combine(Environment.ExpandEnvironmentVariables(value), "bin", "java.exe"));
            }
        }

        if (remainingDepth == 0)
        {
            return;
        }

        foreach (string subkeyName in key.GetSubKeyNames().Take(256))
        {
            using RegistryKey? child = key.OpenSubKey(subkeyName, writable: false);
            if (child is not null)
            {
                AddRegistryKeyCandidates(child, candidates, remainingDepth - 1);
            }
        }
    }

    private static string ResolveRealPath(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            return Path.GetFullPath(path);
        }

        using SafeFileHandle handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        char[] buffer = new char[1024];
        uint length = GetFinalPathNameByHandle(handle, buffer, (uint)buffer.Length, 0);
        if (length == 0 || length >= buffer.Length)
        {
            throw new DomainException("java-runtime-invalid", "The Java executable real path could not be resolved.");
        }

        string resolved = new(buffer, 0, (int)length);
        return resolved.StartsWith(@"\\?\", StringComparison.Ordinal) ? resolved[4..] : resolved;
    }

    private static AgentJavaRuntimeResponse ToResponse(LocalJavaRuntimeRow row)
    {
        return new(row.Id, row.JavaExecutablePath, row.Version, row.Vendor, row.Architecture, row.Source, row.Imported, row.RegisteredAt, row.VmName);
    }

    private sealed record JavaProbe(string ExecutablePath, string Version, string Vendor, string Architecture, string VmName);

    [LibraryImport("kernel32.dll", EntryPoint = "GetFinalPathNameByHandleW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial uint GetFinalPathNameByHandle(SafeFileHandle file, [Out] char[] path, uint pathLength, uint flags);
}
