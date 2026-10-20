using System;
using System.Collections.Generic;

namespace Servitium.Contracts.Files;

/// <summary>文件条目。</summary>
/// <param name="Path">相对路径。</param>
/// <param name="IsDirectory">是否目录。</param>
/// <param name="Size">大小。</param>
/// <param name="ModifiedAt">修改时间。</param>
public sealed record FileEntryDto(string Path, bool IsDirectory, long Size, DateTimeOffset ModifiedAt);

/// <summary>文件目录的游标分页结果。</summary>
/// <param name="Items">当前页条目。</param>
/// <param name="NextCursor">下一页不透明游标；没有更多条目时为空。</param>
public sealed record FileEntryPageDto(IReadOnlyList<FileEntryDto> Items, string? NextCursor);

/// <summary>归档安全检查结果。</summary>
/// <param name="Safe">是否安全。</param>
/// <param name="EntryCount">条目数。</param>
/// <param name="ExpandedSize">预计解压大小。</param>
/// <param name="Issues">全部问题。</param>
public sealed record ArchiveInspectionDto(bool Safe, int EntryCount, long ExpandedSize, IReadOnlyList<string> Issues);

/// <summary>单路径文件操作请求。</summary>
/// <param name="Path">实例根目录内的相对路径。</param>
public sealed record FilePathRequest(string Path);

/// <summary>双路径文件操作请求。</summary>
/// <param name="SourcePath">源相对路径。</param>
/// <param name="DestinationPath">目标相对路径。</param>
/// <param name="CollisionPolicy">同名策略：`reject`、`overwrite`、`keep-both` 或 `skip`。</param>
public sealed record FileTransferRequest(string SourcePath, string DestinationPath, string CollisionPolicy = "reject");

/// <summary>同一目录内重命名请求。</summary>
/// <param name="Path">当前相对路径。</param>
/// <param name="NewName">不含目录的新名称。</param>
public sealed record RenameFileRequest(string Path, string NewName);

/// <summary>文本写入请求。</summary>
/// <param name="Content">文本内容。</param>
/// <param name="Encoding">编码名称；当前安全基线支持 UTF-8。</param>
public sealed record WriteTextFileRequest(string Content, string Encoding);

/// <summary>文本文件内容。</summary>
/// <param name="Path">相对路径。</param>
/// <param name="Content">文本内容。</param>
/// <param name="Encoding">编码名称。</param>
/// <param name="LineEnding">行尾模式。</param>
/// <param name="Size">字节数。</param>
public sealed record TextFileDto(string Path, string Content, string Encoding, string LineEnding, long Size);

/// <summary>实例文件系统容量。</summary>
/// <param name="TotalBytes">卷总容量。</param>
/// <param name="AvailableBytes">卷可用容量。</param>
/// <param name="ActiveTasks">当前文件任务数。</param>
/// <param name="TaskLimit">文件任务并发上限。</param>
/// <param name="ActiveArchiveTasks">当前归档任务数。</param>
/// <param name="ArchiveTaskLimit">归档任务并发上限。</param>
public sealed record FileSystemStatusDto(
    long TotalBytes,
    long AvailableBytes,
    int ActiveTasks = 0,
    int TaskLimit = 4,
    int ActiveArchiveTasks = 0,
    int ArchiveTaskLimit = 2);

/// <summary>批量路径请求。</summary>
/// <param name="Paths">实例根目录内的相对路径。</param>
public sealed record FileBatchRequest(IReadOnlyList<string> Paths);

/// <summary>压缩请求。</summary>
/// <param name="Paths">要压缩的相对路径。</param>
/// <param name="DestinationPath">目标 ZIP 相对路径。</param>
/// <param name="EntryNameEncoding">ZIP 条目名编码。</param>
public sealed record CompressFilesRequest(IReadOnlyList<string> Paths, string DestinationPath, string EntryNameEncoding = "utf-8");

/// <summary>解压请求。</summary>
/// <param name="ArchivePath">归档相对路径。</param>
/// <param name="DestinationPath">目标目录相对路径。</param>
/// <param name="EntryNameEncoding">条目名编码。</param>
public sealed record ExtractArchiveRequest(string ArchivePath, string DestinationPath, string EntryNameEncoding = "auto");

/// <summary>从 URL 下载请求。</summary>
/// <param name="Url">HTTP 或 HTTPS URL。</param>
/// <param name="DestinationPath">目标相对路径。</param>
/// <param name="Overwrite">是否覆盖。</param>
public sealed record DownloadFromUrlRequest(Uri Url, string DestinationPath, bool Overwrite);

/// <summary>创建分片上传会话。</summary>
/// <param name="FileName">用于显示和扩展名校验的文件名。</param>
/// <param name="DestinationPath">最终目标相对路径。</param>
/// <param name="TotalBytes">总字节数。</param>
/// <param name="PartSizeBytes">期望分片大小。</param>
/// <param name="Overwrite">是否允许原子覆盖目标。</param>
public sealed record CreateUploadSessionRequest(
    string FileName,
    string DestinationPath,
    long TotalBytes,
    int PartSizeBytes = 8 * 1024 * 1024,
    bool Overwrite = false);

/// <summary>上传会话状态。</summary>
/// <param name="Id">会话标识。</param>
/// <param name="FileName">文件名。</param>
/// <param name="DestinationPath">目标路径。</param>
/// <param name="TotalBytes">总字节数。</param>
/// <param name="PartSizeBytes">分片大小。</param>
/// <param name="PartCount">分片数。</param>
/// <param name="ReceivedParts">已接收分片索引。</param>
/// <param name="ExpiresAt">过期时间。</param>
/// <param name="Overwrite">是否允许覆盖目标。</param>
public sealed record UploadSessionDto(
    Guid Id,
    string FileName,
    string DestinationPath,
    long TotalBytes,
    int PartSizeBytes,
    int PartCount,
    IReadOnlyList<int> ReceivedParts,
    DateTimeOffset ExpiresAt,
    bool Overwrite = false);

/// <summary>上传完成请求。</summary>
/// <param name="Sha256">完整文件 SHA-256 十六进制。</param>
public sealed record CompleteUploadRequest(string Sha256);

/// <summary>文件任务结果。</summary>
/// <param name="TaskId">任务标识。</param>
/// <param name="Kind">任务类型。</param>
/// <param name="Status">状态。</param>
/// <param name="Path">目标路径。</param>
public sealed record FileTaskDto(Guid TaskId, string Kind, string Status, string Path);

/// <summary>可下载文件的信息。</summary>
/// <param name="Path">相对路径。</param>
/// <param name="Length">文件大小。</param>
/// <param name="ModifiedAt">最后修改时间。</param>
public sealed record FileDownloadInfoDto(string Path, long Length, DateTimeOffset ModifiedAt);
