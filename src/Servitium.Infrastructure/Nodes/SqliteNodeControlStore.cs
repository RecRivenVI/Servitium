using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Servitium.Application.Nodes;
using Servitium.Contracts.Nodes;
using Servitium.Contracts.Agent;
using Servitium.Contracts.Scheduling;
using Servitium.Infrastructure.Persistence;
using Servitium.Application.Abstractions;

namespace Servitium.Infrastructure.Nodes;

internal sealed partial class SqliteNodeControlStore(
    IDbContextFactory<ServitiumDbContext> contextFactory,
    SqliteWriteCoordinator writeCoordinator,
    IClock clock) : INodeControlStore
{
    public Task AddRegistrationTokenAsync(StoredNodeRegistrationToken token, CancellationToken cancellationToken)
    {
        return writeCoordinator.ExecuteAsync(
            async () =>
            {
                using ServitiumDbContext context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
                _ = context.NodeRegistrationTokens.Add(ToRow(token));
                _ = await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            },
            cancellationToken);
    }

    public async Task<IReadOnlyList<StoredNodeRegistrationToken>> ListRegistrationTokensAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        using ServitiumDbContext context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        NodeRegistrationTokenRow[] rows = await context.NodeRegistrationTokens.AsNoTracking()
            .Where(row => !row.Revoked && row.ConsumedAt == null)
            .ToArrayAsync(cancellationToken)
            .ConfigureAwait(false);
        return [.. rows.Where(row => row.ExpiresAt > now).OrderBy(row => row.ExpiresAt).Select(FromRow)];
    }

    public Task RevokeRegistrationTokenAsync(Guid tokenId, CancellationToken cancellationToken)
    {
        return writeCoordinator.ExecuteAsync(
            async () =>
            {
                using ServitiumDbContext context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
                _ = await context.NodeRegistrationTokens.Where(row => row.Id == tokenId && row.ConsumedAt == null)
                    .ExecuteUpdateAsync(setters => setters.SetProperty(row => row.Revoked, true), cancellationToken)
                    .ConfigureAwait(false);
            },
            cancellationToken);
    }

    public Task<StoredNode?> TryRegisterAsync(
        string tokenHash,
        StoredNode node,
        StoredNodeCredential credential,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        return writeCoordinator.ExecuteAsync(
            async () =>
            {
                using ServitiumDbContext context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
                using IDbContextTransaction transaction = await context.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
                NodeRegistrationTokenRow? token = await context.NodeRegistrationTokens.SingleOrDefaultAsync(
                    row => row.TokenHash == tokenHash && !row.Revoked && row.ConsumedAt == null,
                    cancellationToken).ConfigureAwait(false);
                if (token is null || token.ExpiresAt <= now || (token.ExpectedName is not null && !string.Equals(token.ExpectedName, node.Name, StringComparison.OrdinalIgnoreCase)))
                {
                    return null;
                }

                token.ConsumedAt = now;
                _ = context.Nodes.Add(ToRow(node));
                _ = context.NodeCredentials.Add(ToRow(credential));
                _ = await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return node;
            },
            cancellationToken);
    }

    public async Task<StoredNode?> AuthenticateCredentialAsync(string credentialHash, CancellationToken cancellationToken)
    {
        using ServitiumDbContext context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        NodeCredentialRow? credential = await context.NodeCredentials.AsNoTracking()
            .SingleOrDefaultAsync(row => row.CredentialHash == credentialHash && row.RevokedAt == null, cancellationToken)
            .ConfigureAwait(false);
        if (credential is null)
        {
            return null;
        }

        NodeRow? node = await context.Nodes.AsNoTracking()
            .SingleOrDefaultAsync(row => row.Id == credential.NodeId && !row.CredentialRevoked, cancellationToken)
            .ConfigureAwait(false);
        if (node is null)
        {
            return null;
        }

        bool hasOlderActive = await context.NodeCredentials.AsNoTracking().AnyAsync(
            row => row.NodeId == credential.NodeId && row.Id != credential.Id && row.Revision < credential.Revision && row.RevokedAt == null,
            cancellationToken).ConfigureAwait(false);
        if (hasOlderActive)
        {
            await writeCoordinator.ExecuteAsync(
                async () =>
                {
                    using ServitiumDbContext writeContext = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
                    _ = await writeContext.NodeCredentials
                        .Where(row => row.NodeId == credential.NodeId && row.Id != credential.Id && row.Revision < credential.Revision && row.RevokedAt == null)
                        .ExecuteUpdateAsync(setters => setters.SetProperty(row => row.RevokedAt, clock.UtcNow), cancellationToken)
                        .ConfigureAwait(false);
                },
                cancellationToken).ConfigureAwait(false);
        }

        return FromRow(node);
    }

    public async Task<StoredNode?> FindNodeAsync(Guid nodeId, CancellationToken cancellationToken)
    {
        using ServitiumDbContext context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        NodeRow? row = await context.Nodes.AsNoTracking().SingleOrDefaultAsync(node => node.Id == nodeId, cancellationToken).ConfigureAwait(false);
        return row is null ? null : FromRow(row);
    }

    public async Task<IReadOnlyList<StoredNode>> ListNodesAsync(CancellationToken cancellationToken)
    {
        using ServitiumDbContext context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        NodeRow[] rows = await context.Nodes.AsNoTracking().OrderBy(row => row.Name).ToArrayAsync(cancellationToken).ConfigureAwait(false);
        return [.. rows.Select(FromRow)];
    }

    public Task UpdateNodeAsync(StoredNode node, CancellationToken cancellationToken)
    {
        return writeCoordinator.ExecuteAsync(
            async () =>
            {
                using ServitiumDbContext context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
                NodeRow row = await context.Nodes.SingleOrDefaultAsync(candidate => candidate.Id == node.Id, cancellationToken).ConfigureAwait(false)
                    ?? throw new Domain.Primitives.DomainException("not-found", "The node does not exist.");
                row.Name = node.Name;
                row.Status = node.Status;
                row.AgentVersion = node.AgentVersion;
                row.ProtocolMajor = node.ProtocolMajor;
                row.ProtocolMinor = node.ProtocolMinor;
                row.OperatingSystem = node.OperatingSystem;
                row.Architecture = node.Architecture;
                row.LastSeenAt = node.LastSeenAt;
                row.CredentialRevoked = node.CredentialRevoked;
                row.ConfigurationJson = node.Configuration is null ? null : JsonSerializer.Serialize(node.Configuration);
                _ = await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            },
            cancellationToken);
    }

    public Task RemoveNodeAsync(Guid nodeId, CancellationToken cancellationToken)
    {
        return writeCoordinator.ExecuteAsync(
            async () =>
            {
                using ServitiumDbContext context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
                _ = await context.Nodes.Where(row => row.Id == nodeId).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
            },
            cancellationToken);
    }

    public Task UpdateConnectionDetailsAsync(StoredNode node, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(node);
        return writeCoordinator.ExecuteAsync(async () =>
        {
            using ServitiumDbContext context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            _ = await context.Nodes.Where(row => row.Id == node.Id && !row.CredentialRevoked)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(row => row.HostName, node.HostName)
                    .SetProperty(row => row.IsLocal, node.IsLocal)
                    .SetProperty(row => row.OperatingSystem, node.OperatingSystem)
                    .SetProperty(row => row.Architecture, node.Architecture)
                    .SetProperty(row => row.ProtocolMajor, node.ProtocolMajor)
                    .SetProperty(row => row.ProtocolMinor, node.ProtocolMinor), cancellationToken).ConfigureAwait(false);
        }, cancellationToken);
    }

    public Task UpdateHeartbeatAsync(Guid nodeId, string status, string agentVersion, DateTimeOffset lastSeenAt, CancellationToken cancellationToken)
    {
        return writeCoordinator.ExecuteAsync(
            async () =>
            {
                using ServitiumDbContext context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
                _ = await context.Nodes.Where(row => row.Id == nodeId && !row.CredentialRevoked)
                    .ExecuteUpdateAsync(setters => setters
                        .SetProperty(row => row.Status, status)
                        .SetProperty(row => row.AgentVersion, agentVersion)
                        .SetProperty(row => row.LastSeenAt, lastSeenAt), cancellationToken)
                    .ConfigureAwait(false);
            },
            cancellationToken);
    }

    public Task RotateCredentialAsync(Guid nodeId, StoredNodeCredential replacement, CancellationToken cancellationToken)
    {
        return writeCoordinator.ExecuteAsync(
            async () =>
            {
                using ServitiumDbContext context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
                using IDbContextTransaction transaction = await context.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
                DateTimeOffset revokedAt = replacement.CreatedAt;
                _ = await context.NodeCredentials.Where(row => row.NodeId == nodeId && row.RevokedAt == null)
                    .ExecuteUpdateAsync(setters => setters.SetProperty(row => row.RevokedAt, revokedAt), cancellationToken)
                    .ConfigureAwait(false);
                _ = context.NodeCredentials.Add(ToRow(replacement));
                _ = await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            },
            cancellationToken);
    }

    public Task StageCredentialAsync(StoredNodeCredential replacement, CancellationToken cancellationToken)
    {
        return writeCoordinator.ExecuteAsync(
            async () =>
            {
                using ServitiumDbContext context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
                _ = context.NodeCredentials.Add(ToRow(replacement));
                _ = await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            },
            cancellationToken);
    }

    public Task ActivateCredentialAsync(Guid nodeId, Guid credentialId, DateTimeOffset revokedAt, CancellationToken cancellationToken)
    {
        return writeCoordinator.ExecuteAsync(
            async () =>
            {
                using ServitiumDbContext context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
                _ = await context.NodeCredentials
                    .Where(row => row.NodeId == nodeId && row.Id != credentialId && row.RevokedAt == null)
                    .ExecuteUpdateAsync(setters => setters.SetProperty(row => row.RevokedAt, revokedAt), cancellationToken)
                    .ConfigureAwait(false);
            },
            cancellationToken);
    }

    public Task RevokeCredentialAsync(Guid credentialId, DateTimeOffset revokedAt, CancellationToken cancellationToken)
    {
        return writeCoordinator.ExecuteAsync(
            async () =>
            {
                using ServitiumDbContext context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
                _ = await context.NodeCredentials.Where(row => row.Id == credentialId && row.RevokedAt == null)
                    .ExecuteUpdateAsync(setters => setters.SetProperty(row => row.RevokedAt, revokedAt), cancellationToken)
                    .ConfigureAwait(false);
            },
            cancellationToken);
    }

    public async Task<int> GetCredentialRevisionAsync(Guid nodeId, CancellationToken cancellationToken)
    {
        using ServitiumDbContext context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        return await context.NodeCredentials.Where(row => row.NodeId == nodeId)
            .Select(row => (int?)row.Revision)
            .MaxAsync(cancellationToken)
            .ConfigureAwait(false) ?? 0;
    }

    public Task RevokeCredentialsAsync(Guid nodeId, DateTimeOffset revokedAt, CancellationToken cancellationToken)
    {
        return writeCoordinator.ExecuteAsync(
            async () =>
            {
                using ServitiumDbContext context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
                using IDbContextTransaction transaction = await context.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
                _ = await context.NodeCredentials.Where(row => row.NodeId == nodeId && row.RevokedAt == null)
                    .ExecuteUpdateAsync(setters => setters.SetProperty(row => row.RevokedAt, revokedAt), cancellationToken)
                    .ConfigureAwait(false);
                _ = await context.Nodes.Where(row => row.Id == nodeId)
                    .ExecuteUpdateAsync(setters => setters
                        .SetProperty(row => row.CredentialRevoked, true)
                        .SetProperty(row => row.Status, "Revoked"), cancellationToken)
                    .ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            },
            cancellationToken);
    }

    public Task AddMetricAsync(StoredNodeMetric metric, DateTimeOffset retentionCutoff, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(metric);
        return writeCoordinator.ExecuteAsync(
            async () =>
            {
                using ServitiumDbContext context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
                NodeMetricRow[] existing = await context.NodeMetrics.Where(row => row.NodeId == metric.NodeId)
                    .ToArrayAsync(cancellationToken).ConfigureAwait(false);
                context.NodeMetrics.RemoveRange(existing.Where(row => row.SampledAt < retentionCutoff));
                NodeMetricsDto sample = metric.Metrics;
                _ = context.NodeMetrics.Add(new NodeMetricRow
                {
                    NodeId = metric.NodeId,
                    SampledAt = sample.SampledAt,
                    CpuPercent = sample.CpuPercent,
                    MemoryUsedBytes = sample.MemoryUsedBytes,
                    MemoryTotalBytes = sample.MemoryTotalBytes,
                    DiskFreeBytes = sample.DiskFreeBytes,
                    DiskTotalBytes = sample.DiskTotalBytes,
                    NetworkReceiveBytesPerSecond = sample.NetworkReceiveBytesPerSecond,
                    NetworkSendBytesPerSecond = sample.NetworkSendBytesPerSecond,
                    UptimeTicks = sample.Uptime.Ticks,
                });
                _ = await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            },
            cancellationToken);
    }

    public async Task<IReadOnlyList<StoredNodeMetric>> ListMetricsAsync(Guid nodeId, DateTimeOffset since, CancellationToken cancellationToken)
    {
        using ServitiumDbContext context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        NodeMetricRow[] rows = await context.NodeMetrics.AsNoTracking().Where(row => row.NodeId == nodeId)
            .ToArrayAsync(cancellationToken).ConfigureAwait(false);
        return [.. rows.Where(row => row.SampledAt >= since).OrderBy(row => row.SampledAt).Select(row => new StoredNodeMetric(
            row.NodeId,
            new NodeMetricsDto(
                row.SampledAt,
                row.CpuPercent,
                row.MemoryUsedBytes,
                row.MemoryTotalBytes,
                row.DiskFreeBytes,
                row.NetworkReceiveBytesPerSecond,
                row.NetworkSendBytesPerSecond,
                TimeSpan.FromTicks(row.UptimeTicks),
                row.DiskTotalBytes)))];
    }

    public Task ReplaceMirrorAsync(StoredNodeMirror mirror, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(mirror);
        return writeCoordinator.ExecuteAsync(
            async () =>
            {
                using ServitiumDbContext context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
                using IDbContextTransaction transaction = await context.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
                _ = await context.NodeInstanceMirrors.Where(row => row.NodeId == mirror.NodeId).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
                _ = await context.NodeJavaRuntimeMirrors.Where(row => row.NodeId == mirror.NodeId).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
                _ = await context.NodeScheduleMirrors.Where(row => row.NodeId == mirror.NodeId).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
                context.NodeInstanceMirrors.AddRange(mirror.Instances.Select(item => new NodeInstanceMirrorRow
                {
                    Id = item.Id,
                    NodeId = mirror.NodeId,
                    SnapshotJson = JsonSerializer.Serialize(item),
                }));
                context.NodeJavaRuntimeMirrors.AddRange(mirror.JavaRuntimes.Select(item => new NodeJavaRuntimeMirrorRow
                {
                    Id = item.Id,
                    NodeId = mirror.NodeId,
                    SnapshotJson = JsonSerializer.Serialize(item),
                }));
                context.NodeScheduleMirrors.AddRange(mirror.Schedules.Select(item => new NodeScheduleMirrorRow
                {
                    Id = item.Id,
                    NodeId = mirror.NodeId,
                    SnapshotJson = JsonSerializer.Serialize(item),
                }));
                NodeMirrorStateRow? state = await context.NodeMirrorStates.SingleOrDefaultAsync(row => row.NodeId == mirror.NodeId, cancellationToken).ConfigureAwait(false);
                if (state is null)
                {
                    _ = context.NodeMirrorStates.Add(new NodeMirrorStateRow { NodeId = mirror.NodeId, CapturedAt = mirror.CapturedAt });
                }
                else
                {
                    state.CapturedAt = mirror.CapturedAt;
                }

                _ = await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            },
            cancellationToken);
    }

    public Task UpdateInstanceMirrorStatusAsync(
        Guid nodeId,
        Guid instanceId,
        string status,
        DateTimeOffset capturedAt,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(status);
        return writeCoordinator.ExecuteAsync(
            async () =>
            {
                using ServitiumDbContext context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
                NodeInstanceMirrorRow row = await context.NodeInstanceMirrors
                    .SingleOrDefaultAsync(item => item.NodeId == nodeId && item.Id == instanceId, cancellationToken)
                    .ConfigureAwait(false)
                    ?? throw new InvalidOperationException("The Agent instance mirror does not exist.");
                AgentInstanceResponse snapshot = Deserialize<AgentInstanceResponse>(row.SnapshotJson);
                row.SnapshotJson = JsonSerializer.Serialize(snapshot with { Status = status });
                NodeMirrorStateRow state = await context.NodeMirrorStates
                    .SingleAsync(item => item.NodeId == nodeId, cancellationToken)
                    .ConfigureAwait(false);
                state.CapturedAt = capturedAt;
                _ = await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            },
            cancellationToken);
    }

    public async Task<IReadOnlyList<StoredNodeMirror>> ListMirrorsAsync(Guid? nodeId, CancellationToken cancellationToken)
    {
        using ServitiumDbContext context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        NodeMirrorStateRow[] states = nodeId is Guid id
            ? await context.NodeMirrorStates.AsNoTracking().Where(row => row.NodeId == id).ToArrayAsync(cancellationToken).ConfigureAwait(false)
            : await context.NodeMirrorStates.AsNoTracking().ToArrayAsync(cancellationToken).ConfigureAwait(false);
        if (states.Length == 0)
        {
            return [];
        }

        HashSet<Guid> nodeIds = [.. states.Select(row => row.NodeId)];
        NodeInstanceMirrorRow[] instances = await context.NodeInstanceMirrors.AsNoTracking().Where(row => nodeIds.Contains(row.NodeId)).ToArrayAsync(cancellationToken).ConfigureAwait(false);
        NodeJavaRuntimeMirrorRow[] java = await context.NodeJavaRuntimeMirrors.AsNoTracking().Where(row => nodeIds.Contains(row.NodeId)).ToArrayAsync(cancellationToken).ConfigureAwait(false);
        NodeScheduleMirrorRow[] schedules = await context.NodeScheduleMirrors.AsNoTracking().Where(row => nodeIds.Contains(row.NodeId)).ToArrayAsync(cancellationToken).ConfigureAwait(false);
        return [.. states.Select(state => new StoredNodeMirror(
            state.NodeId,
            [.. instances.Where(row => row.NodeId == state.NodeId).Select(row => Deserialize<AgentInstanceResponse>(row.SnapshotJson))],
            [.. java.Where(row => row.NodeId == state.NodeId).Select(row => Deserialize<AgentJavaRuntimeResponse>(row.SnapshotJson))],
            [.. schedules.Where(row => row.NodeId == state.NodeId).Select(row => Deserialize<ScheduledTaskDetailsDto>(row.SnapshotJson))],
            state.CapturedAt))];
    }

    private static T Deserialize<T>(string json)
    {
        return JsonSerializer.Deserialize<T>(json) ?? throw new InvalidOperationException("A persisted node mirror record is invalid.");
    }

    private static NodeRow ToRow(StoredNode node)
    {
        return new NodeRow
        {
            Id = node.Id,
            Name = node.Name,
            Status = node.Status,
            AgentVersion = node.AgentVersion,
            ProtocolMajor = node.ProtocolMajor,
            ProtocolMinor = node.ProtocolMinor,
            OperatingSystem = node.OperatingSystem,
            Architecture = node.Architecture,
            HostName = node.HostName,
            IsLocal = node.IsLocal,
            MachineFingerprint = node.MachineFingerprint,
            CreatedAt = node.CreatedAt,
            LastSeenAt = node.LastSeenAt,
            CredentialRevoked = node.CredentialRevoked,
            ConfigurationJson = node.Configuration is null ? null : JsonSerializer.Serialize(node.Configuration),
        };
    }

    private static StoredNode FromRow(NodeRow row)
    {
        return new(
            row.Id,
            row.Name,
            row.Status,
            row.AgentVersion,
            row.ProtocolMajor,
            row.ProtocolMinor,
            row.OperatingSystem,
            row.Architecture,
            row.MachineFingerprint,
            row.CreatedAt,
            row.LastSeenAt,
            row.CredentialRevoked,
            string.IsNullOrWhiteSpace(row.ConfigurationJson) ? null : JsonSerializer.Deserialize<UpdateNodeRequest>(row.ConfigurationJson),
            row.HostName,
            row.IsLocal);
    }

    private static NodeRegistrationTokenRow ToRow(StoredNodeRegistrationToken token)
    {
        return new NodeRegistrationTokenRow
        {
            Id = token.Id,
            TokenHash = token.TokenHash,
            ExpectedName = token.ExpectedName,
            CreatedAt = token.CreatedAt,
            ExpiresAt = token.ExpiresAt,
            ConsumedAt = token.ConsumedAt,
            Revoked = token.Revoked,
        };
    }

    private static StoredNodeRegistrationToken FromRow(NodeRegistrationTokenRow row)
    {
        return new(row.Id, row.TokenHash, row.ExpectedName, row.CreatedAt, row.ExpiresAt, row.ConsumedAt, row.Revoked);
    }

    private static NodeCredentialRow ToRow(StoredNodeCredential credential)
    {
        return new NodeCredentialRow
        {
            Id = credential.Id,
            NodeId = credential.NodeId,
            CredentialHash = credential.CredentialHash,
            CreatedAt = credential.CreatedAt,
            RevokedAt = credential.RevokedAt,
            Revision = credential.Revision,
        };
    }
}
