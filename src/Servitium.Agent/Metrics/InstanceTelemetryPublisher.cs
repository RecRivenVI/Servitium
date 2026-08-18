using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using Servitium.Agent.ControlPlane;
using Servitium.Agent.Instances;
using Servitium.Agent.Lifecycle;
using Servitium.Contracts.Events;
using Servitium.Contracts.Instances;
using Servitium.Domain.Common;

namespace Servitium.Agent.Metrics;

/// <summary>周期采集运行中实例的进程树指标与玩家状态。</summary>
internal sealed partial class InstanceTelemetryPublisher(
    InstanceRegistry instances,
    InstanceLifecycleService lifecycle,
    AgentOutboxStore outbox,
    ILogger<InstanceTelemetryPublisher> logger) : BackgroundService
{
    private readonly ConcurrentDictionary<Guid, (int Online, int Maximum)> _lastPlayers = new();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using PeriodicTimer timer = new(TimeSpan.FromSeconds(15));
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            IReadOnlyList<LocalInstanceSnapshot> registrations = await instances.ListAsync(stoppingToken).ConfigureAwait(false);
            foreach (LocalInstanceSnapshot instance in registrations.Where(item => item.Status == "Running"))
            {
                try
                {
                    InstanceMetricsDto metrics = await lifecycle.GetMetricsAsync(instance.Id, stoppingToken).ConfigureAwait(false);
                    await outbox.EnqueueAsync(
                        EventTypes.InstanceResourceUsage,
                        "instance:" + instance.Id.ToString("D"),
                        new InstanceResourceUsagePayload(instance.Id, metrics.CpuPercent, metrics.MemoryBytes, metrics.OnlinePlayers),
                        critical: false,
                        stoppingToken).ConfigureAwait(false);

                    if (instance.RuntimeSettings.Query.Enabled)
                    {
                        PlayerListDto players = await lifecycle.QueryPlayersAsync(instance.Id, stoppingToken).ConfigureAwait(false);
                        (int Online, int Maximum) current = (players.Online, players.Maximum);
                        if (!_lastPlayers.TryGetValue(instance.Id, out (int Online, int Maximum) previous) || previous != current)
                        {
                            _lastPlayers[instance.Id] = current;
                            await outbox.EnqueueAsync(
                                EventTypes.InstancePlayersChanged,
                                "instance:" + instance.Id.ToString("D"),
                                new InstancePlayersChangedPayload(instance.Id, players.Online, players.Maximum),
                                critical: false,
                                stoppingToken).ConfigureAwait(false);
                        }
                    }
                }
                catch (DomainException exception)
                {
                    SampleFailed(logger, instance.Id, exception.Code);
                }
            }
        }
    }

    [LoggerMessage(3201, LogLevel.Debug, "Instance telemetry sample failed for {InstanceId} with {ErrorCode}")]
    private static partial void SampleFailed(ILogger logger, Guid instanceId, string errorCode);
}
