using NightGate.Core;
using NightGate.Service;

namespace NightGate.Service.Tests;

public sealed class SystemClockTests
{
    private static readonly WindowsBootEvent BootEvent = new(
        42,
        new DateTimeOffset(2026, 7, 7, 8, 0, 0, TimeSpan.Zero));

    [Fact]
    public void WindowsBootSessionIdProvider_CachesStableDeterministicIdentityForOneBoot()
    {
        SequenceBootEventSource source = new(
            BootEvent,
            new WindowsBootEvent(43, BootEvent.TimeCreatedUtc.AddHours(1)));
        WindowsBootSessionIdProvider provider = new(source);

        Guid? first = provider.Current;
        Guid? second = provider.Current;
        Guid? afterServiceRestart = new WindowsBootSessionIdProvider(
            new SequenceBootEventSource(BootEvent)).Current;
        Guid? nextBoot = new WindowsBootSessionIdProvider(
            new SequenceBootEventSource(
                new WindowsBootEvent(43, BootEvent.TimeCreatedUtc.AddHours(1)))).Current;

        Assert.NotNull(first);
        Assert.NotEqual(Guid.Empty, first);
        Assert.Equal(first, second);
        Assert.Equal(first, afterServiceRestart);
        Assert.NotEqual(first, nextBoot);
        Assert.Equal(1, source.ReadCount);
    }

    [Fact]
    public void WindowsBootSessionIdProvider_EventLogFailureDoesNotInventIdentity()
    {
        WindowsBootSessionIdProvider provider = new(new ThrowingBootEventSource());

        Assert.Null(provider.Current);
    }

    [Fact]
    public void SystemClock_UnavailableBootIdentityFailsObservationOpen()
    {
        SystemClock clock = new(
            new FixedBootSessionIdProvider(null),
            new FixedSystemUptimeSource(TimeSpan.FromHours(3)));

        IOException error = Assert.Throws<IOException>(() => clock.Observe());

        Assert.Equal("boot-session-unavailable", error.Message);
    }

    [Fact]
    public void SystemClock_UsesStableBootIdentityAndCurrentUptime()
    {
        Guid bootSessionId = Guid.Parse("33333333-3333-3333-3333-333333333333");
        MutableSystemUptimeSource uptime = new(TimeSpan.FromHours(3));
        SystemClock clock = new(new FixedBootSessionIdProvider(bootSessionId), uptime);

        ClockObservation first = clock.Observe();
        uptime.Value = TimeSpan.FromHours(4);
        ClockObservation second = clock.Observe();

        Assert.Equal(bootSessionId, first.BootSessionId);
        Assert.Equal(bootSessionId, second.BootSessionId);
        Assert.Equal(TimeSpan.FromHours(3), first.Uptime);
        Assert.Equal(TimeSpan.FromHours(4), second.Uptime);
    }

    private sealed class SequenceBootEventSource(params WindowsBootEvent?[] events) :
        IWindowsBootEventSource
    {
        private int _index;

        public int ReadCount { get; private set; }

        public WindowsBootEvent? ReadLatestBootEvent()
        {
            ReadCount++;
            int index = Math.Min(_index++, events.Length - 1);
            return events[index];
        }
    }

    private sealed class ThrowingBootEventSource : IWindowsBootEventSource
    {
        public WindowsBootEvent? ReadLatestBootEvent() => throw new IOException("event-log-failure");
    }

    private sealed class FixedBootSessionIdProvider(Guid? current) : IBootSessionIdProvider
    {
        public Guid? Current => current;
    }

    private sealed class FixedSystemUptimeSource(TimeSpan value) : ISystemUptimeSource
    {
        public TimeSpan GetUptime() => value;
    }

    private sealed class MutableSystemUptimeSource(TimeSpan value) : ISystemUptimeSource
    {
        public TimeSpan Value { get; set; } = value;

        public TimeSpan GetUptime() => Value;
    }
}
