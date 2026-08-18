using System;
using System.Collections.Generic;

namespace Servitium.Contracts.Distribution;

/// <summary>HMCL Channel。</summary>
/// <param name="Id">Channel 标识。</param>
/// <param name="Slug">稳定 slug。</param>
/// <param name="Name">显示名。</param>
/// <param name="Visibility">可见性。</param>
/// <param name="ActiveReleaseId">生效 Release。</param>
/// <param name="Token">仅创建私有 Channel 时返回一次的明文令牌。</param>
/// <param name="ManifestUrl">创建时返回的完整安装链接。</param>
public sealed record HmclChannelDto(
    Guid Id,
    string Slug,
    string Name,
    string Visibility,
    Guid? ActiveReleaseId,
    string? Token = null,
    Uri? ManifestUrl = null);

/// <summary>HMCL Release 文件。</summary>
/// <param name="Path">相对路径。</param>
/// <param name="Sha1">SHA-1。</param>
/// <param name="Size">大小。</param>
public sealed record HmclReleaseFileDto(string Path, string Sha1, long Size);

/// <summary>HMCL Release。</summary>
/// <param name="Id">Release 标识。</param>
/// <param name="ChannelId">Channel 标识。</param>
/// <param name="Sequence">序号。</param>
/// <param name="Name">名称。</param>
/// <param name="Version">版本。</param>
/// <param name="GameVersion">游戏版本。</param>
/// <param name="Files">文件清单。</param>
/// <param name="CreatedAt">创建时间。</param>
/// <param name="Activations">该 Release 曾作为 Channel 当前版本生效的 UTC 时间段。</param>
public sealed record HmclReleaseDto(
    Guid Id,
    Guid ChannelId,
    long Sequence,
    string Name,
    string Version,
    string GameVersion,
    IReadOnlyList<HmclReleaseFileDto> Files,
    DateTimeOffset CreatedAt,
    IReadOnlyList<HmclReleaseActivationDto>? Activations = null);

/// <summary>Release 在所属 Channel 中的一段实际生效区间。</summary>
/// <param name="StartedAt">开始生效的 UTC 时间。</param>
/// <param name="EndedAt">结束生效的 UTC 时间；空值表示当前仍生效。</param>
public sealed record HmclReleaseActivationDto(DateTimeOffset StartedAt, DateTimeOffset? EndedAt);

/// <summary>创建 Channel 请求。</summary>
/// <param name="Slug">稳定 slug。</param>
/// <param name="Name">显示名。</param>
/// <param name="Visibility">可见性。</param>
public sealed record CreateHmclChannelRequest(string Slug, string Name, string Visibility);

/// <summary>发布 Channel 请求。</summary>
/// <param name="ReleaseId">目标 Release。</param>
public sealed record PublishHmclChannelRequest(Guid ReleaseId);

/// <summary>创建不可变 Release 的元数据。</summary>
/// <param name="Name">名称。</param>
/// <param name="Author">作者。</param>
/// <param name="Version">展示版本。</param>
/// <param name="Description">描述。</param>
/// <param name="GameVersion">Minecraft 版本。</param>
/// <param name="LoaderId">可选加载器标识。</param>
/// <param name="LoaderVersion">可选加载器版本。</param>
/// <param name="OptiFineVersion">可选 OptiFine 版本。</param>
public sealed record CreateHmclReleaseMetadataRequest(
    string Name,
    string Author,
    string Version,
    string Description,
    string GameVersion,
    string? LoaderId,
    string? LoaderVersion,
    string? OptiFineVersion);

/// <summary>从已关联或显式指定的停止中实例采集并创建 Release。</summary>
/// <param name="InstanceId">实例标识。</param>
/// <param name="Metadata">不可变 Release 元数据。</param>
public sealed record CreateHmclReleaseFromInstanceRequest(Guid InstanceId, CreateHmclReleaseMetadataRequest Metadata);

/// <summary>把 Release 内容同步到停止中的实例。</summary>
/// <param name="InstanceId">目标实例。</param>
public sealed record SyncHmclReleaseToInstanceRequest(Guid InstanceId);

/// <summary>创建可续传的 HMCL Archive 上传会话。</summary>
public sealed record CreateHmclArchiveRequest(string FileName, long TotalBytes, int PartSizeBytes);

/// <summary>HMCL Archive 上传会话。</summary>
public sealed record HmclArchiveDto(
    Guid Id,
    string FileName,
    long TotalBytes,
    int PartSizeBytes,
    int PartCount,
    IReadOnlyList<int> ReceivedParts,
    string Status,
    DateTimeOffset ExpiresAt);

/// <summary>完成 Archive 上传。</summary>
public sealed record CompleteHmclArchiveRequest(string Sha256);

/// <summary>从已经完成的 Archive 创建 Release。</summary>
public sealed record CreateHmclReleaseFromArchiveRequest(Guid ArchiveId, CreateHmclReleaseMetadataRequest Metadata);

/// <summary>Release 归档处理结果。</summary>
/// <param name="Release">创建的不可变 Release。</param>
/// <param name="ExcludedPaths">因固定规则被剔除的路径与原因。</param>
public sealed record HmclReleaseCreationResult(
    HmclReleaseDto Release,
    IReadOnlyDictionary<string, string> ExcludedPaths);

/// <summary>发布差异。</summary>
/// <param name="Added">将新增的路径。</param>
/// <param name="Replaced">将覆盖的路径。</param>
/// <param name="Deleted">将删除的路径。</param>
/// <param name="LoaderChanged">加载器声明是否变化。</param>
/// <param name="ValidationIssues">发布门禁一次性收集的全部问题。</param>
public sealed record HmclPublishDiffDto(
    IReadOnlyList<string> Added,
    IReadOnlyList<string> Replaced,
    IReadOnlyList<string> Deleted,
    bool LoaderChanged,
    IReadOnlyList<HmclPublishValidationIssueDto>? ValidationIssues = null);

/// <summary>HMCL 发布门禁问题。</summary>
/// <param name="Code">稳定问题码。</param>
/// <param name="Path">相关文件路径；全局问题为空。</param>
public sealed record HmclPublishValidationIssueDto(string Code, string? Path);

/// <summary>修改 Channel。</summary>
/// <param name="Name">显示名。</param>
/// <param name="Description">说明。</param>
/// <param name="RetentionCount">保留历史数量。</param>
/// <param name="LinkedInstanceId">关联实例。</param>
public sealed record UpdateHmclChannelRequest(string Name, string Description, int RetentionCount, Guid? LinkedInstanceId);

/// <summary>切换 Channel 可见性。</summary>
/// <param name="Visibility">`public` 或 `token`。</param>
/// <param name="ConfirmationName">键名确认。</param>
public sealed record ChangeHmclVisibilityRequest(string Visibility, string ConfirmationName);

/// <summary>Channel 破坏性操作确认。</summary>
/// <param name="ConfirmationName">必须与 Channel 显示名完全一致。</param>
public sealed record ConfirmHmclChannelRequest(string ConfirmationName);

/// <summary>令牌操作结果，明文令牌仅在创建或轮换时返回。</summary>
/// <param name="Token">新令牌。</param>
/// <param name="ManifestUrl">完整安装链接。</param>
public sealed record HmclTokenResultDto(string Token, Uri ManifestUrl);

/// <summary>HMCL 访问统计点。</summary>
/// <param name="Date">UTC 日期。</param>
/// <param name="Manifest200">manifest 200 次数。</param>
/// <param name="Manifest304">manifest 304 次数。</param>
/// <param name="File200">文件 200 次数。</param>
/// <param name="File206">文件 206 次数。</param>
/// <param name="Errors">错误次数。</param>
/// <param name="Bytes">传输字节数。</param>
/// <param name="UniqueSources">唯一来源前缀数。</param>
public sealed record HmclAccessStatDto(
    DateOnly Date,
    long Manifest200,
    long Manifest304,
    long File200,
    long File206,
    long Errors,
    long Bytes,
    long UniqueSources);

/// <summary>Channel 详情。</summary>
/// <param name="Channel">摘要。</param>
/// <param name="Description">说明。</param>
/// <param name="RetentionCount">历史保留数量。</param>
/// <param name="LinkedInstanceId">关联实例。</param>
/// <param name="ManifestUrl">完整安装链接。</param>
/// <param name="IntegrityStatus">完整性状态。</param>
public sealed record HmclChannelDetailsDto(
    HmclChannelDto Channel,
    string Description,
    int RetentionCount,
    Guid? LinkedInstanceId,
    Uri ManifestUrl,
    string IntegrityStatus);

/// <summary>Release 列表摘要。</summary>
public sealed record HmclReleaseSummaryDto(
    Guid Id,
    Guid ChannelId,
    long Sequence,
    string Name,
    string Version,
    string GameVersion,
    int FileCount,
    long TotalSize,
    DateTimeOffset CreatedAt,
    DateTimeOffset? PublishedAt,
    bool IsActive);

/// <summary>分页文件清单。</summary>
public sealed record HmclReleaseFilePageDto(IReadOnlyList<HmclReleaseFileDto> Items, string? NextCursor);

/// <summary>完整性巡检结果。</summary>
public sealed record HmclIntegrityResultDto(
    DateTimeOffset CheckedAt,
    int CheckedChannels,
    int CheckedReleases,
    IReadOnlyList<HmclIntegrityFailureDto> Failures);

/// <summary>单个 Release 的完整性问题。</summary>
public sealed record HmclIntegrityFailureDto(Guid ChannelId, Guid ReleaseId, IReadOnlyList<string> Paths);

/// <summary>历史淘汰与内容回收结果。</summary>
public sealed record HmclReclamationResultDto(int RemovedReleases, int RemovedObjects, long ReclaimedBytes);
