using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

using Google.Protobuf;
using Microsoft.AspNetCore.SignalR;

using Servitium.Contracts.Agent.V1;
using Servitium.Contracts.Instances;
using Servitium.Domain.Common;
using Servitium.Server.Hubs;

namespace Servitium.Server.AgentGateway;

internal sealed class TerminalStreamBroker(
    IAgentTerminalGateway gateway,
    IHubContext<TerminalHub> hub) : IAsyncDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<Guid, TerminalSession> _sessions = [];
    private readonly ConcurrentDictionary<string, TerminalSubscription> _subscriptions = new(StringComparer.Ordinal);

    internal async Task<TerminalAttachDto> AttachAsync(
        Guid userId,
        string connectionId,
        Guid nodeId,
        Guid instanceId,
        int columns,
        int rows,
        CancellationToken cancellationToken)
    {
        ValidateSize(columns, rows);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_subscriptions.TryGetValue(connectionId, out TerminalSubscription? current))
            {
                if (current.Session.InstanceId == instanceId)
                {
                    return current.Session.ToAttach();
                }

                await DetachCoreAsync(current, cancellationToken).ConfigureAwait(false);
            }

            if (!_sessions.TryGetValue(instanceId, out TerminalSession? session))
            {
                session = new(nodeId, instanceId);
                _sessions.Add(instanceId, session);
                try
                {
                    await gateway.OpenTerminalAsync(nodeId, session.StreamId, instanceId, ReceiveAsync, cancellationToken).ConfigureAwait(false);
                }
                catch
                {
                    _ = _sessions.Remove(instanceId);
                    throw;
                }
            }
            else if (session.NodeId != nodeId)
            {
                throw new DomainException("conflict", "The terminal instance belongs to another node.");
            }

            session.CancelDelayedClose();
            TerminalSubscription subscription = new(userId, connectionId, columns, rows, session, hub);
            session.Subscriptions.Add(connectionId, subscription);
            _subscriptions[connectionId] = subscription;
            subscription.Start();
            await SendNegotiatedSizeAsync(session, cancellationToken).ConfigureAwait(false);
            await NotifyObserversAsync(session, cancellationToken).ConfigureAwait(false);
            return session.ToAttach();
        }
        finally
        {
            _ = _gate.Release();
        }
    }

    internal async Task SendInputAsync(string connectionId, string text, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(text) || text.Length > 64 * 1024 || text.Contains('\0', StringComparison.Ordinal) ||
            !_subscriptions.TryGetValue(connectionId, out TerminalSubscription? subscription))
        {
            throw new DomainException("validation-failed", "The terminal input or subscription is invalid.");
        }

        await SendFrameAsync(subscription.Session, new("input", Text: text), cancellationToken).ConfigureAwait(false);
    }

    internal async Task ResizeAsync(string connectionId, int columns, int rows, CancellationToken cancellationToken)
    {
        ValidateSize(columns, rows);
        if (!_subscriptions.TryGetValue(connectionId, out TerminalSubscription? subscription))
        {
            throw new DomainException("not-found", "The terminal subscription does not exist.");
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            subscription.Columns = columns;
            subscription.Rows = rows;
            await SendNegotiatedSizeAsync(subscription.Session, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _ = _gate.Release();
        }
    }

    internal async Task DetachAsync(string connectionId, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_subscriptions.TryGetValue(connectionId, out TerminalSubscription? subscription))
            {
                await DetachCoreAsync(subscription, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            _ = _gate.Release();
        }
    }

    internal async Task InvalidateUserAsync(Guid userId, CancellationToken cancellationToken)
    {
        string[] connectionIds = [.. _subscriptions.Values.Where(item => item.UserId == userId).Select(item => item.ConnectionId)];
        foreach (string connectionId in connectionIds)
        {
            await DetachAsync(connectionId, cancellationToken).ConfigureAwait(false);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            foreach (TerminalSubscription subscription in _subscriptions.Values)
            {
                await subscription.DisposeAsync().ConfigureAwait(false);
            }

            foreach (TerminalSession session in _sessions.Values)
            {
                session.CancelDelayedClose();
                await gateway.CloseTerminalAsync(session.NodeId, session.StreamId, session.InstanceId, CancellationToken.None).ConfigureAwait(false);
                session.Dispose();
            }

            _subscriptions.Clear();
            _sessions.Clear();
        }
        finally
        {
            _ = _gate.Release();
            _gate.Dispose();
        }
    }

    private async Task ReceiveAsync(Guid nodeId, StreamChunk chunk, CancellationToken cancellationToken)
    {
        TerminalSession? session;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            session = _sessions.Values.SingleOrDefault(item => item.StreamId == chunk.StreamId && item.NodeId == nodeId);
            if (session is null || !session.AcceptOutputSequence(chunk.Sequence) || chunk.Data.Length > 128 * 1024)
            {
                throw new DomainException("validation-failed", "The Agent terminal output frame is invalid.");
            }

            TerminalOutputFrame frame;
            try
            {
                frame = JsonSerializer.Deserialize<TerminalOutputFrame>(chunk.Data.Span)
                    ?? throw new JsonException("The output frame is empty.");
            }
            catch (JsonException)
            {
                throw new DomainException("validation-failed", "The Agent terminal output frame is invalid.");
            }

            if (frame.InstanceId != session.InstanceId || frame.Text.Length > 64 * 1024)
            {
                throw new DomainException("validation-failed", "The Agent terminal output scope is invalid.");
            }

            foreach (TerminalSubscription subscription in session.Subscriptions.Values)
            {
                subscription.Enqueue(frame);
            }
        }
        finally
        {
            _ = _gate.Release();
        }
    }

    private async Task DetachCoreAsync(TerminalSubscription subscription, CancellationToken cancellationToken)
    {
        _ = _subscriptions.TryRemove(new KeyValuePair<string, TerminalSubscription>(subscription.ConnectionId, subscription));
        _ = subscription.Session.Subscriptions.Remove(subscription.ConnectionId);
        await subscription.DisposeAsync().ConfigureAwait(false);
        if (subscription.Session.Subscriptions.Count == 0)
        {
            subscription.Session.ScheduleClose(() => CloseIdleSessionAsync(subscription.Session));
        }
        else
        {
            await SendNegotiatedSizeAsync(subscription.Session, cancellationToken).ConfigureAwait(false);
            await NotifyObserversAsync(subscription.Session, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task CloseIdleSessionAsync(TerminalSession session)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (session.Subscriptions.Count != 0 || !_sessions.Remove(session.InstanceId))
            {
                return;
            }

            await gateway.CloseTerminalAsync(session.NodeId, session.StreamId, session.InstanceId, CancellationToken.None).ConfigureAwait(false);
            session.Dispose();
        }
        finally
        {
            _ = _gate.Release();
        }
    }

    private Task SendNegotiatedSizeAsync(TerminalSession session, CancellationToken cancellationToken)
    {
        int columns = session.Subscriptions.Count == 0 ? session.Columns : session.Subscriptions.Values.Min(item => item.Columns);
        int rows = session.Subscriptions.Count == 0 ? session.Rows : session.Subscriptions.Values.Min(item => item.Rows);
        session.Columns = columns;
        session.Rows = rows;
        return SendFrameAsync(session, new("resize", Columns: columns, Rows: rows), cancellationToken);
    }

    private Task SendFrameAsync(TerminalSession session, TerminalInputFrame frame, CancellationToken cancellationToken)
    {
        return gateway.SendTerminalChunkAsync(session.NodeId, new StreamChunk
        {
            StreamId = session.StreamId,
            Sequence = session.NextInputSequence(),
            Data = ByteString.CopyFrom(JsonSerializer.SerializeToUtf8Bytes(frame)),
        }, cancellationToken);
    }

    private Task NotifyObserversAsync(TerminalSession session, CancellationToken cancellationToken)
    {
        return hub.Clients.Clients(session.Subscriptions.Keys).SendAsync(
            "observerCount",
            new { session.InstanceId, observers = session.Subscriptions.Count },
            cancellationToken);
    }

    private static void ValidateSize(int columns, int rows)
    {
        if (columns is < 20 or > 500 || rows is < 5 or > 200)
        {
            throw new DomainException("validation-failed", "The terminal size is invalid.");
        }
    }

    private sealed class TerminalSession(Guid nodeId, Guid instanceId) : IDisposable
    {
        private ulong _inputSequence;
        private ulong _outputSequence;
        private CancellationTokenSource? _delayedClose;

        internal Guid NodeId { get; } = nodeId;
        internal Guid InstanceId { get; } = instanceId;
        internal string StreamId { get; } = Guid.NewGuid().ToString("N");
        internal Dictionary<string, TerminalSubscription> Subscriptions { get; } = new(StringComparer.Ordinal);
        internal int Columns { get; set; } = 164;
        internal int Rows { get; set; } = 40;

        internal ulong NextInputSequence()
        {
            return _inputSequence++;
        }

        internal bool AcceptOutputSequence(ulong sequence)
        {
            if (sequence != _outputSequence)
            {
                return false;
            }

            _outputSequence++;
            return true;
        }

        internal TerminalAttachDto ToAttach()
        {
            return new(InstanceId, Subscriptions.Count, Columns, Rows);
        }

        internal void ScheduleClose(Func<Task> close)
        {
            CancelDelayedClose();
            _delayedClose = new CancellationTokenSource();
            _ = CloseAfterDelayAsync(close, _delayedClose.Token);
        }

        internal void CancelDelayedClose()
        {
            if (_delayedClose is null)
            {
                return;
            }

            _delayedClose.Cancel();
            _delayedClose.Dispose();
            _delayedClose = null;
        }

        public void Dispose()
        {
            CancelDelayedClose();
        }

        private static async Task CloseAfterDelayAsync(Func<Task> close, CancellationToken cancellationToken)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(60), cancellationToken).ConfigureAwait(false);
                await close().ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }
    }

    private sealed class TerminalSubscription(
        Guid userId,
        string connectionId,
        int columns,
        int rows,
        TerminalSession session,
        IHubContext<TerminalHub> hub) : IAsyncDisposable
    {
        private readonly Channel<TerminalOutputFrame> _queue = Channel.CreateBounded<TerminalOutputFrame>(
            new BoundedChannelOptions(512)
            {
                SingleReader = true,
                SingleWriter = false,
                FullMode = BoundedChannelFullMode.Wait,
                AllowSynchronousContinuations = false,
            });
        private readonly CancellationTokenSource _closed = new();
        private Task _pump = Task.CompletedTask;

        internal Guid UserId { get; } = userId;
        internal string ConnectionId { get; } = connectionId;
        internal TerminalSession Session { get; } = session;
        internal int Columns { get; set; } = columns;
        internal int Rows { get; set; } = rows;

        internal void Start()
        {
            _pump = PumpAsync(_closed.Token);
        }

        internal void Enqueue(TerminalOutputFrame frame)
        {
            if (_queue.Writer.TryWrite(frame))
            {
                return;
            }

            _ = _queue.Reader.TryRead(out _);
            _ = _queue.Writer.TryWrite(frame with { Truncated = true });
        }

        public async ValueTask DisposeAsync()
        {
            _ = _queue.Writer.TryComplete();
            await _closed.CancelAsync().ConfigureAwait(false);
            try
            {
                await _pump.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }

            _closed.Dispose();
        }

        private async Task PumpAsync(CancellationToken cancellationToken)
        {
            await foreach (TerminalOutputFrame frame in _queue.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                await hub.Clients.Client(ConnectionId).SendAsync("output", frame, cancellationToken).ConfigureAwait(false);
            }
        }
    }
}
