using NightGate.Desktop;

namespace NightGate.Desktop.Tests;

public sealed class DesktopPrivacyEventSinkTests
{
    [Fact]
    public async Task MissedLock_RecordsOnlyExistingPrivacySafeEventAndDisposeAwaitsIt()
    {
        RecordingClient client = new(blockWrites: true);
        DesktopPrivacyEventSink sink = new(client);

        sink.ReportMissedLock(LockAttemptKind.Relock);
        Task dispose = sink.DisposeAsync().AsTask();
        Assert.False(dispose.IsCompleted);

        client.Release();
        await dispose;

        Assert.Equal([PrivacySafeEventKind.MissedLock], client.Events);
    }

    [Fact]
    public async Task WorkstationLock_RecordsOnlyThePrivacySafeFactAndDisposeAwaitsIt()
    {
        RecordingClient client = new(blockWrites: true);
        DesktopPrivacyEventSink sink = new(client);

        sink.ReportWorkstationLocked();
        Task dispose = sink.DisposeAsync().AsTask();
        Assert.False(dispose.IsCompleted);

        client.Release();
        await dispose;

        Assert.Equal([PrivacySafeEventKind.WorkstationLocked], client.Events);
    }

    [Fact]
    public async Task ConfirmedTrayExit_RecordsDeliberateBypassBeforeShutdownContinues()
    {
        RecordingClient client = new(blockWrites: true);
        DesktopPrivacyEventSink sink = new(client);

        Task record = sink.ReportDeliberateBypassAsync().AsTask();
        Assert.False(record.IsCompleted);

        client.Release();
        await record;
        await sink.DisposeAsync();

        Assert.Equal([PrivacySafeEventKind.DeliberateBypass], client.Events);
    }

    [Theory]
    [InlineData(ProcessGateOutcomeKind.CloseRequested, true)]
    [InlineData(ProcessGateOutcomeKind.NoEligibleWindow, true)]
    [InlineData(ProcessGateOutcomeKind.Healthy, false)]
    [InlineData(ProcessGateOutcomeKind.Degraded, false)]
    [InlineData(ProcessGateOutcomeKind.TerminateAttempted, false)]
    public async Task ProcessOutcome_RecordsOnlyLateEntertainmentWithoutPayload(
        ProcessGateOutcomeKind kind,
        bool expected)
    {
        RecordingClient client = new();
        await using DesktopPrivacyEventSink sink = new(client);

        await sink.PublishAsync(new(kind, null, "ignored-code"));

        Assert.Equal(
            expected ? [PrivacySafeEventKind.LateNewEntertainment] : [],
            client.Events);
    }

    [Fact]
    public async Task RecordingFault_IsContainedAndPostDisposeCallbacksAreIgnored()
    {
        ThrowingClient client = new();
        DesktopPrivacyEventSink sink = new(client);

        sink.ReportMissedLock(LockAttemptKind.Initial);
        await sink.PublishAsync(new(
            ProcessGateOutcomeKind.CloseRequested,
            null,
            null));
        await sink.DisposeAsync();
        sink.ReportMissedLock(LockAttemptKind.Relock);

        Assert.Equal(2, client.CallCount);
    }

    private sealed class RecordingClient(bool blockWrites = false) : IDesktopPolicyClient
    {
        private readonly TaskCompletionSource _release = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public List<PrivacySafeEventKind> Events { get; } = [];

        public ValueTask<DesktopPolicyResult> GetPolicyAsync(
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(DesktopPolicyResult.FailOpen("not-used"));

        public async ValueTask<DesktopRecordEventResult> RecordEventAsync(
            PrivacySafeEventKind kind,
            CancellationToken cancellationToken = default)
        {
            Events.Add(kind);
            if (blockWrites)
            {
                await _release.Task.WaitAsync(cancellationToken);
            }

            return new(true, null);
        }

        public void Release() => _release.TrySetResult();
    }

    private sealed class ThrowingClient : IDesktopPolicyClient
    {
        public int CallCount { get; private set; }

        public ValueTask<DesktopPolicyResult> GetPolicyAsync(
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(DesktopPolicyResult.FailOpen("not-used"));

        public ValueTask<DesktopRecordEventResult> RecordEventAsync(
            PrivacySafeEventKind kind,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            throw new IOException("recording failed");
        }
    }
}
