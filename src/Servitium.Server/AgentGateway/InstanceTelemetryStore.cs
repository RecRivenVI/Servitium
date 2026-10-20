using System;
using System.Collections.Concurrent;

using Servitium.Contracts.Events;

namespace Servitium.Server.AgentGateway;

/// <summary>保存 Agent 最近一次上报的实例运行摘要，供低延迟列表查询使用。</summary>
internal sealed class InstanceTelemetryStore
{
    private readonly ConcurrentDictionary<Guid, InstanceTelemetrySnapshot> _items = new();

    internal void Update(InstanceResourceUsagePayload payload, DateTimeOffset observedAt)
    {
        ArgumentNullException.ThrowIfNull(payload);
        _ = _items.AddOrUpdate(
            payload.InstanceId,
            _ => new(payload.CpuPercent, payload.MemoryBytes, payload.OnlinePlayers, observedAt),
            (_, current) => current with
            {
                CpuPercent = payload.CpuPercent,
                MemoryBytes = payload.MemoryBytes,
                OnlinePlayers = payload.OnlinePlayers ?? current.OnlinePlayers,
                ObservedAt = observedAt,
            });
    }

    internal void Update(InstancePlayersChangedPayload payload, DateTimeOffset observedAt)
    {
        ArgumentNullException.ThrowIfNull(payload);
        _ = _items.AddOrUpdate(
            payload.InstanceId,
            _ => new(null, null, payload.OnlinePlayers, observedAt),
            (_, current) => current with { OnlinePlayers = payload.OnlinePlayers, ObservedAt = observedAt });
    }

    internal InstanceTelemetrySnapshot? Find(Guid instanceId, DateTimeOffset now)
    {
        return _items.TryGetValue(instanceId, out InstanceTelemetrySnapshot? value) && value.ObservedAt >= now.AddMinutes(-2)
            ? value
            : null;
    }
}

internal sealed record InstanceTelemetrySnapshot(
    double? CpuPercent,
    long? MemoryBytes,
    int? OnlinePlayers,
    DateTimeOffset ObservedAt);
