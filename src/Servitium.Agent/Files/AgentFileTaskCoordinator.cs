using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;

using Servitium.Agent.ControlPlane;
using Servitium.Agent.Hosting;
using Servitium.Agent.Storage;
using Servitium.Application.Abstractions;
using Servitium.Contracts.Events;
using Servitium.Domain.Primitives;
using Servitium.Infrastructure.Persistence;

namespace Servitium.Agent.Files;

internal sealed class AgentFileTaskCoordinator(
    IDbContextFactory<AgentDbContext> contextFactory,
    SqliteWriteCoordinator writeCoordinator,
    AgentNodeConfigurationStore configuration,
    AgentOutboxStore outbox,
    IClock clock) : IHostedService, IDisposable
{
    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> _running = new();
    private readonly DynamicQuota _fileQuota = new(() => configuration.Current.FileTaskConcurrency);
    private readonly DynamicQuota _archiveQuota = new(() => configuration.Current.CompressionTaskConcurrency);
    private readonly DynamicQuota _urlQuota = new(() => configuration.Current.UrlDownloadConcurrency);

    internal int ActiveFileTasks => _fileQuota.Active;
    internal int ActiveArchiveTasks => _archiveQuota.Active;
    internal int ActiveUrlTasks => _urlQuota.Active;

    internal async Task<T> RunAsync<T>(
        Guid taskId,
        Guid instanceId,
        string kind,
        string targetPath,
        bool archive,
        bool urlDownload,
        Func<CancellationToken, Task<T>> action,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(action);
        return await RunCoreAsync(
            taskId,
            instanceId,
            kind,
            targetPath,
            archive,
            urlDownload,
            (_, token) => action(token),
            cancellationToken).ConfigureAwait(false);
    }

    internal Task<T> RunWithProgressAsync<T>(
        Guid taskId,
        Guid instanceId,
        string kind,
        string targetPath,
        bool archive,
        bool urlDownload,
        Func<Func<double, CancellationToken, Task>, CancellationToken, Task<T>> action,
        CancellationToken cancellationToken)
    {
        return RunCoreAsync(taskId, instanceId, kind, targetPath, archive, urlDownload, action, cancellationToken);
    }

    private async Task<T> RunCoreAsync<T>(
        Guid taskId,
        Guid instanceId,
        string kind,
        string targetPath,
        bool archive,
        bool urlDownload,
        Func<Func<double, CancellationToken, Task>, CancellationToken, Task<T>> action,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (taskId == Guid.Empty || instanceId == Guid.Empty || string.IsNullOrWhiteSpace(kind) || string.IsNullOrWhiteSpace(targetPath))
        {
            throw new DomainException("validation-failed", "The file task metadata is invalid.");
        }

        using var taskCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (!_running.TryAdd(taskId, taskCancellation))
        {
            throw new DomainException("conflict", "The file task already exists.");
        }

        DateTimeOffset createdAt = clock.UtcNow;
        await AddRowAsync(taskId, instanceId, kind, targetPath, createdAt, cancellationToken).ConfigureAwait(false);
        try
        {
            using DynamicQuota.Lease fileLease = await _fileQuota.AcquireAsync(taskCancellation.Token).ConfigureAwait(false);
            DynamicQuota.Lease? archiveLease = null;
            DynamicQuota.Lease? urlLease = null;
            try
            {
                if (archive)
                {
                    archiveLease = await _archiveQuota.AcquireAsync(taskCancellation.Token).ConfigureAwait(false);
                }

                if (urlDownload)
                {
                    urlLease = await _urlQuota.AcquireAsync(taskCancellation.Token).ConfigureAwait(false);
                }

                await SetStateAsync(taskId, "Running", null, false, taskCancellation.Token).ConfigureAwait(false);
                await outbox.EnqueueAsync(EventTypes.FileTaskProgress, "instance:" + instanceId.ToString("D"), new { taskId, instanceId, kind, status = "Running", progress = 0d }, false, taskCancellation.Token).ConfigureAwait(false);
                async Task ReportProgressAsync(double value, CancellationToken reportToken)
                {
                    double progress = Math.Clamp(value, 0d, 1d);
                    await outbox.EnqueueAsync(
                        EventTypes.FileTaskProgress,
                        "instance:" + instanceId.ToString("D"),
                        new { taskId, instanceId, kind, status = "Running", progress },
                        false,
                        reportToken).ConfigureAwait(false);
                }

                T result = await action(ReportProgressAsync, taskCancellation.Token).ConfigureAwait(false);
                await SetStateAsync(taskId, "Succeeded", null, false, CancellationToken.None).ConfigureAwait(false);
                await outbox.EnqueueAsync(EventTypes.FileTaskProgress, "instance:" + instanceId.ToString("D"), new { taskId, instanceId, kind, status = "Succeeded", progress = 1d }, false, CancellationToken.None).ConfigureAwait(false);
                return result;
            }
            finally
            {
                urlLease?.Dispose();
                archiveLease?.Dispose();
            }
        }
        catch (OperationCanceledException) when (taskCancellation.IsCancellationRequested)
        {
            await SetStateAsync(taskId, "Cancelled", null, true, CancellationToken.None).ConfigureAwait(false);
            throw;
        }
        catch (DomainException exception)
        {
            await SetStateAsync(taskId, "Failed", exception.Code, false, CancellationToken.None).ConfigureAwait(false);
            throw;
        }
        catch
        {
            await SetStateAsync(taskId, "Failed", "internal-error", false, CancellationToken.None).ConfigureAwait(false);
            throw;
        }
        finally
        {
            _ = _running.TryRemove(taskId, out _);
        }
    }

    internal async Task CancelAsync(Guid instanceId, Guid taskId, CancellationToken cancellationToken)
    {
        if (!_running.TryGetValue(taskId, out CancellationTokenSource? source))
        {
            throw new DomainException("operation-not-cancellable", "The file task is not running or has passed its commit point.");
        }

        using AgentDbContext context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        LocalFileTaskRow? row = await context.FileTasks.AsNoTracking().SingleOrDefaultAsync(candidate => candidate.Id == taskId, cancellationToken).ConfigureAwait(false);
        if (row is null || row.InstanceId != instanceId || row.Status is not ("Pending" or "Running"))
        {
            throw new DomainException("operation-not-cancellable", "The file task is not cancellable.");
        }

        await source.CancelAsync().ConfigureAwait(false);
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        return writeCoordinator.ExecuteAsync(async () =>
        {
            using AgentDbContext context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            _ = await context.FileTasks.Where(row => row.Status == "Pending" || row.Status == "Running")
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(row => row.Status, "Failed")
                    .SetProperty(row => row.ErrorCode, "operation-interrupted")
                    .SetProperty(row => row.UpdatedAtUnixMilliseconds, clock.UtcNow.ToUnixTimeMilliseconds()), cancellationToken).ConfigureAwait(false);
        }, cancellationToken);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        foreach (CancellationTokenSource source in _running.Values)
        {
            await source.CancelAsync().ConfigureAwait(false);
        }
    }

    private async Task AddRowAsync(Guid taskId, Guid instanceId, string kind, string targetPath, DateTimeOffset createdAt, CancellationToken cancellationToken)
    {
        await writeCoordinator.ExecuteAsync(async () =>
        {
            using AgentDbContext context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            _ = context.FileTasks.Add(new LocalFileTaskRow
            {
                Id = taskId,
                InstanceId = instanceId,
                Kind = kind,
                Status = "Pending",
                TargetPath = targetPath,
                CreatedAtUnixMilliseconds = createdAt.ToUnixTimeMilliseconds(),
                UpdatedAtUnixMilliseconds = createdAt.ToUnixTimeMilliseconds(),
            });
            _ = await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);
    }

    private Task SetStateAsync(Guid taskId, string status, string? errorCode, bool cancellationRequested, CancellationToken cancellationToken)
    {
        return writeCoordinator.ExecuteAsync(async () =>
        {
            using AgentDbContext context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            LocalFileTaskRow? row = await context.FileTasks.SingleOrDefaultAsync(candidate => candidate.Id == taskId, cancellationToken).ConfigureAwait(false);
            if (row is null)
            {
                return;
            }

            row.Status = status;
            row.ErrorCode = errorCode;
            row.CancellationRequested = cancellationRequested;
            row.UpdatedAtUnixMilliseconds = clock.UtcNow.ToUnixTimeMilliseconds();
            _ = await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }, cancellationToken);
    }

    public void Dispose()
    {
        foreach (CancellationTokenSource source in _running.Values)
        {
            source.Dispose();
        }

        _fileQuota.Dispose();
        _archiveQuota.Dispose();
        _urlQuota.Dispose();
    }

    private sealed class DynamicQuota(Func<int> limit) : IDisposable
    {
        private readonly Lock _sync = new();
        private readonly Queue<TaskCompletionSource<Lease>> _waiters = new();
        private int _active;
        private bool _disposed;

        internal int Active
        {
            get
            {
                lock (_sync)
                {
                    return _active;
                }
            }
        }

        [System.Diagnostics.CodeAnalysis.SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope", Justification = "The returned quota lease transfers disposal ownership to the awaiting caller.")]
        internal Task<Lease> AcquireAsync(CancellationToken cancellationToken)
        {
            TaskCompletionSource<Lease> waiter;
            lock (_sync)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (_active < Math.Max(1, limit()))
                {
                    _active++;
                    return Task.FromResult(new Lease(Release));
                }

                waiter = new(TaskCreationOptions.RunContinuationsAsynchronously);
                _waiters.Enqueue(waiter);
            }

            return WaitForLeaseAsync(waiter, cancellationToken);
        }

        private static async Task<Lease> WaitForLeaseAsync(TaskCompletionSource<Lease> waiter, CancellationToken cancellationToken)
        {
            using CancellationTokenRegistration registration = cancellationToken.Register(
                static state =>
                {
                    (TaskCompletionSource<Lease> completion, CancellationToken token) = ((TaskCompletionSource<Lease>, CancellationToken))state!;
                    _ = completion.TrySetCanceled(token);
                },
                (waiter, cancellationToken));
            return await waiter.Task.ConfigureAwait(false);
        }

        [System.Diagnostics.CodeAnalysis.SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope", Justification = "The queued consumer receives ownership of the quota lease.")]
        private void Release()
        {
            TaskCompletionSource<Lease>? next = null;
            lock (_sync)
            {
                _active--;
                while (_waiters.Count > 0 && _active < Math.Max(1, limit()))
                {
                    TaskCompletionSource<Lease> candidate = _waiters.Dequeue();
                    if (!candidate.Task.IsCompleted)
                    {
                        _active++;
                        next = candidate;
                        break;
                    }
                }
            }

            _ = next?.TrySetResult(new Lease(Release));
        }

        public void Dispose()
        {
            TaskCompletionSource<Lease>[] waiters;
            lock (_sync)
            {
                _disposed = true;
                waiters = [.. _waiters];
                _waiters.Clear();
            }

            foreach (TaskCompletionSource<Lease> waiter in waiters)
            {
                _ = waiter.TrySetException(new ObjectDisposedException(nameof(DynamicQuota)));
            }
        }

        internal sealed class Lease(Action release) : IDisposable
        {
            private Action? _release = release;

            public void Dispose()
            {
                Interlocked.Exchange(ref _release, null)?.Invoke();
            }
        }
    }
}
