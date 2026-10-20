using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using CommunityToolkit.Mvvm.ComponentModel;

using Servitium.Client;
using Servitium.Client.Session;
using Servitium.Contracts.Transport;
using Servitium.Contracts.Distribution;
using Servitium.Contracts.Identity;
using Servitium.Contracts.Instances;
using Servitium.Contracts.Nodes;

namespace Servitium.Desktop.Services;

internal sealed partial class SessionService(
    ServerProfileService profiles,
    WindowsCredentialSessionTokenStore tokenStore,
    DesktopLogService log,
    TimeProvider timeProvider) : ObservableObject, IDisposable
{
    private ServitiumApiClient? _client;

    private ITimer? _renewalTimer;
    private int _renewalRunning;

    internal PrincipalDto? Principal
    {
        get;
        private set => SetProperty(ref field, value);
    }

    internal string ConnectionState
    {
        get;
        private set => SetProperty(ref field, value);
    } = "disconnected";

    internal NodeDto? SelectedNode
    {
        get;
        set => SetProperty(ref field, value);
    }

    internal Guid? PendingInstanceNodeFilter { get; set; }

    internal InstanceDto? SelectedInstance
    {
        get;
        set => SetProperty(ref field, value);
    }

    internal HmclChannelDto? SelectedChannel
    {
        get;
        set => SetProperty(ref field, value);
    }

    internal UserDto? SelectedUser
    {
        get;
        set => SetProperty(ref field, value);
    }

    internal HmclReleaseSummaryDto? SelectedRelease
    {
        get;
        set => SetProperty(ref field, value);
    }

    internal string? LocalAuthenticationErrorCode
    {
        get;
        private set => SetProperty(ref field, value);
    }

    internal ServitiumApiClient Client => _client ?? throw new InvalidOperationException("The Server client has not been initialized.");

    internal bool IsConnected => _client is not null;

    internal IReadOnlyList<NodeDto> Nodes { get; private set; } = [];

    internal IReadOnlyList<InstanceDto> Instances { get; private set; } = [];

    internal IReadOnlyList<HmclChannelDto> Channels { get; private set; } = [];

    internal async Task ConnectAsync(Uri address, CancellationToken cancellationToken)
    {
        _ = await profiles.AddOrSelectAsync(address, cancellationToken).ConfigureAwait(false);
        _client?.Dispose();
        _client = new ServitiumApiClient(profiles.Active.Address, tokenStore, timeProvider: timeProvider);
        ConnectionState = "connecting";
        AuthenticationStateDto state = await _client.Session.GetStateAsync(cancellationToken).ConfigureAwait(false);
        Principal = state.Principal;
        if (Principal is null)
        {
            bool restored = await _client.Session.RestoreAsync(cancellationToken).ConfigureAwait(false);
            Principal = _client.Session.Principal;
            if (!restored && state.Initialized)
            {
                LocalSessionResult local = await _client.Session.TryLocalSessionAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
                Principal = local.Principal;
                LocalAuthenticationErrorCode = local.Succeeded ? null : local.ErrorCode;
            }
        }

        ConnectionState = "connected";
        _renewalTimer?.Dispose();
        _renewalTimer = timeProvider.CreateTimer(
            _ => _ = RenewLocalSessionSafelyAsync(), null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
        if (Principal is not null)
        {
            await RefreshSelectionsAsync(cancellationToken).ConfigureAwait(false);
            await _client.Events.StartAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "Timer callbacks must contain transient connection failures and record a stable local diagnostic event.")]
    private async Task RenewLocalSessionSafelyAsync()
    {
        if (Interlocked.Exchange(ref _renewalRunning, 1) != 0)
        {
            return;
        }

        try
        {
            ServitiumApiClient? client = _client;
            if (client is null)
            {
                return;
            }

            LocalSessionResult? renewed = await client.Session.RenewLocalSessionIfDueAsync(CancellationToken.None).ConfigureAwait(false);
            if (renewed is not null)
            {
                Principal = renewed.Principal;
                LocalAuthenticationErrorCode = renewed.Succeeded ? null : renewed.ErrorCode;
            }
        }
        catch (Exception exception)
        {
            LocalAuthenticationErrorCode = "local-session-renewal-failed";
            await log.WriteAsync("warning", "session.local-renewal-failed", exception.GetType().Name).ConfigureAwait(false);
        }
        finally
        {
            _ = Interlocked.Exchange(ref _renewalRunning, 0);
        }
    }

    internal async Task<AuthenticationStateDto> GetAuthenticationStateAsync(CancellationToken cancellationToken)
    {
        AuthenticationStateDto state = await Client.Session.GetStateAsync(cancellationToken).ConfigureAwait(false);
        Principal = state.Principal;
        return state;
    }

    internal async Task<SignInResult> SignInAsync(SignInRequest request, CancellationToken cancellationToken)
    {
        SignInResult result = await Client.Session.SignInAsync(request, cancellationToken).ConfigureAwait(false);
        Principal = result.Principal;
        if (!result.TotpRequired)
        {
            await RefreshSelectionsAsync(cancellationToken).ConfigureAwait(false);
            await Client.Events.StartAsync(cancellationToken).ConfigureAwait(false);
        }

        return result;
    }

    internal async Task CompleteTotpAsync(string code, CancellationToken cancellationToken)
    {
        Principal = await Client.Session.CompleteTotpAsync(code, cancellationToken).ConfigureAwait(false);
        await RefreshSelectionsAsync(cancellationToken).ConfigureAwait(false);
        await Client.Events.StartAsync(cancellationToken).ConfigureAwait(false);
    }

    internal async Task SignInWithOidcAsync(CancellationToken cancellationToken)
    {
        Principal = await Client.Session.SignInWithOidcAsync(
            UserInteractionService.LaunchUriAsync,
            cancellationToken).ConfigureAwait(false);
        await RefreshSelectionsAsync(cancellationToken).ConfigureAwait(false);
        await Client.Events.StartAsync(cancellationToken).ConfigureAwait(false);
    }

    internal Task BindOidcAsync(CancellationToken cancellationToken)
    {
        return Client.Session.BindOidcAsync(UserInteractionService.LaunchUriAsync, cancellationToken);
    }

    internal async Task SignOutAsync(CancellationToken cancellationToken)
    {
        await Client.Session.SignOutAsync(cancellationToken).ConfigureAwait(false);
        Principal = null;
        Nodes = [];
        Instances = [];
        Channels = [];
        SelectedNode = null;
        SelectedInstance = null;
        SelectedChannel = null;
    }

    internal async Task RefreshSelectionsAsync(CancellationToken cancellationToken)
    {
        if (Principal is null)
        {
            return;
        }

        Instances = await ReadAllAsync(
            (cursor, token) => Client.ListInstancesAsync(null, null, null, [], cursor, 200, token),
            cancellationToken).ConfigureAwait(false);
        SelectedInstance = Preserve(SelectedInstance, Instances, item => item.Id);
        if (Principal.User.Role is "Owner" or "Administrator")
        {
            Nodes = await ReadAllAsync(
                (cursor, token) => Client.ListNodesAsync(null, null, cursor, 200, token),
                cancellationToken).ConfigureAwait(false);
            Channels = await ReadAllAsync(
                (cursor, token) => Client.ListHmclChannelsAsync(cursor, 200, token),
                cancellationToken).ConfigureAwait(false);
            SelectedNode = Preserve(SelectedNode, Nodes, item => item.Id);
            SelectedChannel = Preserve(SelectedChannel, Channels, item => item.Id);
        }
    }

    private static T? Preserve<T>(T? current, IReadOnlyList<T> items, Func<T, Guid> id) where T : class
    {
        Guid? currentId = current is null ? null : id(current);
        return currentId is Guid value
            ? items.FirstOrDefault(item => id(item) == value) ?? (items.Count > 0 ? items[0] : null)
            : items.Count > 0 ? items[0] : null;
    }

    private static async Task<IReadOnlyList<T>> ReadAllAsync<T>(
        Func<string?, CancellationToken, Task<CursorPage<T>>> read,
        CancellationToken cancellationToken)
    {
        const int MaximumItems = 10000;
        List<T> items = [];
        HashSet<string> cursors = new(StringComparer.Ordinal);
        string? cursor = null;
        do
        {
            CursorPage<T> page = await read(cursor, cancellationToken).ConfigureAwait(false);
            items.AddRange(page.Items);
            if (items.Count > MaximumItems)
            {
                throw new InvalidOperationException("The Server selection list exceeded the Desktop safety limit.");
            }

            cursor = page.NextCursor;
            if (cursor is not null && !cursors.Add(cursor))
            {
                throw new InvalidOperationException("The Server returned a repeated pagination cursor.");
            }
        }
        while (cursor is not null);

        return items;
    }

    public void Dispose()
    {
        _client?.Dispose();
        _client = null;
        _renewalTimer?.Dispose();
        _renewalTimer = null;
    }
}
