namespace NightGate.Desktop;

/// <summary>
/// Orders supported desktop override requests against the final process termination stage.
/// This is an in-process coordination boundary, not a security boundary against another
/// same-SID process deliberately issuing protocol commands.
/// </summary>
public interface ICutoffPipelineBarrier
{
    ValueTask<IDisposable> EnterAsync(
        CancellationToken cancellationToken = default);
}

public sealed class CutoffPipelineBarrier : ICutoffPipelineBarrier
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async ValueTask<IDisposable> EnterAsync(
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        return new Lease(_gate);
    }

    private sealed class Lease(SemaphoreSlim gate) : IDisposable
    {
        private SemaphoreSlim? _gate = gate;

        public void Dispose() => Interlocked.Exchange(ref _gate, null)?.Release();
    }
}
