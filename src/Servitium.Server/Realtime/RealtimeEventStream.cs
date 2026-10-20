using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.AspNetCore.SignalR;

using Servitium.Application.Abstractions;
using Servitium.Contracts.Events;
using Servitium.Server.Hubs;

namespace Servitium.Server.Realtime;

internal interface IRealtimeEventStream
{
    Task<RealtimeEventDto> PublishAsync(string type, string scope, object payload, CancellationToken cancellationToken);
    EventResumeDto Resume(long lastEventId, Func<string, bool> scopeAllowed);
    void Register(Guid userId, HubCallerContext context);
    void Unregister(Guid userId, string connectionId);
    Task InvalidateUserAsync(Guid userId, string reason, CancellationToken cancellationToken);
}

internal sealed class RealtimeEventStream(IHubContext<EventsHub> hub, IClock clock) : IRealtimeEventStream
{
    private const int Capacity = 200;
    private readonly Lock _gate = new();
    private readonly Queue<RealtimeEventDto> _events = new(Capacity);
    private readonly ConcurrentDictionary<Guid, ConcurrentDictionary<string, HubCallerContext>> _connections = new();
    private long _sequence;

    public async Task<RealtimeEventDto> PublishAsync(string type, string scope, object payload, CancellationToken cancellationToken)
    {
        if (!EventTypes.All.Contains(type) || string.IsNullOrWhiteSpace(scope))
        {
            throw new ArgumentException("The realtime event type or scope is invalid.", nameof(type));
        }

        RealtimeEventDto envelope = new(
            Interlocked.Increment(ref _sequence),
            type,
            clock.UtcNow,
            scope,
            JsonSerializer.SerializeToElement(payload));
        lock (_gate)
        {
            _events.Enqueue(envelope);
            while (_events.Count > Capacity)
            {
                _ = _events.Dequeue();
            }
        }

        await hub.Clients.Group(scope).SendAsync("event", envelope, cancellationToken).ConfigureAwait(false);
        return envelope;
    }

    public EventResumeDto Resume(long lastEventId, Func<string, bool> scopeAllowed)
    {
        ArgumentNullException.ThrowIfNull(scopeAllowed);
        lock (_gate)
        {
            bool refresh = _events.Count > 0 && lastEventId > 0 && lastEventId < _events.Peek().EventId - 1;
            RealtimeEventDto[] events = [.. _events.Where(item => item.EventId > lastEventId && scopeAllowed(item.Scope)).Take(Capacity)];
            return new(events, refresh);
        }
    }

    public void Register(Guid userId, HubCallerContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        _connections.GetOrAdd(userId, _ => new ConcurrentDictionary<string, HubCallerContext>(StringComparer.Ordinal))[context.ConnectionId] = context;
    }

    public void Unregister(Guid userId, string connectionId)
    {
        if (_connections.TryGetValue(userId, out ConcurrentDictionary<string, HubCallerContext>? connections))
        {
            _ = connections.TryRemove(connectionId, out _);
            if (connections.IsEmpty)
            {
                _ = _connections.TryRemove(new KeyValuePair<Guid, ConcurrentDictionary<string, HubCallerContext>>(userId, connections));
            }
        }
    }

    public async Task InvalidateUserAsync(Guid userId, string reason, CancellationToken cancellationToken)
    {
        _ = await PublishAsync(EventTypes.AuthorizationChanged, "global", new AuthorizationChangedPayload(userId), cancellationToken).ConfigureAwait(false);
        if (!_connections.TryRemove(userId, out ConcurrentDictionary<string, HubCallerContext>? connections))
        {
            return;
        }

        await hub.Clients.Clients(connections.Keys).SendAsync(
            "authorizationChanged",
            new { userId, reason },
            cancellationToken).ConfigureAwait(false);
        foreach (HubCallerContext context in connections.Values)
        {
            context.Abort();
        }
    }
}
