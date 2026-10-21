using System.Threading;
using System.Threading.Tasks;

namespace Servitium.Desktop.Services;

internal sealed class TransferPauseGate
{
    private readonly Lock _sync = new();
    private TaskCompletionSource? _resume;
    internal void Pause()
    {
        lock (_sync) { _resume ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); }
    }
    internal void Resume()
    {
        TaskCompletionSource? resume;
        lock (_sync) { resume = _resume; _resume = null; }
        _ = resume?.TrySetResult();
    }
    internal Task WaitAsync(CancellationToken cancellationToken)
    {
        lock (_sync) { return _resume?.Task.WaitAsync(cancellationToken) ?? Task.CompletedTask; }
    }
}
