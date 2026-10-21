using System;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.AspNetCore.SignalR.Client;

using Servitium.Contracts.Instances;

namespace Servitium.Client.Realtime;

/// <summary>管理单个终端 Hub 会话。</summary>
public sealed class TerminalConnection : IDisposable
{
    private readonly ServitiumApiClient _client;
    private HubConnection? _connection;
    private bool _disposed;

    internal TerminalConnection(ServitiumApiClient client)
    {
        _client = client;
    }

    /// <summary>收到终端输出帧。</summary>
    public event EventHandler<TerminalOutputEventArgs>? OutputReceived;

    /// <summary>连接并附加实例终端。</summary>
    public async Task<TerminalAttachDto> AttachAsync(Guid instanceId, int columns, int rows, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await DetachAsync(cancellationToken).ConfigureAwait(false);
        Uri endpoint = new(_client.BaseAddress, "hubs/v1/terminal");
        _connection = new HubConnectionBuilder()
            .WithUrl(endpoint, _client.ConfigureHubAuthentication)
            .WithAutomaticReconnect()
            .Build();
        _ = _connection.On<TerminalOutputFrame>("output", frame =>
            OutputReceived?.Invoke(this, new TerminalOutputEventArgs(frame)));
        await _connection.StartAsync(cancellationToken).ConfigureAwait(false);
        return await _connection.InvokeAsync<TerminalAttachDto>(
            "Attach", new TerminalAttachRequest(instanceId, columns, rows), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>发送终端输入。</summary>
    public Task SendInputAsync(string text, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(text);
        return RequireConnection().InvokeAsync("SendInput", text, cancellationToken);
    }

    /// <summary>调整终端尺寸。</summary>
    public Task ResizeAsync(int columns, int rows, CancellationToken cancellationToken)
    {
        return RequireConnection().InvokeAsync("Resize", columns, rows, cancellationToken);
    }

    /// <summary>解除附加并关闭连接。</summary>
    public async Task DetachAsync(CancellationToken cancellationToken)
    {
        HubConnection? connection = _connection;
        _connection = null;
        if (connection is null)
        {
            return;
        }

        if (connection.State == HubConnectionState.Connected)
        {
            await connection.InvokeAsync("Detach", cancellationToken).ConfigureAwait(false);
        }

        await connection.StopAsync(cancellationToken).ConfigureAwait(false);
        await connection.DisposeAsync().ConfigureAwait(false);
    }

    private HubConnection RequireConnection()
    {
        return _connection is { State: HubConnectionState.Connected } connection
            ? connection
            : throw new InvalidOperationException("The terminal is not connected.");
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        HubConnection? connection = _connection;
        _connection = null;
        if (connection is not null)
        {
            _ = DisposeConnectionAsync(connection);
        }
    }

    private static async Task DisposeConnectionAsync(HubConnection connection)
    {
        await connection.DisposeAsync().ConfigureAwait(false);
    }
}
