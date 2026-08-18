using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

using Microsoft.Win32.SafeHandles;

using Servitium.Agent.Security;

namespace Servitium.Agent.Processes;

internal sealed partial class ConPtySession : IDisposable
{
    private const uint ExtendedStartupInfoPresent = 0x00080000;
    private const uint CreateUnicodeEnvironment = 0x00000400;
    private const uint CreateSuspended = 0x00000004;
    private const uint StartfUseStdHandles = 0x00000100;
    private static readonly IntPtr ProcThreadAttributePseudoConsole = new(0x00020016);

    private readonly SafePseudoConsoleHandle _pseudoConsole;
    private readonly FileStream _input;
    private readonly FileStream _output;
    private bool _disposed;

    private ConPtySession(Process process, SafePseudoConsoleHandle pseudoConsole, FileStream input, FileStream output)
    {
        Process = process;
        _pseudoConsole = pseudoConsole;
        _input = input;
        _output = output;
    }

    internal Process Process { get; }
    internal Stream Input => _input;
    internal Stream Output => _output;

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope", Justification = "The FileStream takes ownership of each SafeFileHandle and the returned session owns both streams.")]
    internal static ConPtySession Start(
        ProcessStartInfo startInfo,
        WindowsJobObject job,
        int columns,
        int rows,
        RunCredential? credential = null)
    {
        ArgumentNullException.ThrowIfNull(startInfo);
        ArgumentNullException.ThrowIfNull(job);
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17763))
        {
            throw new PlatformNotSupportedException("ConPTY requires Windows 10 version 1809 or later.");
        }

        IntPtr pseudoInputRead = IntPtr.Zero;
        IntPtr parentInputWrite = IntPtr.Zero;
        IntPtr parentOutputRead = IntPtr.Zero;
        IntPtr pseudoOutputWrite = IntPtr.Zero;
        IntPtr attributeList = IntPtr.Zero;
        IntPtr environment = IntPtr.Zero;
        IntPtr commandLineBuffer = IntPtr.Zero;
        IntPtr userToken = IntPtr.Zero;
        SafePseudoConsoleHandle? pseudoConsole = null;
        FileStream? input = null;
        FileStream? output = null;
        Process? process = null;
        ProcessInformation processInformation = default;
        try
        {
            EnsureSuccess(NativeMethods.CreatePipe(out pseudoInputRead, out parentInputWrite, IntPtr.Zero, 0));
            EnsureSuccess(NativeMethods.CreatePipe(out parentOutputRead, out pseudoOutputWrite, IntPtr.Zero, 0));
            int result = NativeMethods.CreatePseudoConsole(new Coord(checked((short)columns), checked((short)rows)), pseudoInputRead, pseudoOutputWrite, 0, out IntPtr pseudoConsoleHandle);
            if (result != 0)
            {
                throw new Win32Exception(result);
            }

            pseudoConsole = new SafePseudoConsoleHandle(pseudoConsoleHandle);

            IntPtr attributeBytes = IntPtr.Zero;
            _ = NativeMethods.InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref attributeBytes);
            attributeList = Marshal.AllocHGlobal(attributeBytes);
            EnsureSuccess(NativeMethods.InitializeProcThreadAttributeList(attributeList, 1, 0, ref attributeBytes));
            EnsureSuccess(NativeMethods.UpdateProcThreadAttribute(
                attributeList,
                0,
                ProcThreadAttributePseudoConsole,
                pseudoConsole.DangerousGetHandle(),
                new IntPtr(IntPtr.Size),
                IntPtr.Zero,
                IntPtr.Zero));

            StartupInfoEx startup = new()
            {
                StartupInfo = new StartupInfo
                {
                    Size = Marshal.SizeOf<StartupInfoEx>(),
                    Flags = StartfUseStdHandles,
                },
                AttributeList = attributeList,
            };
            environment = CreateEnvironmentBlock(startInfo.Environment);
            commandLineBuffer = Marshal.StringToHGlobalUni(BuildCommandLine(startInfo.FileName, startInfo.ArgumentList));
            bool created;
            if (credential is null)
            {
                created = NativeMethods.CreateProcess(
                    null,
                    commandLineBuffer,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    false,
                    ExtendedStartupInfoPresent | CreateUnicodeEnvironment | CreateSuspended,
                    environment,
                    startInfo.WorkingDirectory,
                    ref startup,
                    out processInformation);
            }
            else
            {
                string? domain = credential.Domain ?? (credential.UserName.Contains('@', StringComparison.Ordinal) ? null : ".");
                if (!NativeMethods.LogonUser(
                        credential.UserName,
                        domain,
                        credential.Password,
                        2,
                        0,
                        out userToken))
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "The Agent run credential could not be authenticated.");
                }

                created = NativeMethods.CreateProcessAsUser(
                    userToken,
                    null,
                    commandLineBuffer,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    false,
                    ExtendedStartupInfoPresent | CreateUnicodeEnvironment | CreateSuspended,
                    environment,
                    startInfo.WorkingDirectory,
                    ref startup,
                    out processInformation);
            }
            EnsureSuccess(created);

            _ = NativeMethods.CloseHandle(pseudoInputRead);
            pseudoInputRead = IntPtr.Zero;
            _ = NativeMethods.CloseHandle(pseudoOutputWrite);
            pseudoOutputWrite = IntPtr.Zero;

            process = Process.GetProcessById(checked((int)processInformation.ProcessId));
            job.Assign(process);
            if (NativeMethods.ResumeThread(processInformation.Thread) == uint.MaxValue)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            _ = NativeMethods.CloseHandle(processInformation.Thread);
            processInformation.Thread = IntPtr.Zero;
            _ = NativeMethods.CloseHandle(processInformation.Process);
            processInformation.Process = IntPtr.Zero;

            input = new FileStream(new SafeFileHandle(parentInputWrite, ownsHandle: true), FileAccess.Write, 4096, isAsync: false);
            parentInputWrite = IntPtr.Zero;
            output = new FileStream(new SafeFileHandle(parentOutputRead, ownsHandle: true), FileAccess.Read, 4096, isAsync: false);
            parentOutputRead = IntPtr.Zero;
            ConPtySession session = new(process, pseudoConsole, input, output);
            process = null;
            pseudoConsole = null;
            input = null;
            output = null;
            return session;
        }
        catch
        {
            if (processInformation.Process != IntPtr.Zero)
            {
                _ = NativeMethods.TerminateProcess(processInformation.Process, 1);
            }

            throw;
        }
        finally
        {
            process?.Dispose();
            input?.Dispose();
            output?.Dispose();
            pseudoConsole?.Dispose();
            CloseIfValid(ref pseudoInputRead);
            CloseIfValid(ref parentInputWrite);
            CloseIfValid(ref parentOutputRead);
            CloseIfValid(ref pseudoOutputWrite);
            CloseIfValid(ref processInformation.Thread);
            CloseIfValid(ref processInformation.Process);
            CloseIfValid(ref userToken);
            if (attributeList != IntPtr.Zero)
            {
                NativeMethods.DeleteProcThreadAttributeList(attributeList);
                Marshal.FreeHGlobal(attributeList);
            }

            if (environment != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(environment);
            }

            if (commandLineBuffer != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(commandLineBuffer);
            }
        }
    }

    internal void Resize(int columns, int rows)
    {
        int result = NativeMethods.ResizePseudoConsole(
            _pseudoConsole.DangerousGetHandle(),
            new Coord(checked((short)columns), checked((short)rows)));
        if (result != 0)
        {
            throw new Win32Exception(result);
        }
    }

    internal void CompleteProcessExit()
    {
        _input.Dispose();
        _pseudoConsole.Dispose();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _input.Dispose();
        _output.Dispose();
        _pseudoConsole.Dispose();
    }

    private static string BuildCommandLine(string executable, IList<string> arguments)
    {
        IEnumerable<string> all = new[] { executable }.Concat(arguments);
        return string.Join(' ', all.Select(QuoteArgument));
    }

    private static string QuoteArgument(string argument)
    {
        if (argument.Length > 32766 || argument.Contains('\0', StringComparison.Ordinal))
        {
            throw new ArgumentException("A Windows process argument is invalid.", nameof(argument));
        }

        if (argument.Length > 0 && !argument.Any(character => char.IsWhiteSpace(character) || character == '"'))
        {
            return argument;
        }

        StringBuilder quoted = new(argument.Length + 2);
        _ = quoted.Append('"');
        int backslashes = 0;
        foreach (char character in argument)
        {
            if (character == '\\')
            {
                backslashes++;
                continue;
            }

            if (character == '"')
            {
                _ = quoted.Append('\\', (backslashes * 2) + 1);
                _ = quoted.Append('"');
                backslashes = 0;
                continue;
            }

            _ = quoted.Append('\\', backslashes);
            backslashes = 0;
            _ = quoted.Append(character);
        }

        _ = quoted.Append('\\', backslashes * 2);
        _ = quoted.Append('"');
        return quoted.ToString();
    }

    private static IntPtr CreateEnvironmentBlock(IDictionary<string, string?> variables)
    {
        string block = string.Join('\0', variables
            .Where(item => !string.IsNullOrEmpty(item.Key) && item.Value is not null)
            .OrderBy(item => item.Key, StringComparer.OrdinalIgnoreCase)
            .Select(item => item.Key + "=" + item.Value)) + "\0\0";
        return Marshal.StringToHGlobalUni(block);
    }

    private static void EnsureSuccess(bool succeeded)
    {
        if (!succeeded)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }
    }

    private static void CloseIfValid(ref IntPtr handle)
    {
        if (handle != IntPtr.Zero && handle != new IntPtr(-1))
        {
            _ = NativeMethods.CloseHandle(handle);
            handle = IntPtr.Zero;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct Coord(short x, short y)
    {
        internal readonly short X = x;
        internal readonly short Y = y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfo
    {
        internal int Size;
        internal IntPtr Reserved;
        internal IntPtr Desktop;
        internal IntPtr Title;
        internal uint X;
        internal uint Y;
        internal uint XSize;
        internal uint YSize;
        internal uint XCountChars;
        internal uint YCountChars;
        internal uint FillAttribute;
        internal uint Flags;
        internal ushort ShowWindow;
        internal ushort Reserved2;
        internal IntPtr Reserved2Pointer;
        internal IntPtr StandardInput;
        internal IntPtr StandardOutput;
        internal IntPtr StandardError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfoEx
    {
        internal StartupInfo StartupInfo;
        internal IntPtr AttributeList;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        internal IntPtr Process;
        internal IntPtr Thread;
        internal uint ProcessId;
        internal uint ThreadId;
    }

    private sealed class SafePseudoConsoleHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        internal SafePseudoConsoleHandle(IntPtr value)
            : base(ownsHandle: true)
        {
            SetHandle(value);
        }

        protected override bool ReleaseHandle()
        {
            NativeMethods.ClosePseudoConsole(handle);
            return true;
        }
    }

    private static partial class NativeMethods
    {
        [LibraryImport("kernel32.dll", SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool CreatePipe(out IntPtr readPipe, out IntPtr writePipe, IntPtr pipeAttributes, uint size);

        [LibraryImport("kernel32.dll")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        internal static partial int CreatePseudoConsole(Coord size, IntPtr input, IntPtr output, uint flags, out IntPtr pseudoConsole);

        [LibraryImport("kernel32.dll")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        internal static partial int ResizePseudoConsole(IntPtr pseudoConsole, Coord size);

        [LibraryImport("kernel32.dll")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        internal static partial void ClosePseudoConsole(IntPtr pseudoConsole);

        [LibraryImport("kernel32.dll", SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool InitializeProcThreadAttributeList(IntPtr attributeList, int count, uint flags, ref IntPtr size);

        [LibraryImport("kernel32.dll", SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool UpdateProcThreadAttribute(
            IntPtr attributeList,
            uint flags,
            IntPtr attribute,
            IntPtr value,
            IntPtr size,
            IntPtr previousValue,
            IntPtr returnSize);

        [LibraryImport("kernel32.dll")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        internal static partial void DeleteProcThreadAttributeList(IntPtr attributeList);

        [LibraryImport("kernel32.dll", EntryPoint = "CreateProcessW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool CreateProcess(
            string? applicationName,
            IntPtr commandLine,
            IntPtr processAttributes,
            IntPtr threadAttributes,
            [MarshalAs(UnmanagedType.Bool)] bool inheritHandles,
            uint creationFlags,
            IntPtr environment,
            string currentDirectory,
            ref StartupInfoEx startupInfo,
            out ProcessInformation processInformation);

        [LibraryImport("advapi32.dll", EntryPoint = "LogonUserW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool LogonUser(
            string userName,
            string? domain,
            string password,
            int logonType,
            int logonProvider,
            out IntPtr token);

        [LibraryImport("advapi32.dll", EntryPoint = "CreateProcessAsUserW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool CreateProcessAsUser(
            IntPtr token,
            string? applicationName,
            IntPtr commandLine,
            IntPtr processAttributes,
            IntPtr threadAttributes,
            [MarshalAs(UnmanagedType.Bool)] bool inheritHandles,
            uint creationFlags,
            IntPtr environment,
            string currentDirectory,
            ref StartupInfoEx startupInfo,
            out ProcessInformation processInformation);

        [LibraryImport("kernel32.dll", SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        internal static partial uint ResumeThread(IntPtr thread);

        [LibraryImport("kernel32.dll", SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool TerminateProcess(IntPtr process, uint exitCode);

        [LibraryImport("kernel32.dll", SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        internal static partial int CloseHandle(IntPtr handle);
    }
}
