using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Servitium.Application.Files;

/// <summary>控制平面中的分片上传会话。</summary>
public sealed record StoredUploadSession(
    Guid Id,
    Guid InstanceId,
    Guid NodeId,
    string FileName,
    string DestinationPath,
    long TotalBytes,
    int PartSizeBytes,
    int PartCount,
    IReadOnlyList<int> ReceivedParts,
    bool Overwrite,
    DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt);

/// <summary>上传会话持久化端口。</summary>
public interface IUploadSessionStore
{
    /// <summary>创建会话。</summary>
    Task AddAsync(StoredUploadSession session, CancellationToken cancellationToken);
    /// <summary>读取会话。</summary>
    Task<StoredUploadSession?> FindAsync(Guid id, CancellationToken cancellationToken);
    /// <summary>原子标记一个已验证分片。</summary>
    Task<StoredUploadSession> MarkPartReceivedAsync(Guid id, int index, CancellationToken cancellationToken);
    /// <summary>删除会话。</summary>
    Task DeleteAsync(Guid id, CancellationToken cancellationToken);
    /// <summary>列出已经过期的会话。</summary>
    Task<IReadOnlyList<StoredUploadSession>> ListExpiredAsync(DateTimeOffset now, CancellationToken cancellationToken);
}
