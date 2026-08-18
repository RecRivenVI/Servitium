using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using Servitium.Contracts.Nodes;
using Servitium.Contracts.Agent;
using Servitium.Contracts.Scheduling;

namespace Servitium.Application.Nodes;

/// <summary>控制平面节点快照。</summary>
public sealed record StoredNode(
    Guid Id,
    string Name,
    string Status,
    string? AgentVersion,
    uint ProtocolMajor,
    uint ProtocolMinor,
    string OperatingSystem,
    string Architecture,
    string MachineFingerprint,
    DateTimeOffset CreatedAt,
    DateTimeOffset? LastSeenAt,
    bool CredentialRevoked,
    UpdateNodeRequest? Configuration = null);

/// <summary>一次性节点注册令牌快照。</summary>
public sealed record StoredNodeRegistrationToken(
    Guid Id,
    string TokenHash,
    string? ExpectedName,
    DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt,
    DateTimeOffset? ConsumedAt,
    bool Revoked);

/// <summary>节点长期凭据快照。</summary>
public sealed record StoredNodeCredential(
    Guid Id,
    Guid NodeId,
    string CredentialHash,
    DateTimeOffset CreatedAt,
    DateTimeOffset? RevokedAt,
    int Revision);

/// <summary>节点指标持久化样本。</summary>
public sealed record StoredNodeMetric(Guid NodeId, NodeMetricsDto Metrics);

/// <summary>控制平面保存的 Agent 权威镜像快照。</summary>
public sealed record StoredNodeMirror(
    Guid NodeId,
    IReadOnlyList<AgentInstanceResponse> Instances,
    IReadOnlyList<AgentJavaRuntimeResponse> JavaRuntimes,
    IReadOnlyList<ScheduledTaskDetailsDto> Schedules,
    DateTimeOffset CapturedAt);

/// <summary>节点注册、凭据和在线状态持久化端口。</summary>
public interface INodeControlStore
{
    /// <summary>保存一次性注册令牌。</summary>
    Task AddRegistrationTokenAsync(StoredNodeRegistrationToken token, CancellationToken cancellationToken);
    /// <summary>列出未消费且未作废的注册令牌。</summary>
    Task<IReadOnlyList<StoredNodeRegistrationToken>> ListRegistrationTokensAsync(DateTimeOffset now, CancellationToken cancellationToken);
    /// <summary>作废注册令牌。</summary>
    Task RevokeRegistrationTokenAsync(Guid tokenId, CancellationToken cancellationToken);
    /// <summary>原子消费令牌并创建节点及长期凭据。</summary>
    Task<StoredNode?> TryRegisterAsync(string tokenHash, StoredNode node, StoredNodeCredential credential, DateTimeOffset now, CancellationToken cancellationToken);
    /// <summary>按长期凭据哈希验证节点。</summary>
    Task<StoredNode?> AuthenticateCredentialAsync(string credentialHash, CancellationToken cancellationToken);
    /// <summary>读取节点。</summary>
    Task<StoredNode?> FindNodeAsync(Guid nodeId, CancellationToken cancellationToken);
    /// <summary>列出节点。</summary>
    Task<IReadOnlyList<StoredNode>> ListNodesAsync(CancellationToken cancellationToken);
    /// <summary>更新节点控制平面属性快照。</summary>
    Task UpdateNodeAsync(StoredNode node, CancellationToken cancellationToken);
    /// <summary>从控制平面移除节点及其凭据。</summary>
    Task RemoveNodeAsync(Guid nodeId, CancellationToken cancellationToken);
    /// <summary>更新节点心跳与状态。</summary>
    Task UpdateHeartbeatAsync(Guid nodeId, string status, string agentVersion, DateTimeOffset lastSeenAt, CancellationToken cancellationToken);
    /// <summary>轮换长期凭据并使旧凭据失效。</summary>
    Task RotateCredentialAsync(Guid nodeId, StoredNodeCredential replacement, CancellationToken cancellationToken);
    /// <summary>暂存一个可认证的新凭据，旧凭据仍保持有效。</summary>
    Task StageCredentialAsync(StoredNodeCredential replacement, CancellationToken cancellationToken);
    /// <summary>确认新凭据后吊销该节点的其他凭据。</summary>
    Task ActivateCredentialAsync(Guid nodeId, Guid credentialId, DateTimeOffset revokedAt, CancellationToken cancellationToken);
    /// <summary>撤销一个尚未确认的凭据。</summary>
    Task RevokeCredentialAsync(Guid credentialId, DateTimeOffset revokedAt, CancellationToken cancellationToken);
    /// <summary>读取节点当前最高凭据修订号。</summary>
    Task<int> GetCredentialRevisionAsync(Guid nodeId, CancellationToken cancellationToken);
    /// <summary>吊销节点全部凭据。</summary>
    Task RevokeCredentialsAsync(Guid nodeId, DateTimeOffset revokedAt, CancellationToken cancellationToken);
    /// <summary>保存节点指标并清理超过保留期的旧样本。</summary>
    Task AddMetricAsync(StoredNodeMetric metric, DateTimeOffset retentionCutoff, CancellationToken cancellationToken);
    /// <summary>读取指定节点在时间范围内的指标。</summary>
    Task<IReadOnlyList<StoredNodeMetric>> ListMetricsAsync(Guid nodeId, DateTimeOffset since, CancellationToken cancellationToken);
    /// <summary>原子替换节点的实例、Java 与计划任务镜像。</summary>
    Task ReplaceMirrorAsync(StoredNodeMirror mirror, CancellationToken cancellationToken);
    /// <summary>应用 Agent 自治事件产生的实例状态变化。</summary>
    Task UpdateInstanceMirrorStatusAsync(Guid nodeId, Guid instanceId, string status, DateTimeOffset capturedAt, CancellationToken cancellationToken);
    /// <summary>读取节点镜像；节点标识为空时聚合全部节点。</summary>
    Task<IReadOnlyList<StoredNodeMirror>> ListMirrorsAsync(Guid? nodeId, CancellationToken cancellationToken);
}
