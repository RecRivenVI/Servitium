using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.AspNetCore.SignalR.Client;

using Servitium.Contracts.Events;

namespace Servitium.Client.Realtime;

/// <summary>管理全局事件 Hub、作用域订阅、自动重连与 Resume。</summary>
public sealed class EventsConnection : IDisposable
{
    private static readonly TimeSpan[] ReconnectDelays =
        [TimeSpan.Zero, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(10)];
    private readonly ServitiumApiClient _client;
    private readonly HashSet<string> _scopes = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private HubConnection? _connection;
    private long _lastEventId;
    private Guid? _scopeUserId;
    private string? _scopeRole;
    private bool _disposed;

    internal EventsConnection(ServitiumApiClient client)
    {
        _client = client;
    }

    /// <summary>收到实时事件。</summary>
    public event EventHandler<RealtimeEventArgs>? EventReceived;

    /// <summary>Resume 缓冲不足，需要调用方刷新当前数据。</summary>
    public event EventHandler? FullRefreshRequired;

    /// <summary>当前账户的授权或会话已变化，需要重新读取身份并卸载不再允许的内容。</summary>
    public event EventHandler? AuthorizationChanged;

    /// <summary>连接状态变化。</summary>
    public event EventHandler<ConnectionStateChangedEventArgs>? StateChanged;

    /// <summary>启动事件连接；管理员订阅全局，普通账户只订阅显式选择的授权范围。</summary>
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_connection?.State == HubConnectionState.Connected)
            {
                return;
            }

            if (_connection is not null)
            {
                await _connection.DisposeAsync().ConfigureAwait(false);
            }

            _connection = BuildConnection();
            await _connection.StartAsync(cancellationToken).ConfigureAwait(false);
            RaiseStateChanged("connected");
            lock (_scopes)
            {
                var principal = _client.Session.Principal;
                if (_scopeUserId != principal?.User.Id || _scopeRole != principal?.User.Role)
                {
                    // 切换账户不能沿用旧身份的订阅与事件游标。
                    _scopes.Clear();
                    _scopeUserId = principal?.User.Id;
                    _scopeRole = principal?.User.Role;
                    _ = Interlocked.Exchange(ref _lastEventId, 0);
                }
                if (principal?.User.Role is "Owner" or "Administrator") { _ = _scopes.Add("global"); }
                else { _ = _scopes.Remove("global"); }
            }
            await ResumeAndSubscribeAsync(_connection, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _ = _gate.Release();
        }
    }

    /// <summary>订阅授权范围。</summary>
    public async Task SubscribeAsync(string scope, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scope);
        lock (_scopes)
        {
            if (!_scopes.Add(scope)) { return; }
        }

        if (_connection?.State == HubConnectionState.Connected)
        {
            try
            {
                await _connection.InvokeAsync("Subscribe", scope, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                // 被服务端拒绝的订阅不能留在重连集合中，也不能让下次调用伪装成成功。
                lock (_scopes) { _ = _scopes.Remove(scope); }
                throw;
            }
        }
    }

    /// <summary>退订范围。</summary>
    public async Task UnsubscribeAsync(string scope, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scope);
        lock (_scopes)
        {
            _ = _scopes.Remove(scope);
        }

        if (_connection?.State == HubConnectionState.Connected)
        {
            await _connection.InvokeAsync("Unsubscribe", scope, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>会话变化后重建连接，使握手使用最新 Cookie。</summary>
    public async Task ReconnectForSessionAsync(CancellationToken cancellationToken)
    {
        if (_connection is null)
        {
            return;
        }

        await StopAsync(cancellationToken).ConfigureAwait(false);
        if (_client.SessionToken is not null)
        {
            await StartAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>停止连接。</summary>
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_connection is not null)
            {
                await _connection.StopAsync(cancellationToken).ConfigureAwait(false);
                await _connection.DisposeAsync().ConfigureAwait(false);
                _connection = null;
                RaiseStateChanged("disconnected");
            }
        }
        finally
        {
            _ = _gate.Release();
        }
    }

    private HubConnection BuildConnection()
    {
        Uri endpoint = new(_client.BaseAddress, "hubs/v1/events");
        HubConnection connection = new HubConnectionBuilder()
            .WithUrl(endpoint, _client.ConfigureHubAuthentication)
            .WithAutomaticReconnect(ReconnectDelays)
            .Build();
        _ = connection.On<RealtimeEventDto>("event", OnEvent);
        _ = connection.On<AuthorizationChangedPayload>("authorizationChanged", _ =>
        {
            // 服务端随后中断连接；不能在自动重连时重新订阅已撤回的范围。
            lock (_scopes) { _scopes.Clear(); }
            AuthorizationChanged?.Invoke(this, EventArgs.Empty);
        });
        connection.Reconnecting += _ =>
        {
            RaiseStateChanged("reconnecting");
            return Task.CompletedTask;
        };
        connection.Reconnected += async _ =>
        {
            RaiseStateChanged("connected");
            await ResumeAndSubscribeAsync(connection, CancellationToken.None).ConfigureAwait(false);
        };
        connection.Closed += _ =>
        {
            RaiseStateChanged("disconnected");
            return Task.CompletedTask;
        };
        return connection;
    }

    private void OnEvent(RealtimeEventDto realtimeEvent)
    {
        _ = Interlocked.Exchange(ref _lastEventId, Math.Max(Interlocked.Read(ref _lastEventId), realtimeEvent.EventId));
        EventReceived?.Invoke(this, new RealtimeEventArgs(realtimeEvent));
    }

    private void RaiseStateChanged(string state)
    {
        StateChanged?.Invoke(this, new ConnectionStateChangedEventArgs(state));
    }

    private async Task ResumeAndSubscribeAsync(HubConnection connection, CancellationToken cancellationToken)
    {
        string[] scopes;
        lock (_scopes)
        {
            scopes = [.. _scopes];
        }

        foreach (string scope in scopes)
        {
            await connection.InvokeAsync("Subscribe", scope, cancellationToken).ConfigureAwait(false);
        }

        EventResumeDto resumed = await connection.InvokeAsync<EventResumeDto>(
            "Resume", Interlocked.Read(ref _lastEventId), cancellationToken).ConfigureAwait(false);
        foreach (RealtimeEventDto realtimeEvent in resumed.Events)
        {
            OnEvent(realtimeEvent);
        }

        if (resumed.RequiresFullRefresh)
        {
            FullRefreshRequired?.Invoke(this, EventArgs.Empty);
        }
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

        _gate.Dispose();
    }

    private static async Task DisposeConnectionAsync(HubConnection connection)
    {
        await connection.DisposeAsync().ConfigureAwait(false);
    }
}
