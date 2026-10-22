using System;
using System.Collections.Generic;

using Servitium.Contracts.Instances;
using Servitium.Contracts.Nodes;
using Servitium.Contracts.Scheduling;

namespace Servitium.Contracts.Agent;

/// <summary>Agent 实例创建模式。</summary>
public enum AgentInstanceCreationMode
{
    /// <summary>创建空受管目录。</summary>
    Empty,
    /// <summary>附加外部绝对路径。</summary>
    AttachExternal,
    /// <summary>从已经传输完成的 ZIP 创建。</summary>
    ServerArchive,
}

/// <summary>Agent 创建实例指令。</summary>
/// <param name="InstanceId">实例标识。</param>
/// <param name="Name">名称。</param>
/// <param name="Mode">创建模式。</param>
/// <param name="ExternalPath">外部绝对路径。</param>
/// <param name="UploadStreamId">已完成文件流标识。</param>
/// <param name="Launch">启动配置。</param>
/// <param name="Tags">标签。</param>
/// <param name="Notes">备注。</param>
/// <param name="InitialConfiguration">创建时应用的完整实例配置。</param>
public sealed record AgentCreateInstanceCommand(
    Guid InstanceId,
    string Name,
    AgentInstanceCreationMode Mode,
    string? ExternalPath,
    string? UploadStreamId,
    StructuredLaunchRequest Launch,
    IReadOnlyList<string> Tags,
    string? Notes,
    UpdateInstanceRequest? InitialConfiguration = null);

/// <summary>Agent 实例标识指令。</summary>
/// <param name="InstanceId">实例标识。</param>
public sealed record AgentInstanceIdCommand(Guid InstanceId);

/// <summary>Agent 手工登记 Java 运行时指令。</summary>
/// <param name="ExecutablePath">java.exe 的绝对路径。</param>
public sealed record AgentRegisterJavaCommand(string ExecutablePath);

/// <summary>Agent 导入 Java 压缩包指令。</summary>
/// <param name="UploadStreamId">已完成文件流标识，由网关写入。</param>
public sealed record AgentImportJavaCommand(string? UploadStreamId);

/// <summary>Agent 注销 Java 运行时指令。</summary>
/// <param name="RuntimeId">运行时标识。</param>
/// <param name="DeleteFiles">是否删除仅由 Servitium 拥有的导入目录。</param>
/// <param name="Force">已展示使用列表并确认仍要取消登记。</param>
public sealed record AgentUnregisterJavaCommand(Guid RuntimeId, bool DeleteFiles = false, bool Force = false);

/// <summary>向运行中实例发送控制台命令。</summary>
/// <param name="InstanceId">实例标识。</param>
/// <param name="Command">不含换行的命令文本。</param>
public sealed record AgentSendInstanceCommand(Guid InstanceId, string Command);

/// <summary>停止实例指令。</summary>
/// <param name="InstanceId">实例标识。</param>
/// <param name="StopCommand">停止命令。</param>
/// <param name="StopTimeoutSeconds">等待正常退出的秒数。</param>
public sealed record AgentStopInstanceCommand(Guid InstanceId, string StopCommand, int StopTimeoutSeconds);

/// <summary>执行实例更新动作指令。</summary>
public sealed record AgentRunInstanceUpdateCommand(Guid InstanceId, bool Confirm);

/// <summary>读取实例日志指令。</summary>
public sealed record AgentReadInstanceLogCommand(
    Guid InstanceId,
    string RelativePath,
    long StartLine,
    int Count,
    string? Search);

/// <summary>导出当前终端会话缓冲。</summary>
/// <param name="InstanceId">实例标识。</param>
/// <param name="IncludeAnsi">是否保留 ANSI 转义序列。</param>
public sealed record AgentExportSessionLogCommand(Guid InstanceId, bool IncludeAnsi);

/// <summary>实例相对路径指令。</summary>
/// <param name="InstanceId">实例标识。</param>
/// <param name="RelativePath">实例根目录内的相对路径。</param>
/// <param name="TaskId">可选文件任务标识。</param>
public sealed record AgentFilePathCommand(Guid InstanceId, string RelativePath, Guid? TaskId = null);

/// <summary>实例两路径文件指令。</summary>
/// <param name="InstanceId">实例标识。</param>
/// <param name="SourcePath">源相对路径。</param>
/// <param name="DestinationPath">目标相对路径。</param>
/// <param name="TaskId">可选文件任务标识。</param>
/// <param name="CollisionPolicy">同名策略。</param>
public sealed record AgentFileTransferCommand(Guid InstanceId, string SourcePath, string DestinationPath, Guid? TaskId = null, string CollisionPolicy = "reject");

/// <summary>实例内重命名指令。</summary>
public sealed record AgentRenameFileCommand(Guid InstanceId, string RelativePath, string NewName);

/// <summary>实例文本原子写入指令。</summary>
/// <param name="InstanceId">实例标识。</param>
/// <param name="RelativePath">相对路径。</param>
/// <param name="Content">文本内容。</param>
/// <param name="Encoding">编码名称。</param>
public sealed record AgentWriteTextCommand(Guid InstanceId, string RelativePath, string Content, string Encoding);

/// <summary>实例文本读取指令。</summary>
public sealed record AgentReadTextCommand(Guid InstanceId, string RelativePath, string? Encoding = null);

/// <summary>Agent 文本文件响应。</summary>
/// <param name="RelativePath">相对路径。</param>
/// <param name="Content">文本内容。</param>
/// <param name="Encoding">编码名称。</param>
/// <param name="LineEnding">行尾模式。</param>
/// <param name="Size">字节数。</param>
public sealed record AgentTextFileResponse(string RelativePath, string Content, string Encoding, string LineEnding, long Size);

/// <summary>Agent 本地实例运行时状态。</summary>
/// <param name="InstanceId">实例标识。</param>
/// <param name="Status">状态。</param>
/// <param name="ProcessId">进程标识。</param>
/// <param name="StartedAt">启动时间。</param>
/// <param name="LastExitCode">最近退出码。</param>
/// <param name="RecentOutput">最近输出。</param>
public sealed record AgentInstanceRuntimeResponse(
    Guid InstanceId,
    string Status,
    int? ProcessId,
    DateTimeOffset? StartedAt,
    int? LastExitCode,
    IReadOnlyList<string> RecentOutput);

/// <summary>Agent 本地 Java 运行时响应。</summary>
/// <param name="Id">运行时标识。</param>
/// <param name="ExecutablePath">java.exe 路径。</param>
/// <param name="Version">版本。</param>
/// <param name="Vendor">厂商。</param>
/// <param name="Architecture">体系结构。</param>
/// <param name="Source">来源。</param>
/// <param name="Imported">是否由 Servitium 导入并拥有文件。</param>
/// <param name="RegisteredAt">登记时间。</param>
/// <param name="VmName">Java VM 名称。</param>
public sealed record AgentJavaRuntimeResponse(
    Guid Id,
    string ExecutablePath,
    string Version,
    string Vendor,
    string Architecture,
    string Source,
    bool Imported,
    DateTimeOffset RegisteredAt,
    string? VmName);

/// <summary>Agent 本地实例响应。</summary>
/// <param name="Id">实例标识。</param>
/// <param name="Name">名称。</param>
/// <param name="WorkingDirectory">工作目录。</param>
/// <param name="OwnedByServitium">是否受管。</param>
/// <param name="Status">状态。</param>
/// <param name="Launch">启动配置。</param>
/// <param name="Tags">标签。</param>
/// <param name="Notes">备注。</param>
/// <param name="Configuration">完整实例配置，不包含秘密。</param>
/// <param name="Revision">本地修订号。</param>
/// <param name="Warnings">非阻断配置警告代码。</param>
/// <param name="StartedAt">最近一次启动时间。</param>
/// <param name="Software">实际启动目标的软件元数据。</param>
public sealed record AgentInstanceResponse(
    Guid Id,
    string Name,
    string WorkingDirectory,
    bool OwnedByServitium,
    string Status,
    StructuredLaunchRequest Launch,
    IReadOnlyList<string> Tags,
    string? Notes,
    UpdateInstanceRequest? Configuration = null,
    long Revision = 0,
    IReadOnlyList<string>? Warnings = null,
    DateTimeOffset? StartedAt = null,
    InstanceSoftwareDto? Software = null);

/// <summary>Agent 用于控制平面对账的完整本地快照。</summary>
/// <param name="Instances">实例登记。</param>
/// <param name="JavaRuntimes">Java 运行时登记。</param>
/// <param name="Schedules">计划任务定义。</param>
/// <param name="CapturedAt">快照采集时间。</param>
public sealed record AgentReconcileResponse(
    IReadOnlyList<AgentInstanceResponse> Instances,
    IReadOnlyList<AgentJavaRuntimeResponse> JavaRuntimes,
    IReadOnlyList<ScheduledTaskDetailsDto> Schedules,
    DateTimeOffset CapturedAt);

/// <summary>Agent 实例配置更新指令。</summary>
/// <param name="InstanceId">实例标识。</param>
/// <param name="Request">配置。</param>
public sealed record AgentUpdateInstanceCommand(Guid InstanceId, UpdateInstanceRequest Request);

/// <summary>完成控制平面到 Agent 的文件上传。</summary>
/// <param name="InstanceId">实例标识。</param>
/// <param name="DestinationPath">目标相对路径。</param>
/// <param name="UploadStreamId">流标识，由网关写入。</param>
/// <param name="Overwrite">是否覆盖。</param>
public sealed record AgentCompleteFileUploadCommand(Guid InstanceId, string DestinationPath, string? UploadStreamId, bool Overwrite);

/// <summary>将一个已校验的上传分块持久化到 Agent。</summary>
public sealed record AgentStoreFileUploadPartCommand(
    Guid InstanceId,
    Guid UploadId,
    int PartIndex,
    int PartCount,
    long PartSizeBytes,
    long TotalBytes,
    string Sha256,
    DateTimeOffset ExpiresAt,
    string? UploadStreamId = null);

/// <summary>在 Agent 合并并提交持久化的上传分块。</summary>
public sealed record AgentCompleteChunkedFileUploadCommand(
    Guid InstanceId,
    Guid UploadId,
    string DestinationPath,
    int PartCount,
    long TotalBytes,
    string Sha256,
    bool Overwrite);

/// <summary>取消 Agent 上的分块上传。</summary>
public sealed record AgentCancelFileUploadCommand(Guid InstanceId, Guid UploadId);

/// <summary>完成分块上传并将 JAR 提交到模组或插件目录。</summary>
public sealed record AgentCompleteModUploadCommand(
    Guid InstanceId,
    Guid UploadId,
    string Kind,
    string FileName,
    int PartCount,
    long TotalBytes,
    string Sha256,
    bool Overwrite);

/// <summary>Agent 批量路径指令。</summary>
/// <param name="InstanceId">实例标识。</param>
/// <param name="RelativePaths">相对路径。</param>
/// <param name="TaskId">可选文件任务标识。</param>
public sealed record AgentFileBatchCommand(Guid InstanceId, IReadOnlyList<string> RelativePaths, Guid? TaskId = null);

/// <summary>取消 Agent 本地文件任务。</summary>
public sealed record AgentCancelFileTaskCommand(Guid InstanceId, Guid TaskId);

/// <summary>分页列出实例目录。</summary>
public sealed record AgentListDirectoryCommand(
    Guid InstanceId,
    string RelativePath,
    string? Cursor = null,
    int PageSize = 100,
    string? Search = null,
    string Sort = "name",
    bool Descending = false);

/// <summary>打开 Agent 到控制平面的文件下载子流。</summary>
public sealed record AgentOpenFileDownloadRequest(Guid InstanceId, string RelativePath, long Offset, long Length);

/// <summary>Server 到 Agent 入站文件子流的预检元数据。</summary>
/// <param name="ResourceName">仅用于诊断的资源名，不作为文件路径使用。</param>
/// <param name="Length">声明的完整字节数。</param>
public sealed record AgentInboundTransferDescriptor(string ResourceName, long Length);

/// <summary>从停止中的实例采集 HMCL Release 内容。</summary>
/// <param name="InstanceId">实例标识。</param>
/// <param name="StreamId">Agent 到控制平面的 content-collect 子流。</param>
public sealed record AgentCollectDirectoryForReleaseCommand(Guid InstanceId, string StreamId);

/// <summary>实例目录采集结果。</summary>
public sealed record AgentCollectedDirectoryResult(
    Guid InstanceId,
    string StreamId,
    int FileCount,
    long ArchiveBytes,
    IReadOnlyDictionary<string, string> ExcludedPaths);

/// <summary>把已通过 content-sync 子流接收的 Release 同步到停止中的实例。</summary>
/// <param name="InstanceId">实例标识。</param>
/// <param name="ReleaseId">目标 Release。</param>
/// <param name="StreamId">已完成的 content-sync 子流。</param>
/// <param name="Paths">新 Release 管理的完整路径集合。</param>
public sealed record AgentSyncFromReleaseCommand(Guid InstanceId, Guid ReleaseId, string StreamId, IReadOnlyList<string> Paths);

/// <summary>实例同步结果。</summary>
public sealed record AgentSyncFromReleaseResult(Guid InstanceId, Guid ReleaseId, int Added, int Replaced, int Deleted);

/// <summary>Agent 压缩指令。</summary>
/// <param name="InstanceId">实例标识。</param>
/// <param name="RelativePaths">输入路径。</param>
/// <param name="DestinationPath">目标 ZIP。</param>
/// <param name="EntryNameEncoding">ZIP 条目名编码。</param>
/// <param name="TaskId">可选归档任务标识。</param>
public sealed record AgentCompressCommand(Guid InstanceId, IReadOnlyList<string> RelativePaths, string DestinationPath, string EntryNameEncoding = "utf-8", Guid? TaskId = null);

/// <summary>Agent 解压指令。</summary>
/// <param name="InstanceId">实例标识。</param>
/// <param name="ArchivePath">归档路径。</param>
/// <param name="DestinationPath">目标目录。</param>
/// <param name="EntryNameEncoding">条目名编码。</param>
/// <param name="TaskId">可选归档任务标识。</param>
public sealed record AgentExtractCommand(Guid InstanceId, string ArchivePath, string DestinationPath, string EntryNameEncoding, Guid? TaskId = null);

/// <summary>Agent URL 下载指令。</summary>
/// <param name="InstanceId">实例标识。</param>
/// <param name="Url">URL。</param>
/// <param name="DestinationPath">目标路径。</param>
/// <param name="Overwrite">是否覆盖。</param>
/// <param name="TaskId">可选 URL 下载任务标识。</param>
public sealed record AgentDownloadFromUrlCommand(Guid InstanceId, Uri Url, string DestinationPath, bool Overwrite, Guid? TaskId = null);

/// <summary>Agent 配置写入指令。</summary>
/// <param name="InstanceId">实例标识。</param>
/// <param name="ConfigKey">配置键。</param>
/// <param name="Request">写入请求。</param>
public sealed record AgentWriteConfigurationCommand(Guid InstanceId, string ConfigKey, Configurations.WriteConfigurationRequest Request);

/// <summary>Agent 配置备份指令。</summary>
/// <param name="InstanceId">实例标识。</param>
/// <param name="ConfigKey">配置键。</param>
/// <param name="BackupId">备份标识。</param>
public sealed record AgentConfigurationBackupCommand(Guid InstanceId, string ConfigKey, Guid BackupId);

/// <summary>Agent 模组指令。</summary>
/// <param name="InstanceId">实例标识。</param>
/// <param name="ModId">稳定模组标识。</param>
/// <param name="Kind">`mods` 或 `plugins`。</param>
public sealed record AgentModCommand(Guid InstanceId, string ModId, string Kind);

/// <summary>Agent 模组启用状态指令。</summary>
/// <param name="InstanceId">实例标识。</param>
/// <param name="ModId">稳定模组标识。</param>
/// <param name="Kind">`mods` 或 `plugins`。</param>
/// <param name="Enabled">是否启用。</param>
public sealed record AgentModToggleCommand(Guid InstanceId, string ModId, string Kind, bool Enabled);

/// <summary>Agent 计划任务指令。</summary>
/// <param name="InstanceId">实例标识。</param>
/// <param name="TaskId">任务标识。</param>
public sealed record AgentScheduleIdCommand(Guid InstanceId, Guid TaskId);

/// <summary>Agent 计划任务写入指令。</summary>
/// <param name="InstanceId">实例标识。</param>
/// <param name="TaskId">任务标识。</param>
/// <param name="Request">任务定义。</param>
public sealed record AgentUpsertScheduleCommand(Guid InstanceId, Guid TaskId, UpsertScheduledTaskRequest Request);

/// <summary>Agent 计划任务启用状态指令。</summary>
/// <param name="InstanceId">实例标识。</param>
/// <param name="TaskId">任务标识。</param>
/// <param name="Enabled">是否启用。</param>
public sealed record AgentSetScheduleEnabledCommand(Guid InstanceId, Guid TaskId, bool Enabled);

/// <summary>Agent 长期凭据轮换指令。</summary>
/// <param name="Credential">新凭据明文，仅在受认证的节点流上传输一次。</param>
/// <param name="Revision">单调递增修订号。</param>
public sealed record AgentRotateCredentialCommand(string Credential, int Revision);

/// <summary>Agent 节点级配置更新指令。</summary>
/// <param name="Configuration">节点配置。</param>
public sealed record AgentUpdateNodeConfigurationCommand(UpdateNodeRequest Configuration);
