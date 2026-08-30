using NightGate.Core;
using NightGate.Service;

namespace NightGate.Service.Tests;

public sealed class DesktopSessionLeaseTests
{
    private const string SessionId = "0123456789abcdef0123456789abcdef";
    private static readonly DateTimeOffset Now =
        new(2026, 8, 26, 16, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task ChromePolicy_IsFailOpenBeforeDesktopStartsAndAfterConfirmedExit()
    {
        MutableTimeProvider time = new();
        DesktopSessionLease lease = new(time);
        InMemoryServiceStatus status = new();
        await status.PublishAsync(HealthyStatus());
        using TempDatabase database = new();
        SqliteNightGateRepository repository = new(database.Path);
        NightGateProtocolCommandHandler handler = new(
            repository,
            repository,
            repository,
            status,
            status,
            new OverridePolicy(new EmptyAllowedProcesses()),
            new NightMutationGate(),
            new FixedClock(Now),
            desktopSessionLease: lease);

        ProtocolCommandResult beforeStart =
            await handler.ExecuteAsync(new GetPolicyCommand());
        ProtocolCommandResult started = await handler.ExecuteAsync(
            new GetDesktopPolicyCommand(SessionId));
        ProtocolCommandResult whileRunning =
            await handler.ExecuteAsync(new GetPolicyCommand());
        ProtocolCommandResult ended = await handler.ExecuteAsync(
            new EndDesktopSessionCommand(SessionId));
        ProtocolCommandResult afterExit =
            await handler.ExecuteAsync(new GetPolicyCommand());

        AssertFailOpen(beforeStart, "desktop-session-inactive");
        Assert.True(started.Payload.GetProperty("enforcementEnabled").GetBoolean());
        Assert.True(whileRunning.Payload.GetProperty("enforcementEnabled").GetBoolean());
        Assert.True(ended.Payload.GetProperty("accepted").GetBoolean());
        Assert.Equal(
            ["accepted"],
            ended.Payload.EnumerateObject().Select(property => property.Name));
        AssertFailOpen(afterExit, "desktop-session-inactive");
    }

    [Fact]
    public async Task ChromePolicy_FailsOpenWhenDesktopProcessStopsRenewingItsLease()
    {
        MutableTimeProvider time = new();
        DesktopSessionLease lease = new(time);
        InMemoryServiceStatus status = new();
        await status.PublishAsync(HealthyStatus());
        using TempDatabase database = new();
        SqliteNightGateRepository repository = new(database.Path);
        NightGateProtocolCommandHandler handler = new(
            repository,
            repository,
            repository,
            status,
            status,
            new OverridePolicy(new EmptyAllowedProcesses()),
            new NightMutationGate(),
            new FixedClock(Now),
            desktopSessionLease: lease);

        _ = await handler.ExecuteAsync(new GetDesktopPolicyCommand(SessionId));
        time.Advance(DesktopSessionLease.Lifetime + TimeSpan.FromMilliseconds(1));

        ProtocolCommandResult afterCrash =
            await handler.ExecuteAsync(new GetPolicyCommand());

        AssertFailOpen(afterCrash, "desktop-session-expired");
    }

    [Fact]
    public async Task ChromePolicy_ThatStartedBeforeDesktopExitCannotReturnRestrictionAfterExit()
    {
        MutableTimeProvider time = new();
        DesktopSessionLease lease = new(time);
        Assert.True(lease.Renew(SessionId).IsActive);
        InMemoryServiceStatus status = new();
        await status.PublishAsync(HealthyStatus());
        using TempDatabase database = new();
        SqliteNightGateRepository repository = new(database.Path);
        BlockingPolicyMaintenanceScheduler scheduler = new();
        NightGateProtocolCommandHandler handler = new(
            repository,
            repository,
            repository,
            status,
            status,
            new OverridePolicy(new EmptyAllowedProcesses()),
            new NightMutationGate(),
            new FixedClock(Now),
            policyMaintenanceScheduler: scheduler,
            desktopSessionLease: lease);

        Task<ProtocolCommandResult> pendingChromePolicy = handler
            .ExecuteAsync(new GetPolicyCommand())
            .AsTask();
        await scheduler.WaitUntilEnteredAsync();
        ProtocolCommandResult ended = await handler.ExecuteAsync(
            new EndDesktopSessionCommand(SessionId));
        scheduler.Release();

        ProtocolCommandResult afterExit = await pendingChromePolicy;

        Assert.True(ended.Payload.GetProperty("accepted").GetBoolean());
        AssertFailOpen(afterExit, "desktop-session-inactive");
    }

    [Fact]
    public void Lease_UsesMonotonicElapsedTimeRatherThanWallClock()
    {
        MutableTimeProvider time = new();
        DesktopSessionLease lease = new(time);

        Assert.True(lease.Renew(SessionId).IsActive);
        time.SetUtcNow(time.GetUtcNow().AddDays(-30));
        time.Advance(DesktopSessionLease.Lifetime - TimeSpan.FromMilliseconds(1));

        Assert.True(lease.Observe().IsActive);

        time.Advance(TimeSpan.FromMilliseconds(1));

        Assert.Equal(DesktopSessionLeaseState.Expired, lease.Observe().State);
    }

    [Fact]
    public void Lease_RenewalExtendsTheLifetimeFromTheLatestHeartbeat()
    {
        MutableTimeProvider time = new();
        DesktopSessionLease lease = new(time);
        _ = lease.Renew(SessionId);
        time.Advance(DesktopSessionLease.Lifetime - TimeSpan.FromSeconds(1));

        Assert.True(lease.Renew(SessionId).IsActive);
        time.Advance(TimeSpan.FromSeconds(2));

        Assert.True(lease.Observe().IsActive);
    }

    [Fact]
    public async Task EndedSessionCannotBeRevivedButDifferentSessionCanStart()
    {
        const string replacementSessionId = "fedcba9876543210fedcba9876543210";
        MutableTimeProvider time = new();
        DesktopSessionLease lease = new(time);
        _ = lease.Renew(SessionId);

        Task[] renewals = Enumerable.Range(0, 32)
            .Select(_ => Task.Run(() => lease.Renew(SessionId)))
            .Append(Task.Run(() =>
            {
                _ = lease.End(SessionId);
                return default(DesktopSessionLeaseObservation);
            }))
            .ToArray();
        await Task.WhenAll(renewals);

        Assert.Equal(
            DesktopSessionLeaseState.Retired,
            lease.Renew(SessionId).State);
        Assert.Equal(DesktopSessionLeaseState.Missing, lease.Observe().State);
        Assert.True(lease.Renew(replacementSessionId).IsActive);
        Assert.Equal(replacementSessionId, lease.Observe().SessionId);
    }

    [Fact]
    public async Task RetiredSessionExitCannotEndItsReplacement()
    {
        const string replacementSessionId = "fedcba9876543210fedcba9876543210";
        MutableTimeProvider time = new();
        DesktopSessionLease lease = new(time);
        InMemoryServiceStatus status = new();
        await status.PublishAsync(HealthyStatus());
        using TempDatabase database = new();
        SqliteNightGateRepository repository = new(database.Path);
        NightGateProtocolCommandHandler handler = Handler(
            repository,
            status,
            lease);

        _ = await handler.ExecuteAsync(new GetDesktopPolicyCommand(SessionId));
        ProtocolCommandResult replacement = await handler.ExecuteAsync(
            new GetDesktopPolicyCommand(replacementSessionId));
        ProtocolCommandResult staleExit = await handler.ExecuteAsync(
            new EndDesktopSessionCommand(SessionId));
        ProtocolCommandResult chromePolicy = await handler.ExecuteAsync(
            new GetPolicyCommand());
        ProtocolCommandResult staleRenewal = await handler.ExecuteAsync(
            new GetDesktopPolicyCommand(SessionId));

        Assert.True(replacement.Payload.GetProperty("enforcementEnabled").GetBoolean());
        Assert.False(staleExit.Payload.GetProperty("accepted").GetBoolean());
        Assert.Equal("sessionMismatch", staleExit.Payload.GetProperty("error").GetString());
        Assert.Equal(
            ["accepted", "error"],
            staleExit.Payload.EnumerateObject().Select(property => property.Name));
        Assert.True(chromePolicy.Payload.GetProperty("enforcementEnabled").GetBoolean());
        AssertFailOpen(staleRenewal, "desktop-session-retired");
        Assert.Equal(replacementSessionId, lease.Observe().SessionId);
    }

    [Fact]
    public void RetiredSessionCannotBeRevivedAfterManyLaterDesktopRestarts()
    {
        DesktopSessionLease lease = new(new MutableTimeProvider());
        Assert.True(lease.Renew(SessionId).IsActive);
        string latestSessionId = string.Empty;
        for (int index = 1; index <= 65; index++)
        {
            latestSessionId = index.ToString("x32");
            Assert.True(lease.Renew(latestSessionId).IsActive);
        }

        Assert.Equal(DesktopSessionLeaseState.Retired, lease.Renew(SessionId).State);
        Assert.Equal(latestSessionId, lease.Observe().SessionId);
    }

    [Fact]
    public async Task DirectInvalidSessionCommandCannotActivateTheLease()
    {
        MutableTimeProvider time = new();
        DesktopSessionLease lease = new(time);
        InMemoryServiceStatus status = new();
        await status.PublishAsync(HealthyStatus());
        using TempDatabase database = new();
        SqliteNightGateRepository repository = new(database.Path);
        NightGateProtocolCommandHandler handler = Handler(repository, status, lease);

        ProtocolCommandResult result = await handler.ExecuteAsync(
            new GetDesktopPolicyCommand("0123456789ABCDEF0123456789ABCDEF"));

        AssertFailOpen(result, "desktop-session-invalid");
        Assert.Equal(DesktopSessionLeaseState.Missing, lease.Observe().State);
    }

    [Fact]
    public async Task InactiveChromeCommandsAreRejectedWithoutRepositoryWritesOrGlobalDegradation()
    {
        MutableTimeProvider time = new();
        DesktopSessionLease lease = new(time);
        InMemoryServiceStatus status = new();
        ServiceRuntimeStatus healthy = HealthyStatus();
        await status.PublishAsync(healthy);
        using TempDatabase database = new();
        SqliteNightGateRepository repository = new(database.Path);
        CountingBrowserEventRepository browserEvents = new();
        CountingChromeHealthRepository chromeHealth = new();
        NightGateProtocolCommandHandler handler = Handler(
            repository,
            status,
            lease,
            browserEvents,
            chromeHealth);

        ProtocolCommandResult browser = await handler.ExecuteAsync(
            new RecordBrowserEventCommand(new(
                Now,
                BrowserEventType.NavigationBlocked,
                BrowserSiteCategory.Video)));
        ProtocolCommandResult health = await handler.ExecuteAsync(
            new RecordChromeHealthCommand(
                ChromeProtectionHealth.ExpectedExtensionId,
                ChromeProtectionHealth.MinimumCompatibleExtensionVersion,
                new string('a', 64),
                1,
                true,
                true));

        Assert.Equal(StorageMode.Degraded, browser.Mode);
        Assert.Equal(StorageMode.Degraded, health.Mode);
        AssertRejectedChromeWrite(browser);
        AssertRejectedChromeWrite(health);
        Assert.Equal(0, browserEvents.CallCount);
        Assert.Equal(0, chromeHealth.CallCount);
        Assert.Equal(healthy, status.Current);
    }

    [Fact]
    public async Task ExpiredChromeCommandsAreRejectedWithoutRepositoryWritesOrGlobalDegradation()
    {
        MutableTimeProvider time = new();
        DesktopSessionLease lease = new(time);
        InMemoryServiceStatus status = new();
        ServiceRuntimeStatus healthy = HealthyStatus();
        await status.PublishAsync(healthy);
        using TempDatabase database = new();
        SqliteNightGateRepository repository = new(database.Path);
        CountingBrowserEventRepository browserEvents = new();
        CountingChromeHealthRepository chromeHealth = new();
        NightGateProtocolCommandHandler handler = Handler(
            repository,
            status,
            lease,
            browserEvents,
            chromeHealth);
        _ = await handler.ExecuteAsync(new GetDesktopPolicyCommand(SessionId));
        time.Advance(DesktopSessionLease.Lifetime);

        ProtocolCommandResult browser = await handler.ExecuteAsync(
            new RecordBrowserEventCommand(new(
                Now,
                BrowserEventType.NavigationBlocked,
                BrowserSiteCategory.Video)));
        ProtocolCommandResult health = await handler.ExecuteAsync(
            new RecordChromeHealthCommand(
                ChromeProtectionHealth.ExpectedExtensionId,
                ChromeProtectionHealth.MinimumCompatibleExtensionVersion,
                new string('a', 64),
                1,
                true,
                true));

        Assert.Equal(StorageMode.Degraded, browser.Mode);
        Assert.Equal(StorageMode.Degraded, health.Mode);
        AssertRejectedChromeWrite(browser);
        AssertRejectedChromeWrite(health);
        Assert.Equal(0, browserEvents.CallCount);
        Assert.Equal(0, chromeHealth.CallCount);
        Assert.Equal(healthy, status.Current);
    }

    private static NightGateProtocolCommandHandler Handler(
        SqliteNightGateRepository repository,
        InMemoryServiceStatus status,
        DesktopSessionLease lease,
        IBrowserEventRepository? browserEventRepository = null,
        IChromeProtectionHealthRepository? chromeProtectionHealthRepository = null) =>
        new(
            repository,
            repository,
            repository,
            status,
            status,
            new OverridePolicy(new EmptyAllowedProcesses()),
            new NightMutationGate(),
            new FixedClock(Now),
            browserEventRepository: browserEventRepository,
            chromeProtectionHealthRepository: chromeProtectionHealthRepository,
            desktopSessionLease: lease);

    private static void AssertFailOpen(ProtocolCommandResult result, string code)
    {
        Assert.Equal(StorageMode.Degraded, result.Mode);
        Assert.False(result.Payload.GetProperty("enforcementEnabled").GetBoolean());
        Assert.True(result.Payload.GetProperty("isDegraded").GetBoolean());
        Assert.Equal(code, result.Payload.GetProperty("degradationCode").GetString());
        Assert.Equal(System.Text.Json.JsonValueKind.Null,
            result.Payload.GetProperty("policy").ValueKind);
    }

    private static void AssertRejectedChromeWrite(ProtocolCommandResult result)
    {
        Assert.Equal(
            ["status"],
            result.Payload.EnumerateObject().Select(property => property.Name));
        Assert.Equal("degraded", result.Payload.GetProperty("status").GetString());
    }

    private static ServiceRuntimeStatus HealthyStatus()
    {
        NightWindow window = ScheduleEvaluator.CreateWindow(
            new DateOnly(2026, 8, 26),
            ScheduleProfile.Default.Steps[0],
            TimeZoneInfo.Utc);
        PolicySnapshot policy = new(
            Now,
            NightPhase.LandingLocked,
            window,
            [],
            [new SiteRule("video.example")]);
        return new(true, false, null, policy);
    }

    private sealed class MutableTimeProvider : TimeProvider
    {
        private long _timestamp;
        private DateTimeOffset _utcNow = Now;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp() => _timestamp;

        public override DateTimeOffset GetUtcNow() => _utcNow;

        public void Advance(TimeSpan duration) => _timestamp += duration.Ticks;

        public void SetUtcNow(DateTimeOffset value) => _utcNow = value;
    }

    private sealed class CountingBrowserEventRepository : IBrowserEventRepository
    {
        public int CallCount { get; private set; }

        public ValueTask<StorageWriteResult> RecordBrowserEventAsync(
            BrowserPrivacyEvent browserEvent,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            return ValueTask.FromResult(StorageWriteResult.Success);
        }

        public ValueTask<StorageWriteResult> SaveLateNewEntertainmentWithBrowserEventAsync(
            NightState state,
            BrowserPrivacyEvent browserEvent,
            long? expectedVersion = null,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            return ValueTask.FromResult(StorageWriteResult.Success);
        }
    }

    private sealed class CountingChromeHealthRepository : IChromeProtectionHealthRepository
    {
        public int CallCount { get; private set; }

        public ValueTask<StorageResult<ChromeProtectionHealth?>> ReadChromeProtectionHealthAsync(
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            return ValueTask.FromResult(new StorageResult<ChromeProtectionHealth?>(
                StorageMode.Success,
                null));
        }

        public ValueTask<StorageWriteResult> SaveChromeProtectionHealthAsync(
            ChromeProtectionHealth health,
            long? expectedVersion = null,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            return ValueTask.FromResult(StorageWriteResult.Success);
        }
    }

    private sealed class BlockingPolicyMaintenanceScheduler : IPolicyMaintenanceScheduler
    {
        private readonly TaskCompletionSource _entered = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public void MarkDirty()
        {
        }

        public async ValueTask RefreshAsync(
            bool force,
            CancellationToken cancellationToken = default)
        {
            _entered.TrySetResult();
            await _release.Task.WaitAsync(cancellationToken);
        }

        public Task WaitUntilEnteredAsync() => _entered.Task;

        public void Release() => _release.TrySetResult();
    }

    private sealed class EmptyAllowedProcesses : IAllowedProcessSnapshotProvider
    {
        public System.Collections.Immutable.ImmutableArray<string> GetSnapshot() => [];
    }

    private sealed class FixedClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow => now;
    }

    private sealed class TempDatabase : IDisposable
    {
        public TempDatabase()
        {
            DirectoryPath = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "NightGate.Service.Tests",
                Guid.NewGuid().ToString("N"));
            Path = System.IO.Path.Combine(DirectoryPath, "state.db");
        }

        public string DirectoryPath { get; }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(DirectoryPath))
            {
                Directory.Delete(DirectoryPath, true);
            }
        }
    }
}
