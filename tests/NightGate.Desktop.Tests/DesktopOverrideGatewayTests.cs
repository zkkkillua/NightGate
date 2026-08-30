using System.Text;
using NightGate.Desktop;

namespace NightGate.Desktop.Tests;

public sealed class DesktopOverrideGatewayTests
{
    [Fact]
    public async Task Gateway_DelegatesTheExactTypedRequestToTheDesktopClient()
    {
        RecordingTransport transport = new(
            """
            {"version":1,"type":"requestOverrideResult","requestId":"override-1","payload":{"status":"success","data":{"accepted":false,"error":"cooldown"}}}
            """);
        NightGateDesktopClient client = new(transport, new FixedRequestIdSource());
        DesktopClientOverrideGateway gateway = new(client);

        DesktopOverrideResult result = await gateway.RequestAsync(
            new DesktopOverrideRequest(DesktopOverrideKind.TeamRescue));

        Assert.False(result.Accepted);
        Assert.Equal("cooldown", result.Error);
        string request = Encoding.UTF8.GetString(Assert.Single(transport.Requests).Span);
        Assert.Contains("\"kind\":\"teamRescue\"", request, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(DesktopOverrideKind.TeamRescue)]
    [InlineData(DesktopOverrideKind.Emergency)]
    [InlineData(DesktopOverrideKind.Entertainment)]
    public async Task EveryOverrideKind_HoldsTheCutoffBarrierThroughImmediatePolicyRefresh(
        DesktopOverrideKind kind)
    {
        SignallingBarrier barrier = new();
        RefreshBlockingTransport transport = new(kind);
        NightGateDesktopClient client = new(transport, new SequenceRequestIdSource());
        DesktopClientOverrideGateway gateway = new(client, barrier);
        DesktopOverrideRequest request = new(
            kind,
            kind == DesktopOverrideKind.Emergency
                ? DesktopEmergencyReason.Health
                : null);

        Task<DesktopOverrideResult> requesting = gateway.RequestAsync(request).AsTask();
        await transport.RefreshStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Task<IDisposable> contender = barrier.EnterAsync().AsTask();
        await barrier.SecondEntryRequested.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.False(contender.IsCompleted);

        transport.ReleaseRefresh();
        DesktopOverrideResult result = await requesting.WaitAsync(TimeSpan.FromSeconds(2));
        using IDisposable lease = await contender.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.True(result.Accepted);
        Assert.Equal(2, transport.RequestCount);
    }

    [Fact]
    public async Task CancellationWhileWaitingForBarrier_DoesNotSendOrDeadlock()
    {
        CutoffPipelineBarrier barrier = new();
        IDisposable held = await barrier.EnterAsync();
        RecordingTransport transport = new(
            """
            {"version":1,"type":"requestOverrideResult","requestId":"override-1","payload":{"status":"success","data":{"accepted":false,"error":"cooldown"}}}
            """);
        DesktopClientOverrideGateway gateway = new(
            new NightGateDesktopClient(transport, new FixedRequestIdSource()),
            barrier);
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            gateway.RequestAsync(
                    new DesktopOverrideRequest(DesktopOverrideKind.TeamRescue),
                    cancellation.Token)
                .AsTask());

        Assert.Empty(transport.Requests);
        held.Dispose();
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(2));
        using IDisposable next = await barrier.EnterAsync(timeout.Token);
    }

    [Fact]
    public async Task TransportFailure_ReleasesBarrierForTheNextOperation()
    {
        CutoffPipelineBarrier barrier = new();
        DesktopClientOverrideGateway gateway = new(
            new NightGateDesktopClient(new ThrowingTransport(), new FixedRequestIdSource()),
            barrier);

        DesktopOverrideResult result = await gateway.RequestAsync(
            new DesktopOverrideRequest(DesktopOverrideKind.TeamRescue));
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(2));
        using IDisposable lease = await barrier.EnterAsync(timeout.Token);

        Assert.False(result.Accepted);
    }

    private sealed class FixedRequestIdSource : IProtocolRequestIdSource
    {
        public string NextRequestId() => "override-1";
    }

    private sealed class SequenceRequestIdSource : IProtocolRequestIdSource
    {
        private int _next;

        public string NextRequestId() => Interlocked.Increment(ref _next) switch
        {
            1 => "override-1",
            2 => "policy-2",
            _ => throw new InvalidOperationException("Unexpected request."),
        };
    }

    private sealed class SignallingBarrier : ICutoffPipelineBarrier
    {
        private readonly CutoffPipelineBarrier _inner = new();
        private int _entries;

        public TaskCompletionSource SecondEntryRequested { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public async ValueTask<IDisposable> EnterAsync(
            CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref _entries) == 2)
            {
                SecondEntryRequested.TrySetResult();
            }

            return await _inner.EnterAsync(cancellationToken);
        }
    }

    private sealed class RefreshBlockingTransport(DesktopOverrideKind kind) :
        INightGatePipeTransport
    {
        private readonly TaskCompletionSource _releaseRefresh = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private int _requestCount;

        public int RequestCount => Volatile.Read(ref _requestCount);

        public TaskCompletionSource RefreshStarted { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public async ValueTask<ReadOnlyMemory<byte>> ExchangeAsync(
            ReadOnlyMemory<byte> requestUtf8,
            CancellationToken cancellationToken = default)
        {
            int request = Interlocked.Increment(ref _requestCount);
            if (request == 1)
            {
                string token = kind switch
                {
                    DesktopOverrideKind.TeamRescue => "teamRescue",
                    DesktopOverrideKind.Emergency => "emergency",
                    DesktopOverrideKind.Entertainment => "entertainment",
                    _ => throw new ArgumentOutOfRangeException(nameof(kind)),
                };
                int durationMinutes = kind == DesktopOverrideKind.Emergency ? 30 : 20;
                string response =
                    """
                    {"version":1,"type":"requestOverrideResult","requestId":"override-1","payload":{"status":"success","data":{"accepted":true,"kind":"__KIND__","startsAtUtc":"2026-07-07T00:00:00+00:00","endsAtUtc":"2026-07-07T00:__MINUTES__:00+00:00"}}}
                    """
                    .Replace("__KIND__", token, StringComparison.Ordinal)
                    .Replace(
                        "__MINUTES__",
                        durationMinutes.ToString("00", System.Globalization.CultureInfo.InvariantCulture),
                        StringComparison.Ordinal);
                return Encoding.UTF8.GetBytes(response);
            }

            RefreshStarted.TrySetResult();
            await _releaseRefresh.Task.WaitAsync(cancellationToken);
            return Encoding.UTF8.GetBytes("{}");
        }

        public void ReleaseRefresh() => _releaseRefresh.TrySetResult();
    }

    private sealed class ThrowingTransport : INightGatePipeTransport
    {
        public ValueTask<ReadOnlyMemory<byte>> ExchangeAsync(
            ReadOnlyMemory<byte> requestUtf8,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("transport failed");
    }

    private sealed class RecordingTransport(string responseJson) : INightGatePipeTransport
    {
        private readonly ReadOnlyMemory<byte> _response = Encoding.UTF8.GetBytes(responseJson);

        public List<ReadOnlyMemory<byte>> Requests { get; } = [];

        public ValueTask<ReadOnlyMemory<byte>> ExchangeAsync(
            ReadOnlyMemory<byte> requestUtf8,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add(requestUtf8.ToArray());
            return ValueTask.FromResult(_response);
        }
    }
}
