using System.Windows.Interop;
using NightGate.Desktop;

namespace NightGate.Desktop.Tests;

public sealed class DesktopProductionAdapterTests
{
    [Fact]
    public void GuidEpochFactory_ReturnsNonReusedCanonicalEpochs()
    {
        GuidProcessObserverEpochFactory factory = new();

        string first = factory.CreateEpoch();
        string second = factory.CreateEpoch();

        Assert.NotEqual(first, second);
        Assert.Equal(first, Guid.ParseExact(first, "N").ToString("N"));
        Assert.Equal(second, Guid.ParseExact(second, "N").ToString("N"));
    }

    [Fact]
    public void InteractiveIdentityProvider_ReturnsOnlyCompleteCurrentEvidence()
    {
        WindowsCurrentInteractiveIdentityProvider available = new(
            new StubIdentityNative("S-1-5-21-42", 7));
        WindowsCurrentInteractiveIdentityProvider missingSid = new(
            new StubIdentityNative(null, 7));
        WindowsCurrentInteractiveIdentityProvider invalidSession = new(
            new StubIdentityNative("S-1-5-21-42", -1));

        Assert.Equal(
            new CurrentInteractiveIdentity("S-1-5-21-42", 7),
            available.Read());
        Assert.Null(missingSid.Read());
        Assert.Null(invalidSession.Read());
    }

    [Theory]
    [InlineData(0x7, 0, (int)CurrentSessionEventKind.Locked)]
    [InlineData(0x8, 0, (int)CurrentSessionEventKind.Unlocked)]
    [InlineData(0x5, 0, (int)CurrentSessionEventKind.Logon)]
    [InlineData(0x6, 0, (int)CurrentSessionEventKind.Logoff)]
    [InlineData(0x7, 1, -1)]
    [InlineData(0x4, 0, -1)]
    public void SessionSource_MapsOnlySupportedEventsForExactSession(
        int reason,
        int sessionOffset,
        int expectedKind)
    {
        ManualRuntimeClock clock = new();
        RecordingSessionNative native = new();
        using WindowsCurrentSessionEventSource source = new(
            expectedSessionId: 11,
            clock,
            native);
        List<CurrentSessionChangedEventArgs> events = [];
        source.SessionChanged += (_, args) => events.Add(args);

        clock.Monotonic = TimeSpan.FromSeconds(42);
        native.Raise(reason, 11 + sessionOffset);

        if (expectedKind < 0)
        {
            Assert.Empty(events);
        }
        else
        {
            CurrentSessionChangedEventArgs item = Assert.Single(events);
            Assert.Equal((CurrentSessionEventKind)expectedKind, item.Kind);
            Assert.Equal(TimeSpan.FromSeconds(42), item.MonotonicTimestamp);
        }
    }

    [Fact]
    public void SessionSource_DisposalUnsubscribesAndDisposesNativeSource()
    {
        ManualRuntimeClock clock = new();
        RecordingSessionNative native = new();
        WindowsCurrentSessionEventSource source = new(11, clock, native);
        int callbacks = 0;
        source.SessionChanged += (_, _) => callbacks++;

        source.Dispose();
        native.Raise(0x8, 11);

        Assert.Equal(0, callbacks);
        Assert.True(native.Disposed);
    }

    [Fact]
    public void SessionNative_RemoveHookFailureStillUnregistersWtsNotification()
    {
        ThrowingRemoveHookPlatform platform = new();
        WpfCurrentSessionNotificationNative native = new((nint)123, platform);

        Assert.Throws<IOException>(() => native.Dispose());

        Assert.Equal(1, platform.UnregisterCount);
    }

    [Theory]
    [InlineData(1, (int)DesktopPowerSource.Ac)]
    [InlineData(0, (int)DesktopPowerSource.Battery)]
    [InlineData(255, (int)DesktopPowerSource.Unknown)]
    public void SleepReader_ReturnsReadOnlyAcAndBatteryTimeouts(
        byte acLineStatus,
        int expectedSource)
    {
        WindowsSleepTimeoutReader reader = new(
            new StubSleepNative(true, acLineStatus, 1800, 900));

        SleepTimeoutSnapshot snapshot = Assert.IsType<SleepTimeoutSnapshot>(reader.Read());

        Assert.Equal((DesktopPowerSource)expectedSource, snapshot.ActiveSource);
        Assert.Equal(TimeSpan.FromMinutes(30), snapshot.AcTimeout);
        Assert.Equal(TimeSpan.FromMinutes(15), snapshot.BatteryTimeout);
    }

    [Fact]
    public void SleepReader_NativeFailureOrFaultReturnsNull()
    {
        Assert.Null(new WindowsSleepTimeoutReader(
            new StubSleepNative(false, 1, 0, 0)).Read());
        Assert.Null(new WindowsSleepTimeoutReader(new ThrowingSleepNative()).Read());
    }

    private sealed class StubIdentityNative(string? sid, int sessionId) :
        ICurrentInteractiveIdentityNative
    {
        public string? ReadCurrentUserSid() => sid;

        public bool TryReadCurrentProcessSessionId(out int value)
        {
            value = sessionId;
            return true;
        }
    }

    private sealed class RecordingSessionNative : ICurrentSessionNotificationNative
    {
        public event EventHandler<RawCurrentSessionEventArgs>? SessionChanged;

        public bool Disposed { get; private set; }

        public void Raise(int reason, int sessionId) =>
            SessionChanged?.Invoke(this, new(reason, sessionId));

        public void Dispose() => Disposed = true;
    }

    private sealed class ThrowingRemoveHookPlatform :
        IWpfCurrentSessionNotificationPlatform
    {
        public int UnregisterCount { get; private set; }

        public bool CheckAccess() => true;

        public void AddHook(HwndSourceHook hook)
        {
        }

        public void RemoveHook(HwndSourceHook hook) =>
            throw new IOException("remove-hook-failed");

        public bool Register(nint windowHandle) => true;

        public bool Unregister(nint windowHandle)
        {
            UnregisterCount++;
            return true;
        }
    }

    private sealed class StubSleepNative(
        bool succeeds,
        byte acLineStatus,
        uint acSeconds,
        uint batterySeconds) : IWindowsSleepTimeoutNative
    {
        public bool TryRead(
            out byte actualAcLineStatus,
            out uint actualAcSeconds,
            out uint actualBatterySeconds)
        {
            actualAcLineStatus = acLineStatus;
            actualAcSeconds = acSeconds;
            actualBatterySeconds = batterySeconds;
            return succeeds;
        }
    }

    private sealed class ThrowingSleepNative : IWindowsSleepTimeoutNative
    {
        public bool TryRead(
            out byte acLineStatus,
            out uint acSeconds,
            out uint batterySeconds) => throw new IOException();
    }

    private sealed class ManualRuntimeClock : IDesktopRuntimeClock
    {
        public DateTimeOffset Now { get; set; }

        public TimeSpan Monotonic { get; set; }

        public TimeSpan MonotonicNow => Monotonic;

        public ValueTask DelayAsync(
            TimeSpan delay,
            CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;
    }
}
