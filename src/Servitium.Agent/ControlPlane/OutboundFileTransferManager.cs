using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

using Google.Protobuf;

using Servitium.Agent.Files;
using Servitium.Contracts.Agent;
using Servitium.Contracts.Agent.V1;
using Servitium.Domain.Common;

namespace Servitium.Agent.ControlPlane;

internal sealed class OutboundFileTransferManager(InstanceFileService files, AgentTransferRateLimiters? rateLimiters = null) : IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, TransferState> _active = new(StringComparer.Ordinal);
    private readonly Channel<AgentMessage> _outbound = Channel.CreateBounded<AgentMessage>(
        new BoundedChannelOptions(32)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait,
            AllowSynchronousContinuations = false,
        });

    internal async Task HandleControlAsync(StreamControl control, CancellationToken cancellationToken)
    {
        if (control.Kind != StreamKind.FileDownload || !Guid.TryParseExact(control.StreamId, "N", out _))
        {
            throw new DomainException("validation-failed", "The outbound file stream control is invalid.");
        }

        if (control.Action == StreamControlAction.Open)
        {
            AgentOpenFileDownloadRequest request;
            try
            {
                request = JsonSerializer.Deserialize<AgentOpenFileDownloadRequest>(control.ResourceId)
                    ?? throw new JsonException("The file stream request is empty.");
            }
            catch (JsonException)
            {
                throw new DomainException("validation-failed", "The outbound file stream request is invalid.");
            }

            FileDownloadSource source = await files.OpenDownloadAsync(
                request.InstanceId,
                request.RelativePath,
                request.Offset,
                request.Length,
                cancellationToken).ConfigureAwait(false);
            TransferState state = new(source.Stream, source.Length);
            if (!_active.TryAdd(control.StreamId, state))
            {
                await source.Stream.DisposeAsync().ConfigureAwait(false);
                throw new DomainException("conflict", "The outbound file stream already exists.");
            }

            state.Task = PumpAsync(control.StreamId, state);
            return;
        }

        if (control.Action == StreamControlAction.Close && _active.TryRemove(control.StreamId, out TransferState? existing))
        {
            await existing.CancelAsync().ConfigureAwait(false);
            return;
        }

        throw new DomainException("validation-failed", "The outbound file stream transition is invalid.");
    }

    internal IAsyncEnumerable<AgentMessage> ReadOutboundAsync(CancellationToken cancellationToken)
    {
        return _outbound.Reader.ReadAllAsync(cancellationToken);
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope", Justification = "A registered transfer state owns and disposes the content collection stream; the rejected path disposes it immediately.")]
    internal async Task StartContentCollectAsync(string streamId, string archivePath, CancellationToken cancellationToken)
    {
        if (!Guid.TryParseExact(streamId, "N", out _) || !File.Exists(archivePath))
        {
            throw new DomainException("validation-failed", "The content collection stream is invalid.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        FileStream stream = new(archivePath, FileMode.Open, FileAccess.Read, FileShare.Read, 256 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        TransferState state = new(stream, stream.Length, archivePath);
        if (!_active.TryAdd(streamId, state))
        {
            await stream.DisposeAsync().ConfigureAwait(false);
            throw new DomainException("conflict", "The content collection stream already exists.");
        }

        state.Task = PumpAsync(streamId, state);
    }

    internal async Task ResetAsync()
    {
        foreach ((string id, TransferState state) in _active)
        {
            if (_active.TryRemove(new KeyValuePair<string, TransferState>(id, state)))
            {
                await state.CancelAsync().ConfigureAwait(false);
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

    private async Task PumpAsync(string streamId, TransferState state)
    {
        byte[] buffer = new byte[256 * 1024];
        ulong sequence = 0;
        long remaining = state.Length;
        try
        {
            while (remaining > 0)
            {
                int requested = checked((int)Math.Min(buffer.Length, remaining));
                int read = await state.Stream.ReadAsync(buffer.AsMemory(0, requested), state.Cancellation.Token).ConfigureAwait(false);
                if (read == 0)
                {
                    throw new DomainException("download-failed", "The file changed while it was being downloaded.");
                }

                if (rateLimiters is not null)
                {
                    await rateLimiters.Download.AcquireAsync(read, state.Cancellation.Token).ConfigureAwait(false);
                }

                remaining -= read;
                await _outbound.Writer.WriteAsync(new AgentMessage
                {
                    StreamChunk = new StreamChunk
                    {
                        StreamId = streamId,
                        Sequence = sequence++,
                        Data = ByteString.CopyFrom(buffer, 0, read),
                        EndOfStream = remaining == 0,
                    },
                }, state.Cancellation.Token).ConfigureAwait(false);
            }

            if (state.Length == 0)
            {
                await _outbound.Writer.WriteAsync(new AgentMessage
                {
                    StreamChunk = new StreamChunk { StreamId = streamId, Sequence = 0, EndOfStream = true },
                }, state.Cancellation.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            _ = _active.TryRemove(new KeyValuePair<string, TransferState>(streamId, state));
            await state.Stream.DisposeAsync().ConfigureAwait(false);
            if (state.DeletePath is not null && File.Exists(state.DeletePath))
            {
                File.Delete(state.DeletePath);
            }
            state.Cancellation.Dispose();
        }
    }

    private sealed class TransferState(FileStream stream, long length, string? deletePath = null)
    {
        internal FileStream Stream { get; } = stream;
        internal long Length { get; } = length;
        internal string? DeletePath { get; } = deletePath;
        internal CancellationTokenSource Cancellation { get; } = new();
        internal Task Task { get; set; } = Task.CompletedTask;

        internal async Task CancelAsync()
        {
            await Cancellation.CancelAsync().ConfigureAwait(false);
            await Task.ConfigureAwait(false);
        }
    }
}
