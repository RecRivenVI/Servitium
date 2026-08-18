using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Win32.SafeHandles;

using Servitium.Application.Abstractions;
using Servitium.Application.Identity;
using Servitium.Contracts;
using Servitium.Contracts.Identity;
using Servitium.Server.Hosting;

namespace Servitium.Server.Security;

internal sealed record LocalPipePeer(uint ProcessId, string Sid, string ExecutablePath);

internal interface ILocalPipePeerInspector
{
    LocalPipePeer Inspect(SafePipeHandle pipeHandle);
}

internal sealed partial class WindowsLocalPipePeerInspector : ILocalPipePeerInspector
{
    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const uint TokenQuery = 0x0008;
    private const int TokenUser = 1;

    public LocalPipePeer Inspect(SafePipeHandle pipeHandle)
    {
        ArgumentNullException.ThrowIfNull(pipeHandle);
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Local authentication requires Windows.");
        }

        if (!GetNamedPipeClientProcessId(pipeHandle, out uint processId))
        {
            throw new InvalidOperationException("The named-pipe client process could not be identified.");
        }

        using SafeFileHandle process = OpenProcess(ProcessQueryLimitedInformation, false, processId);
        if (process.IsInvalid)
        {
            throw new InvalidOperationException("The named-pipe client process could not be opened.");
        }

        string executablePath = ReadExecutablePath(process);

        if (!OpenProcessToken(process, TokenQuery, out SafeAccessTokenHandle token))
        {
            throw new InvalidOperationException("The named-pipe client token could not be opened.");
        }

        using (token)
        {
            _ = GetTokenInformation(token, TokenUser, IntPtr.Zero, 0, out uint required);
            if (required == 0)
            {
                throw new InvalidOperationException("The named-pipe client SID size could not be read.");
            }

            IntPtr buffer = Marshal.AllocHGlobal(checked((int)required));
            try
            {
                if (!GetTokenInformation(token, TokenUser, buffer, required, out _))
                {
                    throw new InvalidOperationException("The named-pipe client SID could not be read.");
                }

                TokenUserValue tokenUser = Marshal.PtrToStructure<TokenUserValue>(buffer);
                if (!ConvertSidToStringSid(tokenUser.User.Sid, out IntPtr sidPointer))
                {
                    throw new InvalidOperationException("The named-pipe client SID could not be formatted.");
                }

                try
                {
                    string sid = Marshal.PtrToStringUni(sidPointer) ?? throw new InvalidOperationException("The named-pipe client SID is empty.");
                    return new(processId, sid.ToUpperInvariant(), Path.GetFullPath(executablePath));
                }
                finally
                {
                    _ = LocalFree(sidPointer);
                }
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
    }

    private static unsafe string ReadExecutablePath(SafeFileHandle process)
    {
        Span<char> path = stackalloc char[32_768];
        uint pathLength = (uint)path.Length;
        fixed (char* pathPointer = path)
        {
            if (!QueryFullProcessImageName(process, 0, pathPointer, ref pathLength))
            {
                throw new InvalidOperationException("The named-pipe client image path could not be read.");
            }
        }

        return new string(path[..checked((int)pathLength)]);
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct SidAndAttributes
    {
        internal readonly IntPtr Sid;
        internal readonly uint Attributes;
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct TokenUserValue
    {
        internal readonly SidAndAttributes User;
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint clientProcessId);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial SafeFileHandle OpenProcess(uint desiredAccess, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, uint processId);

    [LibraryImport("kernel32.dll", EntryPoint = "QueryFullProcessImageNameW", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool QueryFullProcessImageName(SafeFileHandle process, uint flags, char* executablePath, ref uint size);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool OpenProcessToken(SafeFileHandle process, uint desiredAccess, out SafeAccessTokenHandle token);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetTokenInformation(SafeAccessTokenHandle token, int informationClass, IntPtr information, uint informationLength, out uint returnLength);

    [LibraryImport("advapi32.dll", EntryPoint = "ConvertSidToStringSidW", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ConvertSidToStringSid(IntPtr sid, out IntPtr stringSid);

    [LibraryImport("kernel32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial IntPtr LocalFree(IntPtr memory);
}

internal sealed class LocalAuthenticationPipeService(
    ServerHostOptions options,
    ILocalPipePeerInspector peerInspector,
    IIdentityStore identityStore,
    IdentityApplicationService identityService,
    IClock clock,
    ILogger<LocalAuthenticationPipeService> logger) : BackgroundService
{
    private const int MaximumLineLength = 4096;
    private const int WorkerCount = 4;
    private readonly ConcurrentDictionary<uint, Queue<DateTimeOffset>> _requestsByProcess = new();

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        return !options.LocalAuthenticationEnabled || !OperatingSystem.IsWindows()
            ? Task.CompletedTask
            : string.IsNullOrWhiteSpace(options.LocalAuthenticationPipeName) || options.LocalAuthenticationPipeName.IndexOfAny(['\\', '/']) >= 0
            ? throw new InvalidOperationException("The local authentication pipe name is invalid.")
            : !Path.IsPathFullyQualified(options.InstallDirectory)
            ? throw new InvalidOperationException("The Servitium install directory must be absolute.")
            : Task.WhenAll(CreateWorkers(stoppingToken));
    }

    private Task[] CreateWorkers(CancellationToken stoppingToken)
    {
        var workers = new Task[WorkerCount];
        for (int index = 0; index < workers.Length; index++)
        {
            workers[index] = AcceptLoopAsync(stoppingToken);
        }

        return workers;
    }

    private async Task AcceptLoopAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            using NamedPipeServerStream pipe = CreatePipe();
            try
            {
                await pipe.WaitForConnectionAsync(stoppingToken).ConfigureAwait(false);
                await HandleConnectionAsync(pipe, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                LocalPipeLog.ConnectionFailed(logger, exception);
            }
        }
    }

    private NamedPipeServerStream CreatePipe()
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Local authentication requires Windows.");
        }

        using var identity = WindowsIdentity.GetCurrent(TokenAccessLevels.Query);
        SecurityIdentifier serviceSid = identity.User ?? throw new InvalidOperationException("The Server service account has no Windows SID.");
        PipeSecurity security = new();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(serviceSid, PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null), PipeAccessRights.ReadWrite, AccessControlType.Allow));
        return NamedPipeServerStreamAcl.Create(
            options.LocalAuthenticationPipeName,
            PipeDirection.InOut,
            NamedPipeServerStream.MaxAllowedServerInstances,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.WriteThrough,
            MaximumLineLength,
            MaximumLineLength,
            security);
    }

    private async Task HandleConnectionAsync(NamedPipeServerStream pipe, CancellationToken cancellationToken)
    {
        LocalPipeResponse response;
        try
        {
            LocalPipePeer peer = peerInspector.Inspect(pipe.SafePipeHandle);
            response = IsRateLimited(peer.ProcessId, clock.UtcNow)
                ? LocalPipeResponse.Failure(ErrorCodes.RateLimited)
                : !IsAllowedExecutable(peer.ExecutablePath, options.InstallDirectory)
                    ? LocalPipeResponse.Failure(ErrorCodes.Forbidden)
                    : await ReadAndIssueAsync(pipe, peer, cancellationToken).ConfigureAwait(false);
        }
        catch (JsonException)
        {
            response = LocalPipeResponse.Failure(ErrorCodes.ValidationFailed);
        }
        catch (IOException exception)
        {
            LocalPipeLog.ConnectionFailed(logger, exception);
            return;
        }
        catch (Exception exception) when (exception is InvalidOperationException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            LocalPipeLog.ConnectionFailed(logger, exception);
            response = LocalPipeResponse.Failure(ErrorCodes.Unauthenticated);
        }

        await WriteResponseAsync(pipe, response, cancellationToken).ConfigureAwait(false);
    }

    private async Task<LocalPipeResponse> ReadAndIssueAsync(NamedPipeServerStream pipe, LocalPipePeer peer, CancellationToken cancellationToken)
    {
        using StreamReader reader = new(pipe, new UTF8Encoding(false, true), false, MaximumLineLength, leaveOpen: true);
        string? line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
        if (line is null || Encoding.UTF8.GetByteCount(line) > MaximumLineLength)
        {
            return LocalPipeResponse.Failure(ErrorCodes.ValidationFailed);
        }

        LocalPipeRequest? request = JsonSerializer.Deserialize<LocalPipeRequest>(line, JsonOptions);
        if (request is null || !string.Equals(request.Action, "issue-session", StringComparison.Ordinal) || string.IsNullOrWhiteSpace(request.ClientVersion))
        {
            return LocalPipeResponse.Failure(ErrorCodes.ValidationFailed);
        }

        StoredWindowsIdentity? binding = await identityStore.FindWindowsIdentityBySidAsync(peer.Sid, cancellationToken).ConfigureAwait(false);
        if (binding is null)
        {
            return LocalPipeResponse.Failure(ErrorCodes.Unauthenticated);
        }

        StoredUser? user = await identityStore.FindUserAsync(binding.UserId, cancellationToken).ConfigureAwait(false);
        if (user is null || user.IsDeleted || user.Role == Domain.Identity.Role.Disabled)
        {
            return LocalPipeResponse.Failure(ErrorCodes.AccountDisabled);
        }

        string token = await identityService.IssueLocalSessionTicketAsync(user.Id, cancellationToken).ConfigureAwait(false);
        return LocalPipeResponse.Success(token, clock.UtcNow.AddSeconds(60), new UserDto(user.Id, user.UserName, user.DisplayName, user.Role.ToString(), false, user.LastSignedInAt, user.CreatedAt));
    }

    private bool IsRateLimited(uint processId, DateTimeOffset now)
    {
        Queue<DateTimeOffset> requests = _requestsByProcess.GetOrAdd(processId, _ => new Queue<DateTimeOffset>());
        lock (requests)
        {
            DateTimeOffset cutoff = now.AddMinutes(-1);
            while (requests.TryPeek(out DateTimeOffset request) && request < cutoff)
            {
                _ = requests.Dequeue();
            }

            if (requests.Count >= 10)
            {
                return true;
            }

            requests.Enqueue(now);
            return false;
        }
    }

    internal static bool IsAllowedExecutable(string executablePath, string installDirectory)
    {
        string fullExecutablePath = Path.GetFullPath(executablePath);
        if (!string.Equals(Path.GetFileName(fullExecutablePath), "Servitium.Desktop.exe", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        string fullInstallDirectory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(installDirectory));
        string relative = Path.GetRelativePath(fullInstallDirectory, fullExecutablePath);
        return !Path.IsPathFullyQualified(relative) && relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal);
    }

    private static async Task WriteResponseAsync(Stream pipe, LocalPipeResponse response, CancellationToken cancellationToken)
    {
        using StreamWriter writer = new(pipe, new UTF8Encoding(false), MaximumLineLength, leaveOpen: true) { AutoFlush = true };
        await writer.WriteLineAsync(JsonSerializer.Serialize(response, JsonOptions).AsMemory(), cancellationToken).ConfigureAwait(false);
    }

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private sealed record LocalPipeRequest(string Action, string ClientVersion);

    private sealed record LocalPipeError(string Code, string Detail);

    private sealed record LocalPipeResponse(string? SessionToken, DateTimeOffset? ExpiresAt, UserDto? User, LocalPipeError? Error)
    {
        internal static LocalPipeResponse Success(string token, DateTimeOffset expiresAt, UserDto user)
        {
            return new(token, expiresAt, user, null);
        }

        internal static LocalPipeResponse Failure(string code)
        {
            return new(null, null, null, new(code, code));
        }
    }
}

internal static partial class LocalPipeLog
{
    [LoggerMessage(2001, LogLevel.Warning, "Local authentication pipe connection failed")]
    internal static partial void ConnectionFailed(ILogger logger, Exception exception);
}
