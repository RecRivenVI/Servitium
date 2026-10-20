using System;
using System.Threading;
using System.Threading.Tasks;

using Servitium.Infrastructure.Persistence;
using Servitium.Server.AgentGateway;

namespace Servitium.Server.Realtime;

/// <summary>在权限提交后统一终止受影响的实时连接与可取消操作。</summary>
internal sealed class AuthorizationChangeCoordinator(
    IRealtimeEventStream events,
    ControlPlaneStore operations,
    TerminalStreamBroker terminals)
{
    internal async Task InvalidateAsync(Guid userId, string reason, CancellationToken cancellationToken)
    {
        _ = await operations.CancelRunningByUserAsync(userId, cancellationToken).ConfigureAwait(false);
        await terminals.InvalidateUserAsync(userId, cancellationToken).ConfigureAwait(false);
        await events.InvalidateUserAsync(userId, reason, cancellationToken).ConfigureAwait(false);
    }
}
