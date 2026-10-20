using System;
using System.Collections.Generic;

namespace Servitium.Contracts.Governance;

/// <summary>仪表盘节点概览。</summary>
public sealed record DashboardNodeDto(
    Guid Id,
    string Name,
    string? PublicAddress,
    double? CpuPercent,
    long? MemoryUsedBytes,
    long? MemoryTotalBytes,
    int InstanceCount,
    string? AgentVersion,
    string ProtocolVersion,
    string Status);

/// <summary>按分钟聚合的请求趋势点。</summary>
public sealed record RequestTrendPointDto(DateTimeOffset At, int RequestCount, int DeniedCount, int InstanceCount);

/// <summary>控制平面仪表盘聚合数据。</summary>
public sealed record DashboardOverviewDto(
    int OnlineNodes,
    int TotalNodes,
    int RunningInstances,
    int TotalInstances,
    int ActiveSessions,
    double ControlPlaneCpuPercent,
    long ControlPlaneMemoryBytes,
    IReadOnlyList<DashboardNodeDto> Nodes,
    IReadOnlyList<RequestTrendPointDto> Trend);
