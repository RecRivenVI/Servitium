using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

using Google.Protobuf;

using Servitium.Agent.Lifecycle;
using Servitium.Contracts.Agent.V1;
using Servitium.Contracts.Instances;
using Servitium.Domain.Primitives;

namespace Servitium.Agent.Terminals;

internal sealed class AgentTerminalStreamManager(InstanceLifecycleService lifecycle) : IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, TerminalStreamState> _streams = new(StringComparer.Ordinal);
    private readonly Channel<AgentMessage> _outbound = Channel.CreateUnbounded<AgentMessage>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false, AllowSynchronousContinuations = false });

    internal async Task HandleControlAsync(StreamControl control, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (control.Kind != StreamKind.Terminal || !Guid.TryParseExact(control.StreamId, "N", out _) ||
            !Guid.TryParse(control.ResourceId, out Guid instanceId))
        {
            throw new DomainException("validation-failed", "The terminal stream control is invalid.");
        }

        if (control.Action == StreamControlAction.Open)
        {
            TerminalStreamState state = new(control.StreamId, instanceId, _outbound.Writer);
            if (!_streams.TryAdd(control.StreamId, state))
            {
                throw new DomainException("conflict", "The terminal stream already exists.");
            }

            try
            {
                InstanceLifecycleService.TerminalAttachment attachment = lifecycle.AttachTerminal(instanceId, state.EnqueueLive);
                state.Initialize(attachment);
                state.Start();
                return;
            }
            catch
            {
                _ = _streams.TryRemove(control.StreamId, out _);
                await state.DisposeAsync().ConfigureAwait(false);
                throw;
            }
        }

        if (control.Action == StreamControlAction.Close && _streams.TryRemove(control.StreamId, out TerminalStreamState? existing))
        {
            await existing.DisposeAsync().ConfigureAwait(false);
            return;
        }

        throw new DomainException("validation-failed", "The terminal stream transition is invalid.");
    }

    internal async Task HandleChunkAsync(StreamChunk chunk, CancellationToken cancellationToken)
    {
        if (!_streams.TryGetValue(chunk.StreamId, out TerminalStreamState? state) || !state.AcceptSequence(chunk.Sequence) || chunk.Data.Length > 64 * 1024)
        {
            throw new DomainException("validation-failed", "The terminal stream chunk is invalid.");
        }

        TerminalInputFrame frame;
        try
        {
            frame = JsonSerializer.Deserialize<TerminalInputFrame>(chunk.Data.Span)
                ?? throw new JsonException("The terminal frame is empty.");
        }
        catch (JsonException)
        {
            throw new DomainException("validation-failed", "The terminal frame is invalid.");
        }

        if (string.Equals(frame.Type, "input", StringComparison.Ordinal))
        {
            await lifecycle.SendTerminalInputAsync(state.InstanceId, frame.Text ?? string.Empty, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (string.Equals(frame.Type, "resize", StringComparison.Ordinal) && frame.Columns is >= 20 and <= 500 && frame.Rows is >= 5 and <= 200)
        {
            lifecycle.ResizeTerminal(state.InstanceId, frame.Columns.Value, frame.Rows.Value);
            return;
        }

        throw new DomainException("validation-failed", "The terminal frame type is invalid.");
    }

    internal IAsyncEnumerable<AgentMessage> ReadOutboundAsync(CancellationToken cancellationToken)
    {
        return _outbound.Reader.ReadAllAsync(cancellationToken);
    }

    internal bool Owns(string streamId)
    {
        return _streams.ContainsKey(streamId);
    }

    internal async Task ResetAsync()
    {
        foreach ((string streamId, TerminalStreamState state) in _streams)
        {
            if (_streams.TryRemove(new KeyValuePair<string, TerminalStreamState>(streamId, state)))
            {
                await state.DisposeAsync().ConfigureAwait(false);
            }
        }

        while (_outbound.Reader.TryRead(out _))
        {
        }
    }

    public async ValueTask DisposeAsync()
    {
        await ResetAsync().ConfigureAwait(false);
        _ = _outbound.Writer.TryComplete();
    }

    private sealed class TerminalStreamState(string streamId, Guid instanceId, ChannelWriter<AgentMessage> writer) : IAsyncDisposable
    {
        private const int MaximumQueuedCharacters = 4 * 1024 * 1024;
        private readonly Lock _gate = new();
        private readonly Queue<string> _snapshot = new();
        private readonly Queue<string> _pending = new();
        private readonly Queue<string> _output = new();
        private readonly SemaphoreSlim _signal = new(0, 1);
        private readonly CancellationTokenSource _closed = new();
        private InstanceLifecycleService.TerminalAttachment? _attachment;
        private Task _flushTask = Task.CompletedTask;
        private int _queuedCharacters;
        private bool _initialized;
        private bool _truncated;
        private ulong _inputSequence;
        private ulong _outputSequence;

        internal Guid InstanceId { get; } = instanceId;

        internal bool AcceptSequence(ulong sequence)
        {
            lock (_gate)
            {
                if (sequence != _inputSequence)
                {
                    return false;
                }

                _inputSequence++;
                return true;
            }
        }

        internal void EnqueueLive(string text)
        {
            lock (_gate)
            {
                EnqueueCore(_initialized ? _output : _pending, text);
            }

            Signal();
        }

        internal void Initialize(InstanceLifecycleService.TerminalAttachment attachment)
        {
            _attachment = attachment;
            lock (_gate)
            {
                foreach (string line in attachment.Snapshot)
                {
                    EnqueueCore(_snapshot, line + "\r\n");
                }

                while (_pending.TryDequeue(out string? pending))
                {
                    EnqueueCore(_output, pending);
                }

                _initialized = true;
            }
        }

        internal void Start()
        {
            _flushTask = FlushLoopAsync(_closed.Token);
            Signal();
        }

        public async ValueTask DisposeAsync()
        {
            _attachment?.Dispose();
            await _closed.CancelAsync().ConfigureAwait(false);
            Signal();
            try
            {
                await _flushTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }

            _closed.Dispose();
            _signal.Dispose();
        }

        private void EnqueueCore(Queue<string> target, string text)
        {
            target.Enqueue(text);
            _queuedCharacters = checked(_queuedCharacters + text.Length);
            while (_queuedCharacters > MaximumQueuedCharacters && TryDropOldest())
            {
                _truncated = true;
            }
        }

        private bool TryDropOldest()
        {
            Queue<string>[] queues = [_snapshot, _pending, _output];
            foreach (Queue<string> queue in queues)
            {
                if (queue.TryDequeue(out string? removed))
                {
                    _queuedCharacters -= removed.Length;
                    return true;
                }
            }

            return false;
        }

        private async Task FlushLoopAsync(CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                using var delay = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
                using var combined = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, delay.Token);
                try
                {
                    await _signal.WaitAsync(combined.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                }

                while (TryBuildFrame(out TerminalOutputFrame? frame))
                {
                    _ = writer.TryWrite(new AgentMessage
                    {
                        StreamChunk = new StreamChunk
                        {
                            StreamId = streamId,
                            Sequence = _outputSequence++,
                            Data = ByteString.CopyFrom(JsonSerializer.SerializeToUtf8Bytes(frame)),
                        },
                    });
                }
            }
        }

        private bool TryBuildFrame(out TerminalOutputFrame? frame)
        {
            lock (_gate)
            {
                Queue<string> source = _snapshot.Count > 0 ? _snapshot : _output;
                if (source.Count == 0 && !_truncated)
                {
                    frame = null;
                    return false;
                }

                bool snapshot = _snapshot.Count > 0;
                bool truncated = _truncated;
                _truncated = false;
                StringBuilder text = new(8192);
                while (source.TryPeek(out string? next) && (text.Length == 0 || text.Length + next.Length <= 8192))
                {
                    _ = source.Dequeue();
                    _queuedCharacters -= next.Length;
                    _ = text.Append(next);
                }

                frame = new TerminalOutputFrame(InstanceId, checked((long)_outputSequence), text.ToString(), snapshot, truncated);
                return true;
            }
        }

        private void Signal()
        {
            try
            {
                _ = _signal.Release();
            }
            catch (SemaphoreFullException)
            {
            }
            catch (ObjectDisposedException)
            {
            }
        }
    }
}
