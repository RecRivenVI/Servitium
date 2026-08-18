using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;

using Servitium.Domain.Common;

namespace Servitium.Agent.Paths;

/// <summary>Agent 侧权威 Windows 路径校验器。</summary>
internal interface IWindowsPathPolicy
{
    string ValidateExternalWorkingDirectory(string candidate, IReadOnlyCollection<string> registeredDirectories, IReadOnlyCollection<string> protectedRoots);
    string ValidateOwnedWorkingDirectory(string candidate, string managedBaseDirectory);
    string ResolveRelativePath(string root, string relativePath);
}

/// <summary>Agent 侧权威 Windows 路径校验器。</summary>
internal sealed partial class WindowsPathPolicy : IWindowsPathPolicy
{
    /// <summary>校验并规范化待附加的外部实例工作目录。</summary>
    public string ValidateExternalWorkingDirectory(
        string candidate,
        IReadOnlyCollection<string> registeredDirectories,
        IReadOnlyCollection<string> protectedRoots)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(registeredDirectories);
        ArgumentNullException.ThrowIfNull(protectedRoots);
        string decoded = Uri.UnescapeDataString(candidate).Trim();
        ValidateAbsoluteSyntax(decoded);
        string normalized = Normalize(decoded);
        EnsureNotRoot(normalized);
        EnsureDirectoryExists(normalized);
        EnsureNoReparsePoint(normalized);
        normalized = ResolveLongPath(normalized);
        EnsureOutsideProtectedRoots(normalized, protectedRoots);
        EnsureNoRegistrationConflict(normalized, registeredDirectories);
        VerifyReadWrite(normalized);
        return normalized;
    }

    /// <summary>校验受管目录必须位于基路径下且不是基路径本身。</summary>
    public string ValidateOwnedWorkingDirectory(string candidate, string managedBaseDirectory)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(managedBaseDirectory);
        ValidateAbsoluteSyntax(candidate);
        ValidateAbsoluteSyntax(managedBaseDirectory);
        string normalized = Normalize(candidate);
        string normalizedBase = Normalize(managedBaseDirectory);
        if (PathEquals(normalized, normalizedBase) || !IsWithin(normalized, normalizedBase))
        {
            throw new DomainException("instance-path-invalid", "The owned working directory must be below the managed base directory.");
        }

        EnsureNoReparsePoint(normalizedBase);
        if (Directory.Exists(normalized))
        {
            EnsureNoReparsePoint(normalized);
        }

        return normalized;
    }

    /// <summary>把经过校验的相对路径组合到根目录内。</summary>
    public string ResolveRelativePath(string root, string relativePath)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(relativePath);
        if (relativePath.Length == 0 || relativePath.Any(char.IsControl) ||
            Path.IsPathFullyQualified(relativePath) || relativePath.StartsWith('\\') ||
            relativePath.StartsWith('/') || relativePath.Contains(':', StringComparison.Ordinal))
        {
            throw new DomainException("path-escapes-root", "The relative path is not allowed.");
        }

        string normalizedRoot = Normalize(root);
        string[] segments = relativePath.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0 || segments.Any(segment => segment is "." or ".."))
        {
            throw new DomainException("path-escapes-root", "The relative path escapes its root.");
        }

        foreach (string segment in segments)
        {
            ValidateFileName(segment);
        }

        string result = Path.GetFullPath(Path.Combine([normalizedRoot, .. segments]));
        return !IsWithin(result, normalizedRoot)
            ? throw new DomainException("path-escapes-root", "The resolved path escapes its root.")
            : result;
    }

    /// <summary>校验单个 Windows 文件名。</summary>
    public static void ValidateFileName(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        if (string.IsNullOrWhiteSpace(name) || name.EndsWith(' ') || name.EndsWith('.') ||
            name.Any(char.IsControl) || name.IndexOfAny(['<', '>', ':', '"', '/', '\\', '|', '?', '*']) >= 0)
        {
            throw new DomainException("path-name-invalid", "The file name contains characters that Windows does not allow.");
        }

        string stem = name.Split('.')[0];
        if (stem.Equals("CON", StringComparison.OrdinalIgnoreCase) ||
            stem.Equals("PRN", StringComparison.OrdinalIgnoreCase) ||
            stem.Equals("AUX", StringComparison.OrdinalIgnoreCase) ||
            stem.Equals("NUL", StringComparison.OrdinalIgnoreCase) ||
            IsNumberedDevice(stem, "COM") ||
            IsNumberedDevice(stem, "LPT"))
        {
            throw new DomainException("path-name-invalid", "The file name is a reserved Windows device name.");
        }
    }

    /// <summary>确认路径链不含任何重解析点。</summary>
    public static void EnsureNoReparsePoint(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        DirectoryInfo? current = new(Path.GetFullPath(path));
        while (current is not null && current.Exists)
        {
            if ((current.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new DomainException("path-reparse-point", "The path contains a reparse point.");
            }

            current = current.Parent;
        }
    }

    private static void ValidateAbsoluteSyntax(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Any(char.IsControl) ||
            path.StartsWith("\\\\", StringComparison.Ordinal) ||
            path.StartsWith("\\\\?\\", StringComparison.Ordinal) ||
            path.StartsWith("\\\\.\\", StringComparison.Ordinal) ||
            !Path.IsPathFullyQualified(path))
        {
            throw new DomainException("instance-path-invalid", "The working directory must be a local absolute path.");
        }

        int colon = path.IndexOf(':', StringComparison.Ordinal);
        if (colon != 1 || path.IndexOf(':', colon + 1) >= 0)
        {
            throw new DomainException("instance-path-invalid", "Device paths and alternate data streams are not allowed.");
        }
    }

    private static string Normalize(string path)
    {
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    }

    private static string ResolveLongPath(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            return Normalize(path);
        }

        char[] buffer = new char[32_768];
        uint length = GetLongPathName(path, buffer, (uint)buffer.Length);
        return length == 0 || length >= buffer.Length
            ? throw new DomainException("instance-path-invalid", "The working directory real path could not be resolved.")
            : Path.TrimEndingDirectorySeparator(new string(buffer, 0, (int)length));
    }

    private static void EnsureNotRoot(string path)
    {
        string root = Path.GetPathRoot(path) ?? string.Empty;
        if (PathEquals(path, Path.TrimEndingDirectorySeparator(root)))
        {
            throw new DomainException("instance-path-invalid", "A drive root cannot be an instance working directory.");
        }
    }

    private static void EnsureOutsideProtectedRoots(string path, IReadOnlyCollection<string> protectedRoots)
    {
        if (protectedRoots.Any(root => PathEquals(path, Normalize(root)) || IsWithin(path, Normalize(root))))
        {
            throw new DomainException("instance-path-invalid", "The working directory is inside a protected system location.");
        }
    }

    private static void EnsureNoRegistrationConflict(string path, IReadOnlyCollection<string> registeredDirectories)
    {
        if (registeredDirectories.Any(existing =>
                PathEquals(path, Normalize(existing)) ||
                IsWithin(path, Normalize(existing)) ||
                IsWithin(Normalize(existing), path)))
        {
            throw new DomainException("instance-path-conflict", "The working directory overlaps another registered instance.");
        }
    }

    private static void EnsureDirectoryExists(string path)
    {
        if (!Directory.Exists(path))
        {
            throw new DomainException("instance-path-invalid", "The working directory does not exist.");
        }
    }

    private static void VerifyReadWrite(string path)
    {
        string probe = Path.Combine(path, ".servitium-write-probe-" + Guid.NewGuid().ToString("N"));
        try
        {
            using FileStream stream = new(probe, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose);
            stream.WriteByte(0x53);
            stream.Flush(true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new DomainException("instance-path-invalid", "The working directory is not readable and writable.");
        }
    }

    private static bool IsWithin(string path, string root)
    {
        string prefix = Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar;
        return path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    private static bool PathEquals(string left, string right)
    {
        return string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsNumberedDevice(string stem, string prefix)
    {
        return stem.Length == 4 && stem.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && stem[3] is >= '1' and <= '9';
    }

    [LibraryImport("kernel32.dll", EntryPoint = "GetLongPathNameW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial uint GetLongPathName(string shortPath, [Out] char[] longPath, uint bufferLength);
}
