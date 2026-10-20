using System;
using System.Collections.Concurrent;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using Servitium.Agent.Files;
using Servitium.Agent.Storage;
using Servitium.Contracts.Agent;
using Servitium.Contracts.Agent.V1;
using Servitium.Domain.Primitives;

namespace Servitium.Agent.ControlPlane;

internal sealed class InboundFileTransferStore(AgentStorageOptions options, AgentTransferRateLimiters? rateLimiters = null) : IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, TransferState> _active = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> _completed = new(StringComparer.Ordinal);

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope", Justification = "A successfully registered transfer state owns the stream until close or store disposal; the failure path disposes it immediately.")]
    internal async Task HandleControlAsync(StreamControl control, CancellationToken cancellationToken)
    {
        if (control.Kind is not (StreamKind.FileUpload or StreamKind.ContentSync) || !Guid.TryParseExact(control.StreamId, "N", out _))
        {
            throw new DomainException("validation-failed", "The inbound stream control is invalid.");
        }

        if (control.Action == StreamControlAction.Open)
        {
            AgentInboundTransferDescriptor descriptor;
            try
            {
                descriptor = JsonSerializer.Deserialize<AgentInboundTransferDescriptor>(control.ResourceId)
                    ?? throw new JsonException("The inbound stream descriptor is empty.");
            }
            catch (JsonException)
            {
                throw new DomainException("validation-failed", "The inbound stream descriptor is invalid.");
            }

            if (descriptor.Length < 0 || string.IsNullOrWhiteSpace(descriptor.ResourceName) || descriptor.ResourceName.Length > 512)
            {
                throw new DomainException("validation-failed", "The inbound stream descriptor is invalid.");
            }

            _ = Directory.CreateDirectory(options.StagingRoot);
            EnsureDiskSpace(options.StagingRoot, descriptor.Length);
            string path = Path.Combine(options.StagingRoot, control.StreamId + ".upload");
            FileStream stream = new(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 256 * 1024, FileOptions.Asynchronous | FileOptions.WriteThrough);
            if (!_active.TryAdd(control.StreamId, new TransferState(path, stream, descriptor.Length)))
            {
                await stream.DisposeAsync().ConfigureAwait(false);
                File.Delete(path);
                throw new DomainException("conflict", "The inbound stream already exists.");
            }

            return;
        }

        if (control.Action == StreamControlAction.Close && _active.TryRemove(control.StreamId, out TransferState? state))
        {
            if (state.WrittenBytes != state.ExpectedLength)
            {
                await state.Stream.DisposeAsync().ConfigureAwait(false);
                DeleteIfPresent(state.Path);
                throw new DomainException("validation-failed", "The inbound stream length does not match its declaration.");
            }

            await state.Stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            await state.Stream.DisposeAsync().ConfigureAwait(false);
            string completedPath = Path.ChangeExtension(state.Path, ".zip");
            File.Move(state.Path, completedPath);
            _completed[control.StreamId] = completedPath;
            return;
        }

        if (control.Action == StreamControlAction.Abort)
        {
            if (_active.TryRemove(control.StreamId, out TransferState? active))
            {
                await active.Stream.DisposeAsync().ConfigureAwait(false);
                DeleteIfPresent(active.Path);
            }

            if (_completed.TryRemove(control.StreamId, out string? completedPath))
            {
                DeleteIfPresent(completedPath);
            }

            return;
        }

        throw new DomainException("validation-failed", "The inbound stream transition is invalid.");
    }

    internal async Task HandleChunkAsync(StreamChunk chunk, CancellationToken cancellationToken)
    {
        if (!_active.TryGetValue(chunk.StreamId, out TransferState? state) || chunk.Sequence != state.NextSequence)
        {
            throw new DomainException("validation-failed", "The inbound stream sequence is invalid.");
        }

        if (chunk.Data.Length > 256 * 1024)
        {
            throw new DomainException("file-too-large", "An inbound stream chunk exceeds the protocol limit.");
        }

        if (state.WrittenBytes > state.ExpectedLength - chunk.Data.Length)
        {
            throw new DomainException("file-too-large", "The inbound stream exceeds its declared length.");
        }

        if (rateLimiters is not null)
        {
            await rateLimiters.Upload.AcquireAsync(chunk.Data.Length, cancellationToken).ConfigureAwait(false);
        }

        await state.Stream.WriteAsync(chunk.Data.Memory, cancellationToken).ConfigureAwait(false);
        state.WrittenBytes += chunk.Data.Length;
        state.NextSequence++;
    }

    internal string Consume(string streamId)
    {
        return !_completed.TryRemove(streamId, out string? path)
            ? throw new DomainException("upload-session-expired", "The uploaded stream is not available.")
            : path;
    }

    public async ValueTask DisposeAsync()
    {
        foreach (TransferState transfer in _active.Values)
        {
            await transfer.Stream.DisposeAsync().ConfigureAwait(false);
            if (File.Exists(transfer.Path))
            {
                File.Delete(transfer.Path);
            }
        }

        _active.Clear();
        foreach (string path in _completed.Values)
        {
            DeleteIfPresent(path);
        }

        _completed.Clear();
    }

    private static void DeleteIfPresent(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    private static void EnsureDiskSpace(string path, long requiredBytes)
    {
        string? root = Path.GetPathRoot(path);
        if (root is null || new DriveInfo(root).AvailableFreeSpace < requiredBytes + (64L * 1024 * 1024))
        {
            throw new DomainException("disk-space-insufficient", "The staging volume does not have sufficient free space.");
        }
    }

    private sealed class TransferState(string path, FileStream stream, long expectedLength)
    {
        internal string Path { get; } = path;
        internal FileStream Stream { get; } = stream;
        internal long ExpectedLength { get; } = expectedLength;
        internal long WrittenBytes { get; set; }
        internal ulong NextSequence { get; set; }
    }
}
