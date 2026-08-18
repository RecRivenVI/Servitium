using System;
using System.Threading;
using System.Threading.Tasks;

using Servitium.Agent.Hosting;
using Servitium.Application.Abstractions;

namespace Servitium.Agent.Files;

/// <summary>按节点配置对所有入站与出站文件流实施共享速率上限。</summary>
internal sealed class AgentTransferRateLimiters(AgentNodeConfigurationStore configuration, IClock clock)
{
    internal SharedTransferRateLimiter Upload { get; } = new SharedTransferRateLimiter(
            () => EffectiveLimit(configuration.Current.UploadBytesPerSecond, configuration.Current.TransferBytesPerSecond),
            () => clock.UtcNow,
            static (delay, token) => Task.Delay(delay, token));
    internal SharedTransferRateLimiter Download { get; } = new SharedTransferRateLimiter(
            () => EffectiveLimit(configuration.Current.DownloadBytesPerSecond, configuration.Current.TransferBytesPerSecond),
            () => clock.UtcNow,
            static (delay, token) => Task.Delay(delay, token));

    private static long EffectiveLimit(long directional, long? legacy)
    {
        return directional > 0 ? directional : legacy.GetValueOrDefault();
    }
}

/// <summary>将并发流的字节预约串成单一时间线，保证聚合速率不超过动态上限。</summary>
internal sealed class SharedTransferRateLimiter(
    Func<long> limitProvider,
    Func<DateTimeOffset> utcNow,
    Func<TimeSpan, CancellationToken, Task> delay) : IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private DateTimeOffset _nextAvailable;

    internal async Task AcquireAsync(int bytes, CancellationToken cancellationToken)
    {
        if (bytes <= 0)
        {
            return;
        }

        TimeSpan wait;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            long limit = limitProvider();
            DateTimeOffset now = utcNow();
            if (limit <= 0)
            {
                _nextAvailable = now;
                return;
            }

            DateTimeOffset start = _nextAvailable > now ? _nextAvailable : now;
            double seconds = bytes / (double)limit;
            DateTimeOffset completion = start.AddSeconds(seconds);
            _nextAvailable = completion;
            wait = completion - now;
        }
        finally
        {
            _ = _gate.Release();
        }

        if (wait > TimeSpan.Zero)
        {
            await delay(wait, cancellationToken).ConfigureAwait(false);
        }
    }

    public void Dispose()
    {
        _gate.Dispose();
    }
}
