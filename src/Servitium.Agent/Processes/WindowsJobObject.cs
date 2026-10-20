using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

using Microsoft.Win32.SafeHandles;

namespace Servitium.Agent.Processes;

internal sealed partial class WindowsJobObject : IDisposable
{
    private const uint JobObjectLimitKillOnJobClose = 0x00002000;
    private readonly SafeJobHandle _handle;

    internal WindowsJobObject()
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Servitium process containment requires Windows Job Objects.");
        }

        _handle = new SafeJobHandle(NativeMethods.CreateJobObject(IntPtr.Zero, null));
        if (_handle.IsInvalid)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        JobObjectExtendedLimitInformation information = new()
        {
            BasicLimitInformation = new JobObjectBasicLimitInformation
            {
                LimitFlags = JobObjectLimitKillOnJobClose,
            },
        };
        if (NativeMethods.SetInformationJobObject(
                _handle.DangerousGetHandle(),
                9,
                ref information,
                (uint)Marshal.SizeOf<JobObjectExtendedLimitInformation>()) == 0)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }
    }

    internal void Assign(Process process)
    {
        ArgumentNullException.ThrowIfNull(process);
        if (NativeMethods.AssignProcessToJobObject(_handle.DangerousGetHandle(), process.Handle) == 0)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }
    }

    internal void Terminate(uint exitCode)
    {
        if (NativeMethods.TerminateJobObject(_handle.DangerousGetHandle(), exitCode) == 0)
        {
            int error = Marshal.GetLastWin32Error();
            if (error != 5)
            {
                throw new Win32Exception(error);
            }
        }
    }

    internal (TimeSpan ProcessorTime, long WorkingSetBytes, int HandleCount, int ThreadCount) QueryUsage()
    {
        const int headerBytes = sizeof(uint) * 2;
        int pointerSize = IntPtr.Size;
        int capacity = 16;
        while (capacity <= 4096)
        {
            int size = checked(headerBytes + (pointerSize * capacity));
            IntPtr buffer = Marshal.AllocHGlobal(size);
            try
            {
                if (NativeMethods.QueryInformationJobObject(_handle.DangerousGetHandle(), 3, buffer, (uint)size, out _) == 0)
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                }

                uint assigned = unchecked((uint)Marshal.ReadInt32(buffer));
                uint listed = unchecked((uint)Marshal.ReadInt32(buffer, sizeof(uint)));
                if (assigned > capacity)
                {
                    capacity = checked((int)assigned + 8);
                    continue;
                }

                TimeSpan processor = TimeSpan.Zero;
                long workingSet = 0;
                int handleCount = 0;
                int threadCount = 0;
                for (int index = 0; index < listed; index++)
                {
                    long processId = pointerSize == 8
                        ? Marshal.ReadInt64(buffer, headerBytes + (index * pointerSize))
                        : Marshal.ReadInt32(buffer, headerBytes + (index * pointerSize));
                    try
                    {
                        using var process = Process.GetProcessById(checked((int)processId));
                        processor += process.TotalProcessorTime;
                        workingSet = checked(workingSet + process.WorkingSet64);
                        handleCount = checked(handleCount + process.HandleCount);
                        threadCount = checked(threadCount + process.Threads.Count);
                    }
                    catch (ArgumentException)
                    {
                    }
                    catch (InvalidOperationException)
                    {
                    }
                }

                return (processor, workingSet, handleCount, threadCount);
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        throw new InvalidOperationException("The Job Object process list exceeds the supported capacity.");
    }

    public void Dispose()
    {
        _handle.Dispose();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectBasicLimitInformation
    {
        internal long PerProcessUserTimeLimit;
        internal long PerJobUserTimeLimit;
        internal uint LimitFlags;
        internal UIntPtr MinimumWorkingSetSize;
        internal UIntPtr MaximumWorkingSetSize;
        internal uint ActiveProcessLimit;
        internal UIntPtr Affinity;
        internal uint PriorityClass;
        internal uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        internal ulong ReadOperationCount;
        internal ulong WriteOperationCount;
        internal ulong OtherOperationCount;
        internal ulong ReadTransferCount;
        internal ulong WriteTransferCount;
        internal ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectExtendedLimitInformation
    {
        internal JobObjectBasicLimitInformation BasicLimitInformation;
        internal IoCounters IoInfo;
        internal UIntPtr ProcessMemoryLimit;
        internal UIntPtr JobMemoryLimit;
        internal UIntPtr PeakProcessMemoryUsed;
        internal UIntPtr PeakJobMemoryUsed;
    }

    private sealed class SafeJobHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        internal SafeJobHandle(IntPtr handle)
            : base(ownsHandle: true)
        {
            SetHandle(handle);
        }

        protected override bool ReleaseHandle()
        {
            return NativeMethods.CloseHandle(handle) != 0;
        }
    }

    private static partial class NativeMethods
    {
        [LibraryImport("kernel32.dll", EntryPoint = "CreateJobObjectW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        internal static partial IntPtr CreateJobObject(IntPtr jobAttributes, string? name);

        [LibraryImport("kernel32.dll", SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        internal static partial int SetInformationJobObject(
            IntPtr job,
            int informationClass,
            ref JobObjectExtendedLimitInformation information,
            uint informationLength);

        [LibraryImport("kernel32.dll", SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        internal static partial int AssignProcessToJobObject(IntPtr job, IntPtr process);

        [LibraryImport("kernel32.dll", SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        internal static partial int TerminateJobObject(IntPtr job, uint exitCode);

        [LibraryImport("kernel32.dll", SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        internal static partial int QueryInformationJobObject(
            IntPtr job,
            int informationClass,
            IntPtr information,
            uint informationLength,
            out uint returnLength);

        [LibraryImport("kernel32.dll", SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        internal static partial int CloseHandle(IntPtr handle);
    }
}
