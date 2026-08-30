namespace NightGate.Core;

public interface INightMutationGate
{
    ValueTask<IDisposable> EnterAsync(CancellationToken cancellationToken = default);
}

public sealed class NoOpNightMutationGate : INightMutationGate
{
    public static NoOpNightMutationGate Instance { get; } = new();

    private NoOpNightMutationGate()
    {
    }

    public ValueTask<IDisposable> EnterAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult<IDisposable>(NoOpLease.Instance);
    }

    private sealed class NoOpLease : IDisposable
    {
        public static NoOpLease Instance { get; } = new();

        public void Dispose()
        {
        }
    }
}
