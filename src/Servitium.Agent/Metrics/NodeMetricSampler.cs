using System;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Threading;
using Servitium.Agent.Storage;
using Servitium.Application.Abstractions;
using Servitium.Contracts.Nodes;

namespace Servitium.Agent.Metrics;

/// <summary>采集 Windows 节点 CPU、内存、数据卷、网络与运行时间。</summary>
internal sealed partial class NodeMetricSampler(AgentStorageOptions storage, IClock clock)
{
    private readonly Lock _gate = new();
    private CpuTimes? _previousCpu;
    private NetworkTotals? _previousNetwork;
    private DateTimeOffset? _previousSampledAt;

    internal NodeMetricsDto Sample()
    {
        DateTimeOffset now = clock.UtcNow;
        lock (_gate)
        {
            CpuTimes cpu = ReadCpuTimes();
            NetworkTotals network = ReadNetworkTotals();
            double cpuPercent = CalculateCpu(_previousCpu, cpu);
            (long receiveRate, long sendRate) = CalculateNetwork(_previousNetwork, network, _previousSampledAt, now);
            _previousCpu = cpu;
            _previousNetwork = network;
            _previousSampledAt = now;

            MemoryStatus memory = new() { Length = (uint)Marshal.SizeOf<MemoryStatus>() };
            if (!GlobalMemoryStatusEx(ref memory))
            {
                throw new InvalidOperationException("Windows memory metrics could not be read.");
            }

            string root = Path.GetPathRoot(storage.DataRoot) ?? throw new InvalidOperationException("The Agent data root has no volume.");
            DriveInfo drive = new(root);
            long usedMemory = checked((long)Math.Min(memory.TotalPhysical - memory.AvailablePhysical, long.MaxValue));
            long totalMemory = checked((long)Math.Min(memory.TotalPhysical, long.MaxValue));
            return new(now, cpuPercent, usedMemory, totalMemory, drive.AvailableFreeSpace, receiveRate, sendRate, TimeSpan.FromMilliseconds(Environment.TickCount64));
        }
    }

    private static CpuTimes ReadCpuTimes()
    {
        return !OperatingSystem.IsWindows() || !GetSystemTimes(out FileTime idle, out FileTime kernel, out FileTime user)
            ? throw new PlatformNotSupportedException("Node metrics require Windows system counters.")
            : new(ToUInt64(idle), ToUInt64(kernel), ToUInt64(user));
    }

    private static NetworkTotals ReadNetworkTotals()
    {
        long received = 0;
        long sent = 0;
        foreach (NetworkInterface adapter in NetworkInterface.GetAllNetworkInterfaces().Where(item => item.OperationalStatus == OperationalStatus.Up))
        {
            try
            {
                IPv4InterfaceStatistics statistics = adapter.GetIPv4Statistics();
                received = checked(received + statistics.BytesReceived);
                sent = checked(sent + statistics.BytesSent);
            }
            catch (NetworkInformationException)
            {
            }
        }

        return new(received, sent);
    }

    private static double CalculateCpu(CpuTimes? previous, CpuTimes current)
    {
        if (previous is null)
        {
            return 0;
        }

        ulong idle = current.Idle - previous.Idle;
        ulong total = current.Kernel - previous.Kernel + current.User - previous.User;
        return total == 0 ? 0 : Math.Clamp((double)(total - Math.Min(idle, total)) / total * 100, 0, 100);
    }

    private static (long Receive, long Send) CalculateNetwork(NetworkTotals? previous, NetworkTotals current, DateTimeOffset? previousAt, DateTimeOffset now)
    {
        double seconds = previousAt is null ? 0 : (now - previousAt.Value).TotalSeconds;
        if (previous is null || seconds <= 0)
        {
            return (0, 0);
        }

        return (
            Math.Max(0, (long)((current.Received - previous.Received) / seconds)),
            Math.Max(0, (long)((current.Sent - previous.Sent) / seconds)));
    }

    private static ulong ToUInt64(FileTime value)
    {
        return ((ulong)value.High << 32) | value.Low;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileTime
    {
        internal uint Low;
        internal uint High;
    }

    [StructLayout(LayoutKind.Sequential)]
    [SuppressMessage("Performance", "CA1815:Override equals and operator equals on value types", Justification = "The structure is a native output buffer and is never compared.")]
    private struct MemoryStatus
    {
        internal uint Length;
        internal uint MemoryLoad;
        internal ulong TotalPhysical;
        internal ulong AvailablePhysical;
        internal ulong TotalPageFile;
        internal ulong AvailablePageFile;
        internal ulong TotalVirtual;
        internal ulong AvailableVirtual;
        internal ulong AvailableExtendedVirtual;
    }

    private sealed record CpuTimes(ulong Idle, ulong Kernel, ulong User);
    private sealed record NetworkTotals(long Received, long Sent);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetSystemTimes(out FileTime idleTime, out FileTime kernelTime, out FileTime userTime);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GlobalMemoryStatusEx(ref MemoryStatus buffer);
}
