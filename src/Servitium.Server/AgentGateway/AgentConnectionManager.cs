using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

using Google.Protobuf;
using Grpc.Core;

using Microsoft.Extensions.Logging;

using Servitium.Application.Abstractions;
using Servitium.Contracts.Agent.V1;

namespace Servitium.Server.AgentGateway;

internal interface IAgentFileTransferGateway
{
    Task<AgentCommandResult> SendFileCommandAsync(
        Guid nodeId,
        string filePath,
        AgentCommand command,
        CancellationToken cancellationToken);

    Task StreamFileAsync(
        Guid nodeId,
        Contracts.Agent.AgentOpenFileDownloadRequest request,
        Stream destination,
        CancellationToken cancellationToken);
}

internal interface IAgentTerminalGateway
{
    Task OpenTerminalAsync(Guid nodeId, string streamId, Guid instanceId, Func<Guid, StreamChunk, CancellationToken, Task> receiver, CancellationToken cancellationToken);
    Task SendTerminalChunkAsync(Guid nodeId, StreamChunk chunk, CancellationToken cancellationToken);
    Task CloseTerminalAsync(Guid nodeId, string streamId, Guid instanceId, CancellationToken cancellationToken);
    Task ReceiveTerminalChunkAsync(Guid nodeId, StreamChunk chunk, CancellationToken cancellationToken);
}

internal interface IAgentDistributionGateway
{
    Task<Contracts.Agent.AgentCollectedDirectoryResult> CollectDirectoryAsync(Guid nodeId, Guid instanceId, Stream destination, CancellationToken cancellationToken);
    Task<AgentCommandResult> SyncReleaseAsync(Guid nodeId, Contracts.Agent.AgentSyncFromReleaseCommand command, Stream archive, CancellationToken cancellationToken);
}

internal sealed partial class AgentConnectionManager(
    ILogger<AgentConnectionManager> logger) : IAgentGateway, IAgentFileTransferGateway, IAgentTerminalGateway, IAgentDistributionGateway
{
    private readonly ConcurrentDictionary<Guid, AgentConnection> _connections = new();
    private readonly ConcurrentDictionary<string, TerminalReceiver> _terminalReceivers = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, FileDownloadReceiver> _downloadReceivers = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, FileDownloadReceiver> _contentReceivers = new(StringComparer.Ordinal);

    internal AgentConnection Register(Guid nodeId, IServerStreamWriter<ServerMessage> responseStream, CancellationToken cancellationToken)
    {
        AgentConnection connection = new(responseStream, cancellationToken);
        if (_connections.TryGetValue(nodeId, out AgentConnection? previous))
        {
            previous.Dispose();
        }

        _connections[nodeId] = connection;
        return connection;
    }

    internal void Remove(Guid nodeId, AgentConnection connection)
    {
        _ = _connections.TryRemove(new KeyValuePair<Guid, AgentConnection>(nodeId, connection));
        RemoveTerminalReceivers(nodeId);
        RemoveDownloadReceivers(nodeId);
        RemoveContentReceivers(nodeId);
        connection.Dispose();
    }

    internal bool Disconnect(Guid nodeId)
    {
        if (!_connections.TryRemove(nodeId, out AgentConnection? connection))
        {
            return false;
        }

        connection.Dispose();
        RemoveTerminalReceivers(nodeId);
        RemoveDownloadReceivers(nodeId);
        RemoveContentReceivers(nodeId);
        return true;
    }

    internal void Complete(CommandResponse response, Guid nodeId)
    {
        if (_connections.TryGetValue(nodeId, out AgentConnection? connection))
        {
            connection.Complete(response);
        }
    }

    public async Task<AgentCommandResult> SendAsync(Guid nodeId, AgentCommand command, CancellationToken cancellationToken)
    {
        return !_connections.TryGetValue(nodeId, out AgentConnection? connection)
            ? new(false, ReadOnlyMemory<byte>.Empty, "node-offline")
            : await connection.SendCommandAsync(command, cancellationToken).ConfigureAwait(false);
    }

    public async Task<AgentCommandResult> SendFileCommandAsync(
        Guid nodeId,
        string filePath,
        AgentCommand command,
        CancellationToken cancellationToken)
    {
        if (!_connections.TryGetValue(nodeId, out AgentConnection? connection))
        {
            return new(false, ReadOnlyMemory<byte>.Empty, "node-offline");
        }

        string streamId = Guid.NewGuid().ToString("N");
        await connection.SendAsync(new ServerMessage
        {
            StreamControl = new StreamControl
            {
                StreamId = streamId,
                Kind = StreamKind.FileUpload,
                Action = StreamControlAction.Open,
                ResourceId = JsonSerializer.Serialize(new Contracts.Agent.AgentInboundTransferDescriptor(Path.GetFileName(filePath), new FileInfo(filePath).Length)),
            },
        }, cancellationToken).ConfigureAwait(false);

        using FileStream source = new(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 256 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        byte[] buffer = new byte[256 * 1024];
        ulong sequence = 0;
        while (true)
        {
            int read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            await connection.SendAsync(new ServerMessage
            {
                StreamChunk = new StreamChunk
                {
                    StreamId = streamId,
                    Sequence = sequence++,
                    Data = ByteString.CopyFrom(buffer, 0, read),
                },
            }, cancellationToken).ConfigureAwait(false);
        }

        await connection.SendAsync(new ServerMessage
        {
            StreamControl = new StreamControl
            {
                StreamId = streamId,
                Kind = StreamKind.FileUpload,
                Action = StreamControlAction.Close,
                ResourceId = JsonSerializer.Serialize(new Contracts.Agent.AgentInboundTransferDescriptor(Path.GetFileName(filePath), new FileInfo(filePath).Length)),
            },
        }, cancellationToken).ConfigureAwait(false);

        byte[] payload = command.Payload.ToArray();
        string marker = "\"UploadStreamId\":null";
        string json = System.Text.Encoding.UTF8.GetString(payload).Replace(marker, $"\"UploadStreamId\":\"{streamId}\"", StringComparison.Ordinal);
        return await connection.SendCommandAsync(command with { Payload = System.Text.Encoding.UTF8.GetBytes(json) }, cancellationToken).ConfigureAwait(false);
    }

    public async Task StreamFileAsync(
        Guid nodeId,
        Contracts.Agent.AgentOpenFileDownloadRequest request,
        Stream destination,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(destination);
        if (!_connections.TryGetValue(nodeId, out AgentConnection? connection))
        {
            throw new IOException("node-offline");
        }

        string streamId = Guid.NewGuid().ToString("N");
        FileDownloadReceiver receiver = new(nodeId);
        if (!_downloadReceivers.TryAdd(streamId, receiver))
        {
            throw new InvalidOperationException("conflict");
        }

        bool completed = false;
        try
        {
            await connection.SendAsync(new ServerMessage
            {
                StreamControl = new StreamControl
                {
                    StreamId = streamId,
                    Kind = StreamKind.FileDownload,
                    Action = StreamControlAction.Open,
                    ResourceId = JsonSerializer.Serialize(request),
                },
            }, cancellationToken).ConfigureAwait(false);

            await foreach (byte[] chunk in receiver.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                await destination.WriteAsync(chunk, cancellationToken).ConfigureAwait(false);
            }

            await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
            completed = receiver.Completed;
            if (!completed)
            {
                throw new IOException("node-offline");
            }
        }
        finally
        {
            _ = _downloadReceivers.TryRemove(new KeyValuePair<string, FileDownloadReceiver>(streamId, receiver));
            if (!completed && _connections.TryGetValue(nodeId, out AgentConnection? active) && ReferenceEquals(active, connection))
            {
                await active.SendAsync(new ServerMessage
                {
                    StreamControl = new StreamControl
                    {
                        StreamId = streamId,
                        Kind = StreamKind.FileDownload,
                        Action = StreamControlAction.Close,
                        ResourceId = request.InstanceId.ToString("D"),
                    },
                }, CancellationToken.None).ConfigureAwait(false);
            }
        }
    }

    public async Task<Contracts.Agent.AgentCollectedDirectoryResult> CollectDirectoryAsync(
        Guid nodeId,
        Guid instanceId,
        Stream destination,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(destination);
        if (!_connections.TryGetValue(nodeId, out AgentConnection? connection))
        {
            throw new IOException("node-offline");
        }

        string streamId = Guid.NewGuid().ToString("N");
        FileDownloadReceiver receiver = new(nodeId);
        if (!_contentReceivers.TryAdd(streamId, receiver))
        {
            throw new InvalidOperationException("conflict");
        }

        Task drain = DrainAsync(receiver, destination, cancellationToken);
        try
        {
            byte[] payload = JsonSerializer.SerializeToUtf8Bytes(new Contracts.Agent.AgentCollectDirectoryForReleaseCommand(instanceId, streamId));
            AgentCommandResult result = await connection.SendCommandAsync(new AgentCommand("CollectDirectoryForRelease", payload), cancellationToken).ConfigureAwait(false);
            if (!result.Succeeded)
            {
                _ = receiver.Writer.TryComplete(new IOException(result.ErrorCode ?? "agent-command-failed"));
                throw new IOException(result.ErrorCode ?? "agent-command-failed");
            }

            await drain.ConfigureAwait(false);
            return JsonSerializer.Deserialize<Contracts.Agent.AgentCollectedDirectoryResult>(result.Payload.Span)
                ?? throw new InvalidDataException("The Agent content collection response is invalid.");
        }
        finally
        {
            _ = _contentReceivers.TryRemove(new KeyValuePair<string, FileDownloadReceiver>(streamId, receiver));
            _ = receiver.Writer.TryComplete();
        }
    }

    public async Task<AgentCommandResult> SyncReleaseAsync(
        Guid nodeId,
        Contracts.Agent.AgentSyncFromReleaseCommand command,
        Stream archive,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(archive);
        if (!_connections.TryGetValue(nodeId, out AgentConnection? connection))
        {
            return new(false, ReadOnlyMemory<byte>.Empty, "node-offline");
        }

        try
        {
            if (!archive.CanSeek)
            {
                throw new InvalidOperationException("The content-sync source must expose its length for disk preflight.");
            }

            long transferLength = archive.Length - archive.Position;
            await connection.SendAsync(CreateSyncControl(command, StreamControlAction.Open, transferLength), cancellationToken).ConfigureAwait(false);
            byte[] buffer = new byte[256 * 1024];
            ulong sequence = 0;
            while (true)
            {
                int read = await archive.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                await connection.SendAsync(new ServerMessage
                {
                    StreamChunk = new StreamChunk
                    {
                        StreamId = command.StreamId,
                        Sequence = sequence++,
                        Data = ByteString.CopyFrom(buffer, 0, read),
                    },
                }, cancellationToken).ConfigureAwait(false);
            }

            await connection.SendAsync(CreateSyncControl(command, StreamControlAction.Close, transferLength), cancellationToken).ConfigureAwait(false);
            return await connection.SendCommandAsync(
                new AgentCommand("SyncFromRelease", JsonSerializer.SerializeToUtf8Bytes(command)),
                cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            try
            {
                using CancellationTokenSource cleanup = new(TimeSpan.FromSeconds(5));
                await connection.SendAsync(CreateSyncControl(command, StreamControlAction.Abort, 0), cleanup.Token).ConfigureAwait(false);
            }
            catch (RpcException cleanupException)
            {
                SyncAbortFailed(logger, command.StreamId, cleanupException.Message);
            }
            catch (OperationCanceledException cleanupException)
            {
                SyncAbortFailed(logger, command.StreamId, cleanupException.Message);
            }
            catch (IOException cleanupException)
            {
                SyncAbortFailed(logger, command.StreamId, cleanupException.Message);
            }
            catch (InvalidOperationException cleanupException)
            {
                SyncAbortFailed(logger, command.StreamId, cleanupException.Message);
            }

            throw;
        }
    }

    private static ServerMessage CreateSyncControl(Contracts.Agent.AgentSyncFromReleaseCommand command, StreamControlAction action, long length)
    {
        return new ServerMessage
        {
            StreamControl = new StreamControl
            {
                StreamId = command.StreamId,
                Kind = StreamKind.ContentSync,
                Action = action,
                ResourceId = JsonSerializer.Serialize(new Contracts.Agent.AgentInboundTransferDescriptor(command.InstanceId.ToString("D"), length)),
            },
        };
    }

    [LoggerMessage(4201, LogLevel.Warning, "Agent content-sync abort failed for stream {StreamId}: {Reason}")]
    private static partial void SyncAbortFailed(ILogger logger, string streamId, string reason);

    public async Task OpenTerminalAsync(
        Guid nodeId,
        string streamId,
        Guid instanceId,
        Func<Guid, StreamChunk, CancellationToken, Task> receiver,
        CancellationToken cancellationToken)
    {
        if (!_connections.TryGetValue(nodeId, out AgentConnection? connection))
        {
            throw new InvalidOperationException("node-offline");
        }

        if (!_terminalReceivers.TryAdd(streamId, new(nodeId, receiver)))
        {
            throw new InvalidOperationException("conflict");
        }

        try
        {
            await connection.SendAsync(new ServerMessage
            {
                StreamControl = new StreamControl
                {
                    StreamId = streamId,
                    Kind = StreamKind.Terminal,
                    Action = StreamControlAction.Open,
                    ResourceId = instanceId.ToString("D"),
                },
            }, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            _ = _terminalReceivers.TryRemove(streamId, out _);
            throw;
        }
    }

    public async Task SendTerminalChunkAsync(Guid nodeId, StreamChunk chunk, CancellationToken cancellationToken)
    {
        if (!_connections.TryGetValue(nodeId, out AgentConnection? connection))
        {
            throw new InvalidOperationException("node-offline");
        }

        await connection.SendAsync(new ServerMessage { StreamChunk = chunk }, cancellationToken).ConfigureAwait(false);
    }

    public async Task CloseTerminalAsync(Guid nodeId, string streamId, Guid instanceId, CancellationToken cancellationToken)
    {
        _ = _terminalReceivers.TryRemove(streamId, out _);
        if (_connections.TryGetValue(nodeId, out AgentConnection? connection))
        {
            await connection.SendAsync(new ServerMessage
            {
                StreamControl = new StreamControl
                {
                    StreamId = streamId,
                    Kind = StreamKind.Terminal,
                    Action = StreamControlAction.Close,
                    ResourceId = instanceId.ToString("D"),
                },
            }, cancellationToken).ConfigureAwait(false);
        }
    }

    public Task ReceiveTerminalChunkAsync(Guid nodeId, StreamChunk chunk, CancellationToken cancellationToken)
    {
        return _terminalReceivers.TryGetValue(chunk.StreamId, out TerminalReceiver? receiver) && receiver.NodeId == nodeId
            ? receiver.Callback(nodeId, chunk, cancellationToken)
            : Task.CompletedTask;
    }

    internal async Task ReceiveStreamChunkAsync(Guid nodeId, StreamChunk chunk, CancellationToken cancellationToken)
    {
        if (_downloadReceivers.TryGetValue(chunk.StreamId, out FileDownloadReceiver? receiver) && receiver.NodeId == nodeId)
        {
            if (chunk.Sequence != receiver.NextSequence || chunk.Data.Length > 256 * 1024)
            {
                _ = receiver.Writer.TryComplete(new InvalidDataException("The Agent file stream sequence is invalid."));
                _ = _downloadReceivers.TryRemove(new KeyValuePair<string, FileDownloadReceiver>(chunk.StreamId, receiver));
                throw new InvalidDataException("The Agent file stream sequence is invalid.");
            }

            receiver.NextSequence++;
            if (chunk.Data.Length > 0)
            {
                await receiver.Writer.WriteAsync(chunk.Data.ToByteArray(), cancellationToken).ConfigureAwait(false);
            }

            if (chunk.EndOfStream)
            {
                receiver.Completed = true;
                _ = receiver.Writer.TryComplete();
                _ = _downloadReceivers.TryRemove(new KeyValuePair<string, FileDownloadReceiver>(chunk.StreamId, receiver));
            }

            return;
        }

        if (_contentReceivers.TryGetValue(chunk.StreamId, out FileDownloadReceiver? contentReceiver) && contentReceiver.NodeId == nodeId)
        {
            await ReceiveFileChunkAsync(_contentReceivers, nodeId, chunk, contentReceiver, cancellationToken).ConfigureAwait(false);
            return;
        }

        await ReceiveTerminalChunkAsync(nodeId, chunk, cancellationToken).ConfigureAwait(false);
    }

    private sealed record TerminalReceiver(Guid NodeId, Func<Guid, StreamChunk, CancellationToken, Task> Callback);

    private sealed class FileDownloadReceiver(Guid nodeId)
    {
        private readonly Channel<byte[]> _channel = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(8)
        {
            SingleReader = true,
            SingleWriter = true,
            FullMode = BoundedChannelFullMode.Wait,
            AllowSynchronousContinuations = false,
        });

        internal Guid NodeId { get; } = nodeId;
        internal ulong NextSequence { get; set; }
        internal bool Completed { get; set; }
        internal ChannelReader<byte[]> Reader => _channel.Reader;
        internal ChannelWriter<byte[]> Writer => _channel.Writer;
    }

    private void RemoveTerminalReceivers(Guid nodeId)
    {
        foreach ((string streamId, TerminalReceiver receiver) in _terminalReceivers)
        {
            if (receiver.NodeId == nodeId)
            {
                _ = _terminalReceivers.TryRemove(new KeyValuePair<string, TerminalReceiver>(streamId, receiver));
            }
        }
    }

    private void RemoveDownloadReceivers(Guid nodeId)
    {
        foreach ((string streamId, FileDownloadReceiver receiver) in _downloadReceivers)
        {
            if (receiver.NodeId == nodeId && _downloadReceivers.TryRemove(new KeyValuePair<string, FileDownloadReceiver>(streamId, receiver)))
            {
                _ = receiver.Writer.TryComplete(new IOException("node-offline"));
            }
        }
    }

    private void RemoveContentReceivers(Guid nodeId)
    {
        foreach ((string streamId, FileDownloadReceiver receiver) in _contentReceivers)
        {
            if (receiver.NodeId == nodeId && _contentReceivers.TryRemove(new KeyValuePair<string, FileDownloadReceiver>(streamId, receiver)))
            {
                _ = receiver.Writer.TryComplete(new IOException("node-offline"));
            }
        }
    }

    private static async Task DrainAsync(FileDownloadReceiver receiver, Stream destination, CancellationToken cancellationToken)
    {
        await foreach (byte[] chunk in receiver.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            await destination.WriteAsync(chunk, cancellationToken).ConfigureAwait(false);
        }

        await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
        if (!receiver.Completed)
        {
            throw new IOException("node-offline");
        }
    }

    private static async Task ReceiveFileChunkAsync(
        ConcurrentDictionary<string, FileDownloadReceiver> receivers,
        Guid nodeId,
        StreamChunk chunk,
        FileDownloadReceiver receiver,
        CancellationToken cancellationToken)
    {
        if (receiver.NodeId != nodeId || chunk.Sequence != receiver.NextSequence || chunk.Data.Length > 256 * 1024)
        {
            _ = receiver.Writer.TryComplete(new InvalidDataException("The Agent content stream sequence is invalid."));
            _ = receivers.TryRemove(new KeyValuePair<string, FileDownloadReceiver>(chunk.StreamId, receiver));
            throw new InvalidDataException("The Agent content stream sequence is invalid.");
        }

        receiver.NextSequence++;
        if (chunk.Data.Length > 0)
        {
            await receiver.Writer.WriteAsync(chunk.Data.ToByteArray(), cancellationToken).ConfigureAwait(false);
        }

        if (chunk.EndOfStream)
        {
            receiver.Completed = true;
            _ = receiver.Writer.TryComplete();
            _ = receivers.TryRemove(new KeyValuePair<string, FileDownloadReceiver>(chunk.StreamId, receiver));
        }
    }
}

internal sealed class AgentConnection(
    IServerStreamWriter<ServerMessage> responseStream,
    CancellationToken connectionCancellation) : IDisposable
{
    private readonly ConcurrentDictionary<string, TaskCompletionSource<CommandResponse>> _pending = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly CancellationTokenSource _closed = CancellationTokenSource.CreateLinkedTokenSource(connectionCancellation);
    private bool _disposed;

    internal async Task<AgentCommandResult> SendCommandAsync(AgentCommand command, CancellationToken cancellationToken)
    {
        string commandId = Guid.NewGuid().ToString("N");
        if (!Enum.TryParse(command.Type, true, out CommandType commandType) || commandType == CommandType.Unspecified)
        {
            return new(false, ReadOnlyMemory<byte>.Empty, "validation-failed");
        }

        TaskCompletionSource<CommandResponse> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_pending.TryAdd(commandId, completion))
        {
            return new(false, ReadOnlyMemory<byte>.Empty, "conflict");
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _closed.Token);
        timeout.CancelAfter(TimeSpan.FromMinutes(10));
        try
        {
            await SendAsync(new ServerMessage
            {
                CommandRequest = new CommandRequest
                {
                    CommandId = commandId,
                    Type = commandType,
                    Payload = ByteString.CopyFrom(command.Payload.Span),
                    TimeoutSeconds = 600,
                },
            }, timeout.Token).ConfigureAwait(false);
            CommandResponse response = await completion.Task.WaitAsync(timeout.Token).ConfigureAwait(false);
            return response.Succeeded
                ? new(true, response.Payload.Memory, null)
                : new(false, response.Payload.Memory, response.Error?.Code ?? "internal-error");
        }
        catch (OperationCanceledException) when (_closed.IsCancellationRequested)
        {
            return new(false, ReadOnlyMemory<byte>.Empty, "node-offline");
        }
        catch (OperationCanceledException)
        {
            return new(false, ReadOnlyMemory<byte>.Empty, "agent-timeout");
        }
        finally
        {
            _ = _pending.TryRemove(commandId, out _);
        }
    }

    internal async Task SendAsync(ServerMessage message, CancellationToken cancellationToken)
    {
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await responseStream.WriteAsync(message, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _ = _writeGate.Release();
        }
    }

    internal void Complete(CommandResponse response)
    {
        if (_pending.TryGetValue(response.CommandId, out TaskCompletionSource<CommandResponse>? completion))
        {
            _ = completion.TrySetResult(response);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _closed.Cancel();
        foreach (TaskCompletionSource<CommandResponse> completion in _pending.Values)
        {
            _ = completion.TrySetCanceled(_closed.Token);
        }

        _closed.Dispose();
        _writeGate.Dispose();
    }
}
