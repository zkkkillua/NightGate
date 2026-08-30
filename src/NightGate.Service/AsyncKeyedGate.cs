using System.Collections.Concurrent;

namespace NightGate.Service;

internal sealed class AsyncKeyedGate<TKey>
    where TKey : notnull
{
    private readonly ConcurrentDictionary<TKey, GateEntry> _entries;

    public AsyncKeyedGate(IEqualityComparer<TKey>? comparer = null)
    {
        _entries = new(comparer ?? EqualityComparer<TKey>.Default);
    }

    internal int ActiveKeyCount => _entries.Count;

    public async ValueTask<IDisposable> EnterAsync(
        TKey key,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        GateEntry entry = RentEntry(key);
        try
        {
            await entry.Semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
            return new Lease(this, key, entry);
        }
        catch
        {
            ReturnEntry(key, entry);
            throw;
        }
    }

    private GateEntry RentEntry(TKey key)
    {
        while (true)
        {
            GateEntry entry = _entries.GetOrAdd(key, static _ => new());
            lock (entry.SyncRoot)
            {
                if (entry.Retired)
                {
                    continue;
                }

                entry.ReferenceCount = checked(entry.ReferenceCount + 1);
                return entry;
            }
        }
    }

    private void Release(TKey key, GateEntry entry)
    {
        entry.Semaphore.Release();
        ReturnEntry(key, entry);
    }

    private void ReturnEntry(TKey key, GateEntry entry)
    {
        bool dispose;
        lock (entry.SyncRoot)
        {
            if (entry.ReferenceCount <= 0)
            {
                throw new InvalidOperationException("The keyed gate reference count is invalid.");
            }

            entry.ReferenceCount--;
            dispose = entry.ReferenceCount == 0;
            if (dispose)
            {
                entry.Retired = true;
                _entries.TryRemove(key, out _);
            }
        }

        if (dispose)
        {
            entry.Semaphore.Dispose();
        }
    }

    private sealed class GateEntry
    {
        public object SyncRoot { get; } = new();

        public SemaphoreSlim Semaphore { get; } = new(1, 1);

        public int ReferenceCount { get; set; }

        public bool Retired { get; set; }
    }

    private sealed class Lease(
        AsyncKeyedGate<TKey> owner,
        TKey key,
        GateEntry entry) : IDisposable
    {
        private AsyncKeyedGate<TKey>? _owner = owner;

        public void Dispose()
        {
            AsyncKeyedGate<TKey>? current = Interlocked.Exchange(ref _owner, null);
            current?.Release(key, entry);
        }
    }
}
