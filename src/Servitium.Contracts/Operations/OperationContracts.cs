using System;

namespace Servitium.Contracts.Operations;

/// <summary>长操作状态。</summary>
/// <param name="Id">操作标识。</param>
/// <param name="Kind">类型。</param>
/// <param name="Status">状态。</param>
/// <param name="Progress">0 到 1 的进度。</param>
/// <param name="Stage">当前阶段。</param>
/// <param name="ErrorCode">失败错误码。</param>
/// <param name="CreatedAt">创建时间。</param>
/// <param name="CompletedAt">完成时间。</param>
public sealed record OperationDto(Guid Id, string Kind, string Status, double Progress, string? Stage, string? ErrorCode, DateTimeOffset CreatedAt, DateTimeOffset? CompletedAt);

/// <summary>操作详情。</summary>
/// <param name="Operation">操作摘要。</param>
/// <param name="Target">目标。</param>
/// <param name="InitiatedBy">发起者。</param>
/// <param name="Result">非秘密结果。</param>
/// <param name="Cancellable">是否可取消。</param>
public sealed record OperationDetailsDto(OperationDto Operation, string Target, Guid? InitiatedBy, string? Result, bool Cancellable);

/// <summary>长操作已持久化并进入执行队列。</summary>
/// <param name="OperationId">操作标识。</param>
/// <param name="Type">稳定操作类型。</param>
/// <param name="Status">当前状态。</param>
public sealed record OperationAcceptedDto(Guid OperationId, string Type, string Status);
