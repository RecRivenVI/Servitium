using System;
using System.Threading.Tasks;

using Microsoft.AspNetCore.SignalR;

using Servitium.Application.Authorization;
using Servitium.Application.Identity;
using Servitium.Contracts.Events;
using Servitium.Server.Realtime;
using Servitium.Server.Security;

namespace Servitium.Server.Hubs;

internal sealed class EventsHub(
    IAuthorizationService authorization,
    IRealtimeEventStream events) : Hub
{
    public override Task OnConnectedAsync()
    {
        AuthenticationResult authentication = Authentication();
        events.Register(authentication.User.Id, Context);
        return base.OnConnectedAsync();
    }

    public override Task OnDisconnectedAsync(Exception? exception)
    {
        AuthenticationResult? authentication = Context.GetHttpContext() is { } http ? RequestAuthentication.Get(http) : null;
        if (authentication is not null)
        {
            events.Unregister(authentication.User.Id, Context.ConnectionId);
        }

        return base.OnDisconnectedAsync(exception);
    }

    public async Task Subscribe(string scope)
    {
        EnsureScopeAllowed(scope);
        await Groups.AddToGroupAsync(Context.ConnectionId, scope, Context.ConnectionAborted).ConfigureAwait(false);
    }

    public async Task Unsubscribe(string scope)
    {
        EnsureScopeAllowed(scope);
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, scope, Context.ConnectionAborted).ConfigureAwait(false);
    }

    public EventResumeDto Resume(long lastEventId)
    {
        return lastEventId < 0 ? throw new HubException("validation-failed") : events.Resume(lastEventId, ScopeAllowed);
    }

    private AuthenticationResult Authentication()
    {
        return Context.GetHttpContext() is { } http && RequestAuthentication.Get(http) is { } authentication
            ? authentication
            : throw new HubException("unauthenticated");
    }

    private void EnsureScopeAllowed(string scope)
    {
        if (!ScopeAllowed(scope))
        {
            throw new HubException("forbidden");
        }
    }

    private bool ScopeAllowed(string scope)
    {
        if (string.IsNullOrWhiteSpace(scope))
        {
            return false;
        }

        AuthenticationResult authentication = Authentication();
        AuthorizationPrincipal principal = new(authentication.User.Id, authentication.User.Role, authentication.GrantedInstanceIds);
        if (string.Equals(scope, "global", StringComparison.Ordinal))
        {
            return authorization.Authorize(principal, new(ResourceKind.Global), ServitiumAction.View).Allowed;
        }

        string[] parts = scope.Split(':', 2, StringSplitOptions.TrimEntries);
        if (parts.Length != 2 || !Guid.TryParse(parts[1], out Guid id))
        {
            return false;
        }

        AuthorizationResource resource = parts[0] switch
        {
            "instance" => new(ResourceKind.Instance, id),
            "node" => new(ResourceKind.Node, id),
            "operation" => new(ResourceKind.Operation, id),
            _ => new(ResourceKind.Global),
        };
        return parts[0] is "instance" or "node" or "operation" && authorization.Authorize(principal, resource, ServitiumAction.View).Allowed;
    }
}
