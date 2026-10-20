using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.AspNetCore.SignalR;

using Servitium.Application.Authorization;
using Servitium.Application.Identity;
using Servitium.Application.Nodes;
using Servitium.Contracts.Instances;
using Servitium.Server.AgentGateway;
using Servitium.Server.Security;

namespace Servitium.Server.Hubs;

internal sealed class TerminalHub(
    IAuthorizationService authorization,
    INodeControlStore nodes,
    TerminalStreamBroker broker) : Hub
{
    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        await broker.DetachAsync(Context.ConnectionId, CancellationToken.None).ConfigureAwait(false);
        await base.OnDisconnectedAsync(exception).ConfigureAwait(false);
    }

    public async Task<TerminalAttachDto> Attach(TerminalAttachRequest request)
    {
        AuthorizationPrincipal principal = Principal();
        EnsureAuthorized(principal, request.InstanceId, ServitiumAction.UseConsole);
        StoredNodeMirror? mirror = (await nodes.ListMirrorsAsync(null, Context.ConnectionAborted).ConfigureAwait(false))
            .SingleOrDefault(item => item.Instances.Any(instance => instance.Id == request.InstanceId)) ?? throw new HubException("not-found");
        try
        {
            return await broker.AttachAsync(
                principal.UserId,
                Context.ConnectionId,
                mirror.NodeId,
                request.InstanceId,
                request.Columns,
                request.Rows,
                Context.ConnectionAborted).ConfigureAwait(false);
        }
        catch (InvalidOperationException exception)
        {
            throw new HubException(exception.Message);
        }
    }

    public Task SendInput(string text)
    {
        return broker.SendInputAsync(Context.ConnectionId, text, Context.ConnectionAborted);
    }

    public Task Resize(int columns, int rows)
    {
        return broker.ResizeAsync(Context.ConnectionId, columns, rows, Context.ConnectionAborted);
    }

    public Task Detach()
    {
        return broker.DetachAsync(Context.ConnectionId, Context.ConnectionAborted);
    }

    private AuthorizationPrincipal Principal()
    {
        AuthenticationResult authentication = Context.GetHttpContext() is { } http && RequestAuthentication.Get(http) is { } result
            ? result
            : throw new HubException("unauthenticated");
        return new(authentication.User.Id, authentication.User.Role, authentication.GrantedInstanceIds);
    }

    private void EnsureAuthorized(AuthorizationPrincipal principal, Guid instanceId, ServitiumAction action)
    {
        if (!authorization.Authorize(principal, new(ResourceKind.Instance, instanceId), action).Allowed)
        {
            throw new HubException("forbidden");
        }
    }
}
