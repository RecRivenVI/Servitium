using System;

using Servitium.Domain.Primitives;

namespace Servitium.Domain.Operations;

/// <summary>定义长操作类型。</summary>
public enum OperationKind
{
    /// <summary>创建实例。</summary>
    CreateInstance,
    /// <summary>导入服务端归档。</summary>
    ImportServerArchive,
    /// <summary>转换 Prism 实例。</summary>
    ConvertPrismInstance,
    /// <summary>创建 HMCL Release。</summary>
    CreateHmclRelease,
    /// <summary>发布 HMCL Channel。</summary>
    PublishHmclChannel,
    /// <summary>删除受管工作目录。</summary>
    DeleteWorkingDirectory,
    /// <summary>复制实例文件或目录。</summary>
    CopyFiles,
    /// <summary>移动实例文件或目录。</summary>
    MoveFiles,
    /// <summary>删除实例文件或目录。</summary>
    DeleteFiles,
    /// <summary>压缩实例内容。</summary>
    CompressFiles,
    /// <summary>解压实例归档。</summary>
    ExtractArchive,
    /// <summary>从受限 URL 下载实例文件。</summary>
    DownloadFileFromUrl,
    /// <summary>探测节点 Java 运行时。</summary>
    DiscoverJava,
    /// <summary>导入节点 Java 运行时。</summary>
    ImportJava,
    /// <summary>取消登记或删除导入的 Java 运行时。</summary>
    UnregisterJava,
    /// <summary>启动实例。</summary>
    StartInstance,
    /// <summary>停止实例。</summary>
    StopInstance,
    /// <summary>重启实例。</summary>
    RestartInstance,
    /// <summary>强制终止实例。</summary>
    ForceStopInstance,
    /// <summary>执行实例更新动作。</summary>
    UpdateInstance,
    /// <summary>批量执行实例动作。</summary>
    BatchInstances,
    /// <summary>合并并提交文件上传。</summary>
    CompleteFileUpload,
    /// <summary>提交模组或插件上传。</summary>
    UploadMod,
    /// <summary>删除 HMCL Channel 及其 Release。</summary>
    DeleteHmclChannel,
    /// <summary>把 HMCL Release 同步到实例。</summary>
    SyncHmclRelease,
}

/// <summary>定义长操作状态。</summary>
public enum OperationStatus
{
    /// <summary>等待执行。</summary>
    Pending,
    /// <summary>正在执行。</summary>
    Running,
    /// <summary>成功。</summary>
    Succeeded,
    /// <summary>失败。</summary>
    Failed,
    /// <summary>已取消。</summary>
    Cancelled,
}

/// <summary>表示具有受控终态的长操作。</summary>
/// <remarks>初始化长操作。</remarks>
public sealed class Operation(Guid id, OperationKind kind, string target, DateTimeOffset createdAt) : Entity(id)
{

    /// <summary>获取类型。</summary>
    public OperationKind Kind { get; } = kind;

    /// <summary>获取目标。</summary>
    public string Target { get; } = Guard.AgainstNullOrWhiteSpace(target, nameof(target));

    /// <summary>获取状态。</summary>
    public OperationStatus Status { get; private set; } = OperationStatus.Pending;

    /// <summary>获取创建时间。</summary>
    public DateTimeOffset CreatedAt { get; } = Guard.AsUtc(createdAt);

    /// <summary>获取完成时间。</summary>
    public DateTimeOffset? CompletedAt { get; private set; }

    /// <summary>获取失败错误码。</summary>
    public string? ErrorCode { get; private set; }

    /// <summary>进入运行状态。</summary>
    public void Start()
    {
        Transition(OperationStatus.Pending, OperationStatus.Running, null, null);
    }

    /// <summary>标记成功。</summary>
    public void Succeed(DateTimeOffset completedAt)
    {
        Transition(OperationStatus.Running, OperationStatus.Succeeded, completedAt, null);
    }

    /// <summary>标记失败。</summary>
    public void Fail(string errorCode, DateTimeOffset completedAt)
    {
        Transition(
            OperationStatus.Running,
            OperationStatus.Failed,
            completedAt,
            Guard.AgainstNullOrWhiteSpace(errorCode, nameof(errorCode)));
    }

    /// <summary>在提交点前取消。</summary>
    public void Cancel(DateTimeOffset completedAt, bool commitPointReached)
    {
        if (commitPointReached || Status is OperationStatus.Succeeded or OperationStatus.Failed or OperationStatus.Cancelled)
        {
            throw new DomainException("operation-not-cancellable", "The operation can no longer be cancelled.");
        }

        Status = OperationStatus.Cancelled;
        CompletedAt = Guard.AsUtc(completedAt);
    }

    private void Transition(OperationStatus required, OperationStatus target, DateTimeOffset? completedAt, string? errorCode)
    {
        if (Status != required)
        {
            throw new DomainException("conflict", $"Cannot transition operation from {Status} to {target}.");
        }

        Status = target;
        CompletedAt = completedAt is null ? null : Guard.AsUtc(completedAt.Value);
        ErrorCode = errorCode;
    }
}
