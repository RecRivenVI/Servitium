using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using Servitium.Contracts;
using Servitium.Contracts.Events;
using Servitium.Contracts.Operations;
using Servitium.Domain.Common;
using Servitium.Domain.Operations;
using Servitium.Infrastructure.Persistence;
using Servitium.Server.Realtime;

namespace Servitium.Server.Operations;

/// <summary>把长操作先持久化，再在与请求生命周期隔离的后台执行器中运行。</summary>
internal sealed partial class ServerOperationExecutor(
    ControlPlaneStore store,
    IRealtimeEventStream events,
    ILogger<ServerOperationExecutor> logger) : BackgroundService
{
    private const int WorkerCount = 8;
    private readonly Channel<QueuedOperation> _queue = Channel.CreateUnbounded<QueuedOperation>(new UnboundedChannelOptions
    {
        SingleReader = false,
        SingleWriter = false,
        AllowSynchronousContinuations = false,
    });
    private readonly ConcurrentDictionary<Guid, ActiveOperation> _active = new();

    internal async Task<OperationAcceptedDto> EnqueueAsync(
        OperationKind kind,
        string target,
        Guid initiatedBy,
        Func<Guid, CancellationToken, Task<ServerOperationResult>> execute,
        Func<Guid, CancellationToken, Task<bool>>? cancel,
        Func<CancellationToken, Task>? cleanup,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(execute);
        Operation operation = await store.CreateAsync(kind, target, initiatedBy, cancellationToken).ConfigureAwait(false);
        ActiveOperation active = new(operation.Id, execute, cancel);
        if (!_active.TryAdd(operation.Id, active))
        {
            await store.FailAsync(operation.Id, ErrorCodes.Conflict, CancellationToken.None).ConfigureAwait(false);
            throw new DomainException(ErrorCodes.Conflict, "The operation identifier is already active.");
        }

        try
        {
            await store.ReportProgressAsync(operation.Id, 0, "queued", cancellationToken).ConfigureAwait(false);
            await PublishStartedAsync(operation, cancellationToken).ConfigureAwait(false);
            await _queue.Writer.WriteAsync(new QueuedOperation(operation, active, cleanup), cancellationToken).ConfigureAwait(false);
            return new OperationAcceptedDto(operation.Id, operation.Kind.ToString(), "running");
        }
        catch
        {
            _ = _active.TryRemove(operation.Id, out _);
            active.Dispose();
            await store.FailAsync(operation.Id, "operation-enqueue-failed", CancellationToken.None).ConfigureAwait(false);
            if (cleanup is not null)
            {
                await cleanup(CancellationToken.None).ConfigureAwait(false);
            }

            throw;
        }
    }

    internal async Task<bool> CancelAsync(Guid operationId, CancellationToken cancellationToken)
    {
        if (!_active.TryGetValue(operationId, out ActiveOperation? active))
        {
            return false;
        }

        bool accepted = await active.TryCancelAsync(cancellationToken).ConfigureAwait(false);
        if (!accepted)
        {
            return false;
        }

        bool persisted = await store.CancelAsync(operationId, cancellationToken).ConfigureAwait(false);
        if (persisted)
        {
            await PublishCompletedAsync(operationId, "Cancelled", null, null, cancellationToken).ConfigureAwait(false);
        }

        return persisted;
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var workers = new Task[WorkerCount];
        for (int index = 0; index < workers.Length; index++)
        {
            workers[index] = RunWorkerAsync(stoppingToken);
        }

        return Task.WhenAll(workers);
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _ = _queue.Writer.TryComplete();
        await base.StopAsync(cancellationToken).ConfigureAwait(false);

        // BackgroundService 停止令牌会让读取器立即退出，因此必须显式终结尚未取出的排队项，
        // 否则操作会永久停留在 Running，关联的上传暂存文件也无法回收。
        while (_queue.Reader.TryRead(out QueuedOperation? queued))
        {
            await FailIfActiveAsync(queued, "operation-interrupted").ConfigureAwait(false);
            await CleanupAsync(queued).ConfigureAwait(false);
            RemoveActive(queued.Operation.Id, queued.Active);
        }

        foreach ((Guid operationId, ActiveOperation active) in _active)
        {
            RemoveActive(operationId, active);
        }
    }

    private async Task RunWorkerAsync(CancellationToken stoppingToken)
    {
        try
        {
            await foreach (QueuedOperation queued in _queue.Reader.ReadAllAsync(stoppingToken).ConfigureAwait(false))
            {
                await ExecuteOneAsync(queued, stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // BackgroundService 的正常停机会取消读取器；必须把它收敛为成功完成，
            // 否则 Host 会将正常停机误判为后台服务故障并提前释放整个服务容器。
        }
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "后台执行边界必须把未知故障持久化为稳定 internal-error，避免工作线程退出及操作永久停留在运行状态。")]
    private async Task ExecuteOneAsync(QueuedOperation queued, CancellationToken stoppingToken)
    {
        // 队列项一旦被读取就由本方法负责终结；不能把主机停止令牌传给状态门，
        // 否则取消可能发生在 try/finally 之前并遗失该项的暂存清理。
        if (!await queued.Active.TryBeginExecutionAsync(CancellationToken.None).ConfigureAwait(false))
        {
            await CleanupAsync(queued).ConfigureAwait(false);
            RemoveActive(queued.Operation.Id, queued.Active);
            return;
        }

        try
        {
            await store.ReportProgressAsync(queued.Operation.Id, 0.05, "dispatching", stoppingToken).ConfigureAwait(false);
            _ = await events.PublishAsync(
                EventTypes.OperationProgress,
                "operation:" + queued.Operation.Id.ToString("D"),
                new OperationProgressPayload(queued.Operation.Id, 0.05, "dispatching"),
                stoppingToken).ConfigureAwait(false);

            using var execution = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, queued.Active.CancellationToken);
            ServerOperationResult result = await queued.Active.ExecuteAsync(execution.Token).ConfigureAwait(false);
            if (!await queued.Active.TryFinalizeAsync(CancellationToken.None).ConfigureAwait(false))
            {
                return;
            }

            if (result.Succeeded)
            {
                await store.CompleteAsync(queued.Operation.Id, result.Result, CancellationToken.None).ConfigureAwait(false);
                await PublishCompletedAsync(queued.Operation.Id, "Succeeded", result.Result, null, CancellationToken.None).ConfigureAwait(false);
            }
            else
            {
                string errorCode = result.ErrorCode ?? ErrorCodes.InternalError;
                await store.FailAsync(queued.Operation.Id, errorCode, result.Result, CancellationToken.None).ConfigureAwait(false);
                await PublishCompletedAsync(queued.Operation.Id, "Failed", result.Result, errorCode, CancellationToken.None).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            await FailIfActiveAsync(queued, "operation-interrupted").ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            await CancelIfActiveAsync(queued).ConfigureAwait(false);
        }
        catch (DomainException exception)
        {
            await FailIfActiveAsync(queued, exception.Code).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            OperationFailed(logger, queued.Operation.Id, exception.Message);
            await FailIfActiveAsync(queued, ErrorCodes.InternalError).ConfigureAwait(false);
        }
        finally
        {
            await CleanupAsync(queued).ConfigureAwait(false);
            RemoveActive(queued.Operation.Id, queued.Active);
        }
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "清理失败不能覆盖已持久化的操作结果或终止后台工作线程，未知异常会被结构化记录并由维护作业再次回收。")]
    private async Task CleanupAsync(QueuedOperation queued)
    {
        if (queued.Cleanup is null)
        {
            return;
        }

        try
        {
            await queued.Cleanup(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            OperationCleanupFailed(logger, queued.Operation.Id, exception.Message);
        }
    }

    private async Task FailIfActiveAsync(QueuedOperation queued, string errorCode)
    {
        if (await queued.Active.TryFinalizeAsync(CancellationToken.None).ConfigureAwait(false))
        {
            await store.FailAsync(queued.Operation.Id, errorCode, CancellationToken.None).ConfigureAwait(false);
            await PublishCompletedAsync(queued.Operation.Id, "Failed", null, errorCode, CancellationToken.None).ConfigureAwait(false);
        }
    }

    private async Task CancelIfActiveAsync(QueuedOperation queued)
    {
        if (await queued.Active.TryFinalizeAsync(CancellationToken.None).ConfigureAwait(false))
        {
            _ = await store.CancelAsync(queued.Operation.Id, CancellationToken.None).ConfigureAwait(false);
            await PublishCompletedAsync(queued.Operation.Id, "Cancelled", null, null, CancellationToken.None).ConfigureAwait(false);
        }
    }

    private async Task PublishStartedAsync(Operation operation, CancellationToken cancellationToken)
    {
        OperationStartedPayload payload = new(operation.Id, operation.Kind.ToString(), operation.Target);
        _ = await events.PublishAsync(EventTypes.OperationStarted, "operation:" + operation.Id.ToString("D"), payload, cancellationToken).ConfigureAwait(false);
        _ = await events.PublishAsync(EventTypes.OperationStarted, "global", payload, cancellationToken).ConfigureAwait(false);
    }

    private async Task PublishCompletedAsync(Guid operationId, string status, string? result, string? errorCode, CancellationToken cancellationToken)
    {
        OperationCompletedPayload payload = new(operationId, status, result, errorCode);
        _ = await events.PublishAsync(EventTypes.OperationCompleted, "operation:" + operationId.ToString("D"), payload, cancellationToken).ConfigureAwait(false);
        _ = await events.PublishAsync(EventTypes.OperationCompleted, "global", payload, cancellationToken).ConfigureAwait(false);
    }

    private void RemoveActive(Guid operationId, ActiveOperation active)
    {
        if (_active.TryRemove(new KeyValuePair<Guid, ActiveOperation>(operationId, active)))
        {
            active.Dispose();
        }
    }

    [LoggerMessage(4301, LogLevel.Error, "Server operation {OperationId} failed unexpectedly: {Reason}")]
    private static partial void OperationFailed(ILogger logger, Guid operationId, string reason);

    [LoggerMessage(4302, LogLevel.Warning, "Server operation {OperationId} staging cleanup failed: {Reason}")]
    private static partial void OperationCleanupFailed(ILogger logger, Guid operationId, string reason);

    private sealed record QueuedOperation(Operation Operation, ActiveOperation Active, Func<CancellationToken, Task>? Cleanup);

    private sealed class ActiveOperation(
        Guid operationId,
        Func<Guid, CancellationToken, Task<ServerOperationResult>> execute,
        Func<Guid, CancellationToken, Task<bool>>? cancel) : IDisposable
    {
        private readonly SemaphoreSlim _gate = new(1, 1);
        private readonly CancellationTokenSource _cancellation = new();
        private ExecutionState _state = ExecutionState.Pending;

        internal CancellationToken CancellationToken => _cancellation.Token;

        internal Task<ServerOperationResult> ExecuteAsync(CancellationToken cancellationToken)
        {
            return execute(operationId, cancellationToken);
        }

        internal async Task<bool> TryBeginExecutionAsync(CancellationToken cancellationToken)
        {
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (_state != ExecutionState.Pending)
                {
                    return false;
                }

                _state = ExecutionState.Running;
                return true;
            }
            finally
            {
                _ = _gate.Release();
            }
        }

        internal async Task<bool> TryFinalizeAsync(CancellationToken cancellationToken)
        {
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (_state is ExecutionState.Cancelled or ExecutionState.Final)
                {
                    return false;
                }

                _state = ExecutionState.Final;
                return true;
            }
            finally
            {
                _ = _gate.Release();
            }
        }

        internal async Task<bool> TryCancelAsync(CancellationToken cancellationToken)
        {
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (_state is ExecutionState.Cancelled or ExecutionState.Final)
                {
                    return false;
                }

                if (_state == ExecutionState.Running && cancel is null)
                {
                    return false;
                }

                if (_state == ExecutionState.Running &&
                    !await cancel!(operationId, cancellationToken).ConfigureAwait(false))
                {
                    return false;
                }

                _state = ExecutionState.Cancelled;
                await _cancellation.CancelAsync().ConfigureAwait(false);
                return true;
            }
            finally
            {
                _ = _gate.Release();
            }
        }

        public void Dispose()
        {
            _cancellation.Dispose();
            _gate.Dispose();
        }
    }

    private enum ExecutionState
    {
        Pending,
        Running,
        Cancelled,
        Final,
    }
}

/// <summary>后台长操作的无秘密结果。</summary>
internal sealed record ServerOperationResult(bool Succeeded, string? Result, string? ErrorCode)
{
    internal static ServerOperationResult FromAgent(Application.Abstractions.AgentCommandResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        return result.Succeeded
            ? new(true, Encoding.UTF8.GetString(result.Payload.Span), null)
            : new(false, null, result.ErrorCode);
    }
}
