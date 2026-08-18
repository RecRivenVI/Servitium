using System;
using System.Threading;
using System.Threading.Tasks;

namespace Servitium.Infrastructure.Persistence;

/// <summary>串行化 SQLite 写事务，避免多个写入者相互锁死。</summary>
public sealed class SqliteWriteCoordinator : IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>在单写入临界区执行并返回结果。</summary>
    public async Task<T> ExecuteAsync<T>(Func<Task<T>> action, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(action);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await action().ConfigureAwait(false);
        }
        finally
        {
            _ = _gate.Release();
        }
    }

    /// <summary>在单写入临界区执行操作。</summary>
    public async Task ExecuteAsync(Func<Task> action, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(action);
        _ = await ExecuteAsync(
            async () =>
            {
                await action().ConfigureAwait(false);
                return true;
            },
            cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _gate.Dispose();
    }
}
