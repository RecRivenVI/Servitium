using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using Servitium.Application.Abstractions;
using Servitium.Contracts.Governance;

namespace Servitium.Server.Governance;

/// <summary>保留最近 24 小时按分钟聚合的请求与拒绝计数。</summary>
internal sealed class RequestTelemetry(IClock clock)
{
    private readonly ConcurrentDictionary<long, RequestBucket> _buckets = new();

    internal int DeniedCount => _buckets.Values.Sum(bucket => bucket.Denied);

    internal void Record(int statusCode)
    {
        long minute = clock.UtcNow.ToUnixTimeSeconds() / 60;
        RequestBucket bucket = _buckets.GetOrAdd(minute, _ => new RequestBucket());
        _ = Interlocked.Increment(ref bucket.Requests);
        if (statusCode is 401 or 403 or 429)
        {
            _ = Interlocked.Increment(ref bucket.Denied);
        }

        long cutoff = minute - (24 * 60);
        foreach (long expired in _buckets.Keys.Where(value => value < cutoff))
        {
            _ = _buckets.TryRemove(expired, out _);
        }
    }

    internal IReadOnlyList<RequestTrendPointDto> Snapshot(int instanceCount)
    {
        return [.. _buckets.OrderBy(pair => pair.Key).Select(pair => new RequestTrendPointDto(
            DateTimeOffset.FromUnixTimeSeconds(pair.Key * 60),
            pair.Value.Requests,
            pair.Value.Denied,
            instanceCount))];
    }

    private sealed class RequestBucket
    {
        internal int Requests;
        internal int Denied;
    }
}

/// <summary>计算控制平面进程 CPU 与内存快照。</summary>
internal sealed class ControlPlaneProcessMetrics(IClock clock)
{
    private readonly Lock _gate = new();
    private DateTimeOffset? _previousAt;
    private TimeSpan _previousCpu;
    private double _lastCpu;

    internal (double CpuPercent, long MemoryBytes) Sample()
    {
        using var process = Process.GetCurrentProcess();
        DateTimeOffset now = clock.UtcNow;
        TimeSpan cpu = process.TotalProcessorTime;
        lock (_gate)
        {
            if (_previousAt is { } previousAt && now > previousAt)
            {
                double elapsedMilliseconds = (now - previousAt).TotalMilliseconds * Environment.ProcessorCount;
                _lastCpu = Math.Clamp((cpu - _previousCpu).TotalMilliseconds * 100 / elapsedMilliseconds, 0, 100);
            }

            _previousAt = now;
            _previousCpu = cpu;
            return (_lastCpu, process.WorkingSet64);
        }
    }
}
