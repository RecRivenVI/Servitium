using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using Servitium.Application.Abstractions;
using Servitium.Application.Identity;
using Servitium.Application.Nodes;
using Servitium.Contracts.Agent;
using Servitium.Domain.Primitives;

namespace Servitium.Server.AgentGateway;

/// <summary>验证 Agent 权威快照并原子替换控制平面镜像。</summary>
internal sealed class NodeReconciler(INodeControlStore store, IIdentityStore identities, IAgentGateway gateway, IClock clock)
{
    internal async Task<StoredNodeMirror> RefreshAsync(Guid nodeId, CancellationToken cancellationToken)
    {
        AgentCommandResult result = await gateway.SendAsync(nodeId, new("Reconcile", ReadOnlyMemory<byte>.Empty), cancellationToken).ConfigureAwait(false);
        return !result.Succeeded
            ? throw new DomainException(result.ErrorCode ?? "agent-reconcile-failed", "The Agent reconciliation command failed.")
            : await ApplyAsync(nodeId, result.Payload, cancellationToken).ConfigureAwait(false);
    }

    internal async Task<StoredNodeMirror> ApplyAsync(Guid nodeId, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken)
    {
        AgentReconcileResponse snapshot;
        try
        {
            snapshot = JsonSerializer.Deserialize<AgentReconcileResponse>(payload.Span)
                ?? throw new JsonException("The reconciliation snapshot is empty.");
        }
        catch (JsonException)
        {
            throw new DomainException("agent-reconcile-invalid", "The Agent reconciliation snapshot is invalid.");
        }

        Validate(snapshot);
        IReadOnlyList<StoredNodeMirror> previous = await store.ListMirrorsAsync(nodeId, cancellationToken).ConfigureAwait(false);
        HashSet<Guid> currentIds = [.. snapshot.Instances.Select(item => item.Id)];
        Guid[] removedIds = [.. previous.SelectMany(item => item.Instances).Select(item => item.Id).Where(id => !currentIds.Contains(id)).Distinct()];
        StoredNodeMirror mirror = new(nodeId, snapshot.Instances, snapshot.JavaRuntimes, snapshot.Schedules, clock.UtcNow);
        await store.ReplaceMirrorAsync(mirror, cancellationToken).ConfigureAwait(false);
        foreach (Guid removedId in removedIds)
        {
            _ = await identities.RemoveInstanceGrantsAsync(removedId, cancellationToken).ConfigureAwait(false);
        }

        return mirror;
    }

    private static void Validate(AgentReconcileResponse snapshot)
    {
        if (snapshot.Instances.Count > 64 || snapshot.JavaRuntimes.Count > 512 || snapshot.Schedules.Count > 8192 ||
            !Unique(snapshot.Instances.Select(item => item.Id)) || !Unique(snapshot.JavaRuntimes.Select(item => item.Id)) || !Unique(snapshot.Schedules.Select(item => item.Id)))
        {
            throw new DomainException("agent-reconcile-invalid", "The Agent reconciliation snapshot exceeds limits or contains duplicate identifiers.");
        }

        HashSet<Guid> instanceIds = [.. snapshot.Instances.Select(item => item.Id)];
        HashSet<Guid> javaIds = [.. snapshot.JavaRuntimes.Select(item => item.Id)];
        if (snapshot.Instances.Any(item =>
                item.Id == Guid.Empty || string.IsNullOrWhiteSpace(item.Name) || string.IsNullOrWhiteSpace(item.WorkingDirectory) ||
                item.Configuration?.Rcon.Password is { Length: > 0 } || !javaIds.Contains(item.Launch.JavaRuntimeId)) ||
            snapshot.JavaRuntimes.Any(item => item.Id == Guid.Empty || string.IsNullOrWhiteSpace(item.ExecutablePath)) ||
            snapshot.Schedules.Any(item => item.Id == Guid.Empty || !instanceIds.Contains(item.InstanceId)))
        {
            throw new DomainException("agent-reconcile-invalid", "The Agent reconciliation snapshot contains invalid references or secret material.");
        }
    }

    private static bool Unique(IEnumerable<Guid> identifiers)
    {
        HashSet<Guid> seen = [];
        return identifiers.All(seen.Add);
    }
}
