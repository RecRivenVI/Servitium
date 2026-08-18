using System;

using System.Collections.Generic;

namespace Servitium.Contracts.Nodes;

/// <summary>节点摘要。</summary>
/// <param name="Id">节点标识。</param>
/// <param name="Name">显示名。</param>
/// <param name="Status">状态。</param>
/// <param name="AgentVersion">Agent 版本。</param>
/// <param name="LastSeenAt">最近心跳时间。</param>
/// <param name="Notes">节点备注。</param>
/// <param name="OperatingSystem">操作系统。</param>
/// <param name="Architecture">处理器架构。</param>
/// <param name="ProtocolMajor">协议主版本。</param>
/// <param name="ProtocolMinor">协议次版本。</param>
/// <param name="InstanceCount">实例总数。</param>
/// <param name="RunningInstanceCount">运行中实例数。</param>
/// <param name="PublicAddress">玩家可用的对外地址。</param>
public sealed record NodeDto(
    Guid Id,
    string Name,
    string Status,
    string? AgentVersion,
    DateTimeOffset? LastSeenAt,
    string? Notes = null,
    string? OperatingSystem = null,
    string? Architecture = null,
    uint ProtocolMajor = 0,
    uint ProtocolMinor = 0,
    int InstanceCount = 0,
    int RunningInstanceCount = 0,
    string? PublicAddress = null);

/// <summary>节点详情及其当前 Agent 配置镜像。</summary>
public sealed record NodeDetailsDto(
    Guid Id,
    string Name,
    string Status,
    string? AgentVersion,
    int ProtocolMajor,
    int ProtocolMinor,
    string OperatingSystem,
    string Architecture,
    DateTimeOffset CreatedAt,
    DateTimeOffset? LastSeenAt,
    UpdateNodeRequest Configuration);

/// <summary>节点注册令牌响应。</summary>
/// <param name="Token">仅返回一次的明文令牌。</param>
/// <param name="ExpiresAt">过期时间。</param>
public sealed record NodeRegistrationTokenResponse(string Token, DateTimeOffset ExpiresAt);

/// <summary>不包含明文的节点注册令牌摘要。</summary>
public sealed record NodeRegistrationTokenDto(
    Guid Id,
    string? ExpectedName,
    DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt);

/// <summary>创建节点注册令牌请求。</summary>
/// <param name="ExpectedName">可选预期节点名。</param>
/// <param name="LifetimeMinutes">有效分钟数，默认 30。</param>
public sealed record CreateNodeRegistrationTokenRequest(string? ExpectedName, int LifetimeMinutes = 30);

/// <summary>节点对外地址映射。</summary>
/// <param name="InternalAddress">实例内部监听地址。</param>
/// <param name="PublicAddress">向玩家展示的地址。</param>
public sealed record NodeAddressMappingRequest(string InternalAddress, string PublicAddress);

/// <summary>节点配置更新。</summary>
/// <param name="Name">显示名。</param>
/// <param name="DefaultInstancePath">默认实例绝对基路径。</param>
/// <param name="PublicAddress">可选对外地址。</param>
/// <param name="TransferBytesPerSecond">兼容旧客户端的统一传输速率上限。</param>
/// <param name="Notes">节点备注。</param>
/// <param name="Tags">节点标签。</param>
/// <param name="AddressMappings">对外地址映射。</param>
/// <param name="JavaDataDirectory">Java 导入数据目录。</param>
/// <param name="UploadBytesPerSecond">上传速率上限，0 表示不限。</param>
/// <param name="DownloadBytesPerSecond">下载速率上限，0 表示不限。</param>
/// <param name="UrlDownloadConcurrency">URL 下载并发上限。</param>
/// <param name="FileTaskConcurrency">文件任务并发上限。</param>
/// <param name="CompressionTaskConcurrency">压缩任务并发上限。</param>
/// <param name="TerminalBufferBytes">终端缓冲字节上限。</param>
/// <param name="TerminalBufferLines">终端缓冲行数上限。</param>
/// <param name="GracefulShutdownEnabled">服务停止时是否优雅停止实例。</param>
/// <param name="ShutdownWaitSeconds">关机等待秒数。</param>
/// <param name="MetricIntervalSeconds">节点指标采样间隔。</param>
/// <param name="MaximumSchedulesPerInstance">每实例计划任务上限。</param>
/// <param name="AdvancedRawCommandsEnabled">控制平面全局开关的 Agent 防御性镜像。</param>
/// <param name="PrivateSourceAuthorities">允许解析到私网地址的精确主机名与端口。</param>
public sealed record UpdateNodeRequest(
    string Name,
    string? DefaultInstancePath,
    string? PublicAddress,
    long? TransferBytesPerSecond,
    string? Notes = null,
    IReadOnlyList<string>? Tags = null,
    IReadOnlyList<NodeAddressMappingRequest>? AddressMappings = null,
    string? JavaDataDirectory = null,
    long UploadBytesPerSecond = 0,
    long DownloadBytesPerSecond = 0,
    int UrlDownloadConcurrency = 3,
    int FileTaskConcurrency = 4,
    int CompressionTaskConcurrency = 2,
    long TerminalBufferBytes = 1_048_576,
    int TerminalBufferLines = 5_000,
    bool GracefulShutdownEnabled = true,
    int ShutdownWaitSeconds = 60,
    int MetricIntervalSeconds = 5,
    int MaximumSchedulesPerInstance = 32,
    bool AdvancedRawCommandsEnabled = false,
    IReadOnlyList<string>? PrivateSourceAuthorities = null);

/// <summary>节点指标样本。</summary>
/// <param name="SampledAt">采样时间。</param>
/// <param name="CpuPercent">CPU 百分比。</param>
/// <param name="MemoryUsedBytes">已用内存。</param>
/// <param name="MemoryTotalBytes">总内存。</param>
/// <param name="DiskFreeBytes">实例卷剩余空间。</param>
/// <param name="NetworkReceiveBytesPerSecond">接收速率。</param>
/// <param name="NetworkSendBytesPerSecond">发送速率。</param>
/// <param name="Uptime">操作系统运行时长。</param>
public sealed record NodeMetricsDto(
    DateTimeOffset SampledAt,
    double CpuPercent,
    long MemoryUsedBytes,
    long MemoryTotalBytes,
    long DiskFreeBytes,
    long NetworkReceiveBytesPerSecond,
    long NetworkSendBytesPerSecond,
    TimeSpan Uptime);

/// <summary>节点凭据轮换结果。</summary>
/// <param name="Revision">新凭据修订号。</param>
/// <param name="RotatedAt">轮换时间。</param>
public sealed record NodeCredentialRotationDto(int Revision, DateTimeOffset RotatedAt);
