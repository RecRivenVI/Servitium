using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using Servitium.Agent.Files;

using Xunit;

namespace Servitium.Agent.IntegrationTests;

public sealed class TransferRateLimiterTests
{
    [Fact]
    [Trait("Category", "Integration")]
    [Trait("TestId", "INT-850")]
    public async Task 共享传输限速器_按聚合字节预约并立即响应动态配置()
    {
        long limit = 1_000;
        DateTimeOffset now = new(2026, 8, 3, 0, 0, 0, TimeSpan.Zero);
        List<TimeSpan> waits = [];
        using SharedTransferRateLimiter limiter = new(
            () => limit,
            () => now,
            (wait, _) =>
            {
                waits.Add(wait);
                now += wait;
                return Task.CompletedTask;
            });

        await limiter.AcquireAsync(250, CancellationToken.None).ConfigureAwait(true);
        await limiter.AcquireAsync(750, CancellationToken.None).ConfigureAwait(true);
        limit = 0;
        await limiter.AcquireAsync(10_000, CancellationToken.None).ConfigureAwait(true);

        Assert.Equal(TimeSpan.FromMilliseconds(250), waits[0]);
        Assert.Equal(TimeSpan.FromMilliseconds(750), waits[1]);
        Assert.Equal(2, waits.Count);
    }
}
