using System;
using System.Collections.Generic;

namespace Servitium.Contracts.Instances;

/// <summary>实例摘要。</summary>
/// <param name="Id">实例标识。</param>
/// <param name="NodeId">节点标识。</param>
/// <param name="Name">名称。</param>
/// <param name="Status">状态。</param>
/// <param name="WorkingDirectory">工作目录。</param>
/// <param name="OwnedByServitium">是否由 Servitium 拥有。</param>
/// <param name="Tags">标签。</param>
/// <param name="NodeName">节点显示名。</param>
/// <param name="JavaRuntimeId">Java 运行时标识。</param>
/// <param name="PrimaryPort">主要监听端口。</param>
/// <param name="OnlinePlayers">最近一次采样的在线玩家数。</param>
/// <param name="LastStartedAt">最近启动时间。</param>
/// <param name="CpuPercent">最近一次采样的 CPU 百分比。</param>
/// <param name="MemoryBytes">最近一次采样的内存字节数。</param>
public sealed record InstanceDto(
    Guid Id,
    Guid NodeId,
    string Name,
    string Status,
    string WorkingDirectory,
    bool OwnedByServitium,
    IReadOnlyList<string> Tags,
    string? NodeName = null,
    Guid? JavaRuntimeId = null,
    int? PrimaryPort = null,
    int? OnlinePlayers = null,
    DateTimeOffset? LastStartedAt = null,
    double? CpuPercent = null,
    long? MemoryBytes = null);

/// <summary>实例详情及其启动与运行配置。</summary>
public sealed record InstanceDetailsDto(
    InstanceDto Instance,
    StructuredLaunchRequest Launch,
    string? Notes,
    UpdateInstanceRequest? Configuration,
    long Revision);

/// <summary>按节点分组的实例集合。</summary>
public sealed record InstanceGroupDto(
    Nodes.NodeDto Node,
    IReadOnlyList<InstanceDto> Items,
    string? NextCursor);

/// <summary>实例分组响应。</summary>
public sealed record InstanceGroupsDto(IReadOnlyList<InstanceGroupDto> Groups);

/// <summary>结构化启动配置请求。</summary>
/// <param name="JavaRuntimeId">Java 运行时标识。</param>
/// <param name="MinimumHeapMegabytes">最小堆。</param>
/// <param name="MaximumHeapMegabytes">最大堆。</param>
/// <param name="JvmArguments">额外 JVM 参数。</param>
/// <param name="TargetKind">`jar` 或 `argfile`。</param>
/// <param name="Target">目标相对路径。</param>
/// <param name="ServerArguments">服务端参数。</param>
/// <param name="EnvironmentVariables">实例环境变量覆盖。</param>
/// <param name="AdvancedMode">是否启用受限制的高级参数数组。</param>
/// <param name="AdvancedArguments">高级模式下直接传给已登记 Java 的参数数组。</param>
public sealed record StructuredLaunchRequest(
    Guid JavaRuntimeId,
    int MinimumHeapMegabytes,
    int MaximumHeapMegabytes,
    IReadOnlyList<string> JvmArguments,
    string TargetKind,
    string Target,
    IReadOnlyList<string> ServerArguments,
    IReadOnlyDictionary<string, string>? EnvironmentVariables = null,
    bool AdvancedMode = false,
    IReadOnlyList<string>? AdvancedArguments = null);

/// <summary>向实例控制台发送命令。</summary>
/// <param name="Command">不含换行的命令文本。</param>
public sealed record SendInstanceCommandRequest(string Command);

/// <summary>实例运行时状态。</summary>
/// <param name="InstanceId">实例标识。</param>
/// <param name="NodeId">节点标识。</param>
/// <param name="Status">状态。</param>
/// <param name="ProcessId">进程标识。</param>
/// <param name="StartedAt">启动时间。</param>
/// <param name="LastExitCode">最近退出码。</param>
/// <param name="RecentOutput">最近输出。</param>
public sealed record InstanceRuntimeDto(
    Guid InstanceId,
    Guid NodeId,
    string Status,
    int? ProcessId,
    DateTimeOffset? StartedAt,
    int? LastExitCode,
    IReadOnlyList<string> RecentOutput);

/// <summary>创建空实例请求。</summary>
/// <param name="Name">名称。</param>
/// <param name="NodeId">节点。</param>
/// <param name="Launch">启动配置。</param>
/// <param name="StopCommand">停止命令。</param>
/// <param name="StopTimeoutSeconds">停止超时。</param>
/// <param name="Tags">标签。</param>
/// <param name="Notes">备注。</param>
public sealed record CreateInstanceRequest(string Name, Guid NodeId, StructuredLaunchRequest Launch, string StopCommand, int StopTimeoutSeconds, IReadOnlyList<string> Tags, string? Notes);

/// <summary>附加任意绝对路径请求。</summary>
/// <param name="Name">实例名称。</param>
/// <param name="NodeId">节点标识。</param>
/// <param name="AbsolutePath">绝对工作目录。</param>
/// <param name="Launch">启动配置。</param>
/// <param name="StopCommand">停止命令。</param>
/// <param name="StopTimeoutSeconds">停止超时。</param>
/// <param name="Tags">标签。</param>
/// <param name="Notes">备注。</param>
public sealed record AttachInstanceDirectoryRequest(
    string Name,
    Guid NodeId,
    string AbsolutePath,
    StructuredLaunchRequest Launch,
    string StopCommand = "stop",
    int StopTimeoutSeconds = 60,
    IReadOnlyList<string>? Tags = null,
    string? Notes = null);

/// <summary>上传服务端 ZIP 后创建实例的请求。</summary>
/// <param name="UploadId">上传标识。</param>
/// <param name="Name">实例名称。</param>
/// <param name="NodeId">节点标识。</param>
/// <param name="Launch">启动配置。</param>
/// <param name="StopCommand">停止命令。</param>
/// <param name="StopTimeoutSeconds">停止超时。</param>
/// <param name="Tags">标签。</param>
/// <param name="Notes">备注。</param>
public sealed record ImportServerArchiveRequest(
    Guid UploadId,
    string Name,
    Guid NodeId,
    StructuredLaunchRequest Launch,
    string StopCommand = "stop",
    int StopTimeoutSeconds = 60,
    IReadOnlyList<string>? Tags = null,
    string? Notes = null);

/// <summary>Prism 原生实例 ZIP 预览请求。</summary>
/// <param name="UploadId">上传标识。</param>
public sealed record PreviewPrismInstanceRequest(Guid UploadId);

/// <summary>Prism 转换请求。</summary>
/// <param name="UploadId">上传标识。</param>
/// <param name="ChannelId">目标 Channel。</param>
/// <param name="ReleaseName">Release 名称。</param>
/// <param name="ReleaseVersion">Release 版本。</param>
public sealed record ConvertPrismInstanceRequest(Guid UploadId, Guid ChannelId, string ReleaseName, string ReleaseVersion);

/// <summary>Prism ZIP 预览。</summary>
/// <param name="InstanceName">实例名称。</param>
/// <param name="GameVersion">游戏版本。</param>
/// <param name="Loader">加载器。</param>
/// <param name="IncludedPaths">候选路径。</param>
/// <param name="ExcludedPaths">被过滤路径及原因。</param>
public sealed record PrismInstancePreviewDto(string InstanceName, string GameVersion, string? Loader, IReadOnlyList<string> IncludedPaths, IReadOnlyDictionary<string, string> ExcludedPaths);

/// <summary>已暂存 Prism ZIP 的安全预览。</summary>
/// <param name="UploadId">隔离暂存上传标识。</param>
/// <param name="Preview">只读转换计划。</param>
/// <param name="Difference">相对目标 Channel 当前 Release 的计划差异；未指定 Channel 时为空。</param>
public sealed record StagedPrismPreviewDto(
    Guid UploadId,
    PrismInstancePreviewDto Preview,
    Distribution.HmclPublishDiffDto? Difference = null);

/// <summary>把已预览 Prism ZIP 转换为 Release。</summary>
/// <param name="ChannelId">目标 Channel。</param>
/// <param name="Metadata">经用户确认的 Release 元数据。</param>
public sealed record ConvertStagedPrismRequest(
    Guid ChannelId,
    Distribution.CreateHmclReleaseMetadataRequest Metadata);

/// <summary>修改实例配置。</summary>
/// <param name="Name">名称。</param>
/// <param name="Tags">标签。</param>
/// <param name="Notes">备注。</param>
/// <param name="Launch">结构化启动配置。</param>
/// <param name="StopCommand">停止命令。</param>
/// <param name="StopTimeoutSeconds">停止等待秒数。</param>
/// <param name="AutoStart">Agent 启动时自动启动。</param>
/// <param name="AutoRestart">崩溃后自动重启。</param>
/// <param name="MaximumRestartAttempts">最大连续重启次数。</param>
/// <param name="Rcon">RCON 设置。</param>
/// <param name="Query">Server List Ping 设置。</param>
/// <param name="WorkingDirectory">停止时可修改的绝对工作目录；空值表示保持不变。</param>
/// <param name="PrimaryPort">主要监听端口。</param>
/// <param name="AutoStartDelaySeconds">Agent 启动后的自动启动延迟。</param>
/// <param name="RestartIntervalSeconds">自动重启间隔。</param>
/// <param name="RestartResetSeconds">连续稳定运行后重置重启计数的秒数。</param>
/// <param name="RunCredentialAlias">Agent 本地运行凭据别名。</param>
/// <param name="Terminal">终端设置。</param>
/// <param name="UpdateAction">更新动作。</param>
/// <param name="ShortcutCommands">快捷命令。</param>
public sealed record UpdateInstanceRequest(
    string Name,
    IReadOnlyList<string> Tags,
    string? Notes,
    StructuredLaunchRequest Launch,
    string StopCommand,
    int StopTimeoutSeconds,
    bool AutoStart,
    bool AutoRestart,
    int MaximumRestartAttempts,
    RconSettingsRequest Rcon,
    QuerySettingsRequest Query,
    string? WorkingDirectory = null,
    int? PrimaryPort = null,
    int AutoStartDelaySeconds = 0,
    int RestartIntervalSeconds = 5,
    int RestartResetSeconds = 600,
    string? RunCredentialAlias = null,
    TerminalSettingsRequest? Terminal = null,
    UpdateActionRequest? UpdateAction = null,
    IReadOnlyList<ShortcutCommandRequest>? ShortcutCommands = null);

/// <summary>实例终端设置。</summary>
/// <param name="UseConPty">是否使用 ConPTY；关闭时回退到重定向管道。</param>
/// <param name="Columns">初始列数。</param>
/// <param name="Rows">初始行数。</param>
/// <param name="InputEncoding">输入编码。</param>
/// <param name="OutputEncoding">输出编码。</param>
/// <param name="NewLine">`LF` 或 `CRLF`。</param>
/// <param name="AnsiEnabled">客户端是否解释 ANSI 样式。</param>
public sealed record TerminalSettingsRequest(
    bool UseConPty = true,
    int Columns = 164,
    int Rows = 40,
    string InputEncoding = "UTF-8",
    string OutputEncoding = "UTF-8",
    string NewLine = "CRLF",
    bool AnsiEnabled = true);

/// <summary>实例更新动作。</summary>
/// <param name="Kind">`none`、`jar-replace` 或 `hmcl-channel-sync`。</param>
/// <param name="SourceRelativePath">替换源相对路径。</param>
/// <param name="TargetRelativePath">被替换目标相对路径。</param>
/// <param name="ChannelId">关联的 HMCL Channel。</param>
public sealed record UpdateActionRequest(
    string Kind = "none",
    string? SourceRelativePath = null,
    string? TargetRelativePath = null,
    Guid? ChannelId = null);

/// <summary>命名快捷命令。</summary>
/// <param name="Name">显示名称。</param>
/// <param name="Command">单行命令。</param>
public sealed record ShortcutCommandRequest(string Name, string Command);

/// <summary>执行已配置更新动作。</summary>
/// <param name="Confirm">确认在停止状态执行并创建备份。</param>
public sealed record RunInstanceUpdateRequest(bool Confirm);

/// <summary>RCON 设置。</summary>
/// <param name="Enabled">是否启用。</param>
/// <param name="Port">端口。</param>
/// <param name="Password">写入时的新密码；读取时恒为空。</param>
public sealed record RconSettingsRequest(bool Enabled, int Port, string? Password);

/// <summary>Server List Ping 设置。</summary>
/// <param name="Enabled">是否启用。</param>
/// <param name="Host">探测主机。</param>
/// <param name="Port">端口。</param>
public sealed record QuerySettingsRequest(bool Enabled, string Host, int Port);

/// <summary>玩家列表。</summary>
/// <param name="Online">在线人数。</param>
/// <param name="Maximum">人数上限。</param>
/// <param name="Players">可见玩家名。</param>
/// <param name="Source">数据来源。</param>
/// <param name="Version">服务端报告的版本。</param>
/// <param name="Motd">服务端 MOTD 的纯文本表示。</param>
public sealed record PlayerListDto(
    int Online,
    int Maximum,
    IReadOnlyList<string> Players,
    string Source,
    string? Version = null,
    string? Motd = null);

/// <summary>RCON 命令请求。</summary>
/// <param name="Command">单行命令。</param>
public sealed record RconCommandRequest(string Command);

/// <summary>RCON 命令结果。</summary>
/// <param name="Output">服务端响应。</param>
public sealed record RconCommandResultDto(string Output);

/// <summary>实例资源指标。</summary>
/// <param name="SampledAt">采样时间。</param>
/// <param name="CpuPercent">进程树 CPU 百分比。</param>
/// <param name="MemoryBytes">进程树工作集。</param>
/// <param name="OnlinePlayers">在线玩家数。</param>
/// <param name="HandleCount">进程树句柄数。</param>
/// <param name="ThreadCount">进程树线程数。</param>
public sealed record InstanceMetricsDto(
    DateTimeOffset SampledAt,
    double CpuPercent,
    long MemoryBytes,
    int? OnlinePlayers,
    int HandleCount = 0,
    int ThreadCount = 0);

/// <summary>实例更新动作结果。</summary>
public sealed record InstanceUpdateResultDto(string Kind, string? BackupRelativePath, DateTimeOffset CompletedAt);

/// <summary>实例批量动作请求。</summary>
/// <param name="Items">节点与实例组合。</param>
public sealed record BatchInstanceActionRequest(IReadOnlyList<InstanceActionTarget> Items);

/// <summary>单个批量实例动作结果。</summary>
public sealed record BatchInstanceActionResultItem(
    Guid NodeId,
    Guid InstanceId,
    bool Succeeded,
    string? ErrorCode);

/// <summary>批量实例动作结果。</summary>
public sealed record BatchInstanceActionResult(IReadOnlyList<BatchInstanceActionResultItem> Items);

/// <summary>批量动作目标。</summary>
/// <param name="NodeId">节点标识。</param>
/// <param name="InstanceId">实例标识。</param>
public sealed record InstanceActionTarget(Guid NodeId, Guid InstanceId);

/// <summary>实例日志查询结果。</summary>
/// <param name="File">相对日志文件。</param>
/// <param name="StartLine">首行编号。</param>
/// <param name="Lines">日志行。</param>
/// <param name="Truncated">结果是否截断。</param>
public sealed record InstanceLogDto(string File, long StartLine, IReadOnlyList<string> Lines, bool Truncated);

/// <summary>当前终端会话日志导出。</summary>
/// <param name="FileName">建议文件名。</param>
/// <param name="Content">UTF-8 文本内容。</param>
/// <param name="AnsiIncluded">是否保留 ANSI 转义序列。</param>
/// <param name="Truncated">会话缓冲是否已截断。</param>
public sealed record SessionLogExportDto(string FileName, string Content, bool AnsiIncluded, bool Truncated);

/// <summary>终端附加请求。</summary>
public sealed record TerminalAttachRequest(Guid InstanceId, int Columns = 164, int Rows = 40);

/// <summary>终端附加结果。</summary>
public sealed record TerminalAttachDto(Guid InstanceId, int Observers, int Columns, int Rows);

/// <summary>终端输入或尺寸控制帧。</summary>
public sealed record TerminalInputFrame(string Type, string? Text = null, int? Columns = null, int? Rows = null);

/// <summary>终端输出帧。</summary>
public sealed record TerminalOutputFrame(Guid InstanceId, long Sequence, string Text, bool Snapshot = false, bool Truncated = false);
