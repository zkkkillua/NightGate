namespace NightGate.Service.Tests;

public sealed class AsyncKeyedGateTests
{
    [Fact]
    public async Task EnterAsync_SameKeySerializesAndRemovesIdleEntry()
    {
        AsyncKeyedGate<string> gate = new(StringComparer.OrdinalIgnoreCase);
        IDisposable first = await gate.EnterAsync("state.db");

        Task<IDisposable> waiting = gate.EnterAsync("STATE.DB").AsTask();

        Assert.False(waiting.IsCompleted);
        Assert.Equal(1, gate.ActiveKeyCount);
        first.Dispose();
        IDisposable second = await waiting.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.Equal(1, gate.ActiveKeyCount);

        second.Dispose();
        Assert.Equal(0, gate.ActiveKeyCount);
    }

    [Fact]
    public async Task EnterAsync_CancelledWaiterDoesNotStrandKeyOrBlockLaterCaller()
    {
        AsyncKeyedGate<string> gate = new(StringComparer.Ordinal);
        IDisposable holder = await gate.EnterAsync("state.db");
        using CancellationTokenSource cancellation = new();
        Task<IDisposable> waiting = gate.EnterAsync("state.db", cancellation.Token).AsTask();
        Assert.False(waiting.IsCompleted);

        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        Assert.Equal(1, gate.ActiveKeyCount);
        holder.Dispose();
        Assert.Equal(0, gate.ActiveKeyCount);

        IDisposable later = await gate.EnterAsync("state.db")
            .AsTask()
            .WaitAsync(TimeSpan.FromSeconds(1));
        later.Dispose();
        Assert.Equal(0, gate.ActiveKeyCount);
    }

    [Fact]
    public async Task EnterAsync_DifferentKeysDoNotBlockEachOther()
    {
        AsyncKeyedGate<string> gate = new(StringComparer.Ordinal);
        IDisposable first = await gate.EnterAsync("first.db");

        Task<IDisposable> otherCall = gate.EnterAsync("second.db").AsTask();

        Assert.True(otherCall.IsCompletedSuccessfully);
        IDisposable second = await otherCall;
        Assert.Equal(2, gate.ActiveKeyCount);

        first.Dispose();
        second.Dispose();
        Assert.Equal(0, gate.ActiveKeyCount);
    }
}
