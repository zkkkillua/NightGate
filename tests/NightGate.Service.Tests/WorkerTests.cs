using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using System.Text.Json;
using NightGate.Core;
using NightGate.Service;

namespace NightGate.Service.Tests;

public sealed class WorkerTests
{
    private const string ConfiguredUserSid = "S-1-5-21-1-2-3-1001";
    private const string ConfiguredUserSidArgument =
        "--NightGate:ConfiguredWindowsUserSid=" + ConfiguredUserSid;

    [Fact]
    public async Task LoopBoundaryFailure_IsCaughtAndPublishedAsDegradedFailOpenStatus()
    {
        ThrowingIteration iteration = new();
        RecordingStatusPublisher publisher = new();
        NightGateWorker worker = new(
            iteration,
            publisher,
            new BlockingLoopDelay(),
            NullLogger<NightGateWorker>.Instance);

        await worker.StartAsync(CancellationToken.None);
        ServiceRuntimeStatus status = await publisher.FirstStatus.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await worker.StopAsync(CancellationToken.None);

        Assert.Equal(1, iteration.ExecutionCount);
        Assert.True(status.IsDegraded);
        Assert.False(status.EnforcementEnabled);
        Assert.Equal("worker-loop-failure", status.DegradationCode);
    }

    [Fact]
    public async Task SuccessfulIteration_AcceptsTheNextConnectionWithoutFailureBackoff()
    {
        TwoStepBlockingIteration iteration = new();
        RecordingStatusPublisher publisher = new();
        RecordingLoopDelay delay = new();
        NightGateWorker worker = new(
            iteration,
            publisher,
            delay,
            NullLogger<NightGateWorker>.Instance);

        await worker.StartAsync(CancellationToken.None);
        await iteration.SecondIterationStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await worker.StopAsync(CancellationToken.None);

        Assert.Equal(2, iteration.ExecutionCount);
        Assert.Equal(0, delay.CallCount);
        Assert.False(publisher.FirstStatus.Task.IsCompleted);
    }

    [Fact]
    public async Task IterationFailure_UsesBackoffAndPublishesDegradedFailOpenStatus()
    {
        ThrowingIteration iteration = new();
        RecordingStatusPublisher publisher = new();
        SignalingBlockingLoopDelay delay = new();
        NightGateWorker worker = new(
            iteration,
            publisher,
            delay,
            NullLogger<NightGateWorker>.Instance);

        await worker.StartAsync(CancellationToken.None);
        ServiceRuntimeStatus status = await publisher.FirstStatus.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await delay.FirstDelay.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await worker.StopAsync(CancellationToken.None);

        Assert.Equal(1, iteration.ExecutionCount);
        Assert.Equal(1, delay.CallCount);
        Assert.True(status.IsDegraded);
        Assert.False(status.EnforcementEnabled);
        Assert.Equal("worker-loop-failure", status.DegradationCode);
    }

    [Fact]
    public async Task PolicyMaintenanceFailure_WaitsForCadenceBeforeRetrying()
    {
        AlwaysThrowingPolicyScheduler iteration = new();
        RecordingStatusPublisher publisher = new();
        BlockingPolicyMaintenanceDelay delay = new();
        NightPolicyWorker worker = new(
            iteration,
            publisher,
            delay,
            NullLogger<NightPolicyWorker>.Instance);

        await worker.StartAsync(CancellationToken.None);
        await delay.FirstDelay.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(1, iteration.ExecutionCount);
        Assert.Equal(1, delay.CallCount);

        await worker.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task PolicyMaintenanceDelayFailure_DoesNotImmediatelyRetryTheScheduler()
    {
        SignalingPolicyScheduler scheduler = new();
        RecordingStatusPublisher publisher = new();
        NightPolicyWorker worker = new(
            scheduler,
            publisher,
            new ThrowingPolicyMaintenanceDelay(),
            NullLogger<NightPolicyWorker>.Instance);

        await worker.StartAsync(CancellationToken.None);
        ServiceRuntimeStatus status = await publisher.FirstStatus.Task
            .WaitAsync(TimeSpan.FromSeconds(5));

        await Assert.ThrowsAsync<TimeoutException>(async () =>
            await scheduler.SecondIterationStarted.Task
                .WaitAsync(TimeSpan.FromMilliseconds(100)));

        await worker.StopAsync(CancellationToken.None);
        Assert.Equal(1, scheduler.ExecutionCount);
        Assert.True(status.IsDegraded);
        Assert.False(status.EnforcementEnabled);
        Assert.Equal("policy-maintenance-failure", status.DegradationCode);
    }

    [Fact]
    public void Host_IsConfiguredForWindowsServiceNameAndWorker()
    {
        using IHost host = NightGateHost.Create(
            [ConfiguredUserSidArgument],
            new FakeWindowsSidResolver());

        WindowsServiceLifetimeOptions options = host.Services
            .GetRequiredService<IOptions<WindowsServiceLifetimeOptions>>()
            .Value;
        IHostedService[] hostedServices = host.Services
            .GetServices<IHostedService>()
            .ToArray();
        IPolicyMaintenanceScheduler scheduler = host.Services
            .GetRequiredService<IPolicyMaintenanceScheduler>();
        IPolicyMaintenanceDelay maintenanceDelay = host.Services
            .GetRequiredService<IPolicyMaintenanceDelay>();

        Assert.Equal("NightGate.LocalService", NightGateHost.WindowsServiceName);
        Assert.Equal("NightGateService", NightGateHost.PipeName);
        Assert.Equal("NightGate.LocalService", options.ServiceName);
        Assert.Contains(hostedServices, service => service is NightGateWorker);
        Assert.Contains(hostedServices, service => service is NightPolicyWorker);
        Assert.IsType<PolicyMaintenanceScheduler>(scheduler);
        Assert.IsType<BoundaryAwarePolicyMaintenanceDelay>(maintenanceDelay);
    }

    [Fact]
    public void Host_UsesExplicitCanonicalUserAndCurrentServiceSidsForAuthorization()
    {
        FakeWindowsSidResolver resolver = new();
        using IHost host = NightGateHost.Create(
            [ConfiguredUserSidArgument],
            resolver);
        IPipePeerAuthorizer authorizer = host.Services.GetRequiredService<IPipePeerAuthorizer>();

        Assert.Empty(resolver.ResolvedAccounts);
        Assert.Equal(1, resolver.CurrentIdentityCallCount);
        Assert.True(authorizer.IsAuthorized(new(resolver.ConfiguredUserSid)));
        Assert.True(authorizer.IsAuthorized(new(resolver.CurrentServiceSid)));
        Assert.False(authorizer.IsAuthorized(new("DOMAIN\\configured")));
    }

    [Fact]
    public void Host_PassesValidatedConfiguredUserSidToSystemNamedPipeFactory()
    {
        using IHost host = NightGateHost.Create(
            [ConfiguredUserSidArgument],
            new FakeWindowsSidResolver());

        SystemNamedPipeServerFactory factory = Assert.IsType<SystemNamedPipeServerFactory>(
            host.Services.GetRequiredService<INamedPipeServerFactory>());

        Assert.Equal(ConfiguredUserSid, factory.ConfiguredUserSid);
    }

    [Fact]
    public void Host_RejectsMissingConfiguredDesktopUserSidWithoutEnvironmentFallback()
    {
        FakeWindowsSidResolver resolver = new();

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
            () => NightGateHost.Create([], resolver));

        Assert.Contains("ConfiguredWindowsUserSid", exception.Message, StringComparison.Ordinal);
        Assert.Empty(resolver.ResolvedAccounts);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-a-sid")]
    [InlineData("s-1-5-21-1-2-3-1001")]
    public void Host_RejectsBlankMalformedOrNonCanonicalConfiguredDesktopUserSid(string sid)
    {
        FakeWindowsSidResolver resolver = new();

        Assert.Throws<InvalidOperationException>(() => NightGateHost.Create(
            [$"--NightGate:ConfiguredWindowsUserSid={sid}"],
            resolver));

        Assert.Empty(resolver.ResolvedAccounts);
    }

    [Fact]
    public void Host_RejectsConfiguredDesktopUserSidEqualToServiceIdentity()
    {
        FakeWindowsSidResolver resolver = new();

        Assert.Throws<InvalidOperationException>(() => NightGateHost.Create(
            ["--NightGate:ConfiguredWindowsUserSid=S-1-5-19"],
            resolver));

        Assert.Empty(resolver.ResolvedAccounts);
    }

    [Fact]
    public void Host_DoesNotAcceptLegacyAccountNameConfiguration()
    {
        FakeWindowsSidResolver resolver = new();

        Assert.Throws<InvalidOperationException>(() => NightGateHost.Create(
            ["--NightGate:ConfiguredWindowsUser=DOMAIN\\configured"],
            resolver));

        Assert.Empty(resolver.ResolvedAccounts);
    }

    [Fact]
    public void Host_UsesPersistedActiveRuleSnapshotAndIgnoresLegacyConfigurationGrants()
    {
        string root = Path.Combine(Path.GetTempPath(), "NightGate", "host-game.exe");
        string appRules = JsonSerializer.Serialize(new[]
        {
            new
            {
                id = "host-game-id",
                rootExecutablePath = root,
                helperExecutablePaths = Array.Empty<string>(),
                category = "game",
                sessionMinutes = 35,
            },
        });
        using IHost host = NightGateHost.Create(
            [
                $"--NightGate:AppRules={appRules}",
                ConfiguredUserSidArgument,
                "--NightGate:AllowedProcessIdentifiers=browser.exe;arbitrary.exe",
            ],
            new FakeWindowsSidResolver());

        IAllowedProcessSnapshotProvider provider = host.Services
            .GetRequiredService<IAllowedProcessSnapshotProvider>();

        Assert.IsType<PersistedActiveRuleSnapshot>(provider);
        Assert.False(provider.GetSnapshotResult().IsAvailable);
        Assert.Same(
            provider,
            host.Services.GetRequiredService<IActiveRuleSnapshotPublisher>());
        Assert.IsType<ConfigurationConfiguredRuleProvider>(
            host.Services.GetRequiredService<IConfiguredRuleProvider>());
    }

    [Fact]
    public void Host_ResolvesProductionConfiguredSiteRuleProvider()
    {
        using IHost host = NightGateHost.Create(
            [ConfiguredUserSidArgument],
            new FakeWindowsSidResolver());

        IConfiguredSiteRuleProvider provider = host.Services
            .GetRequiredService<IConfiguredSiteRuleProvider>();

        Assert.IsType<ConfigurationConfiguredSiteRuleProvider>(provider);
    }

    [Fact]
    public async Task RuntimeStatus_DoesNotLetHealthyTickClearPriorFailureDegradation()
    {
        InMemoryServiceStatus status = new();
        await status.PublishAsync(ServiceRuntimeStatus.Degraded("pipe-failure"));

        await status.PublishAsync(new(true, false, null));

        Assert.True(status.Current.IsDegraded);
        Assert.False(status.Current.EnforcementEnabled);
        Assert.Equal("pipe-failure", status.Current.DegradationCode);
    }

    [Fact]
    public async Task RuntimeStatus_DoesNotLetDisabledPseudoHealthyStatusClearDegradation()
    {
        InMemoryServiceStatus status = new();
        await status.PublishAsync(ServiceRuntimeStatus.Degraded("pipe-failure"));

        await status.PublishAsync(new(false, false, null, SuccessfulPolicy()));

        Assert.True(status.Current.IsDegraded);
        Assert.False(status.Current.EnforcementEnabled);
        Assert.Equal("pipe-failure", status.Current.DegradationCode);
    }

    [Fact]
    public async Task RuntimeStatus_DoesNotLetDegradedPolicyClearPriorFailureDegradation()
    {
        InMemoryServiceStatus status = new();
        await status.PublishAsync(ServiceRuntimeStatus.Degraded("pipe-failure"));
        PolicySnapshot degradedPolicy = SuccessfulPolicy() with
        {
            EnforcementEnabled = false,
            IsDegraded = true,
        };

        await status.PublishAsync(new(true, false, null, degradedPolicy));

        Assert.True(status.Current.IsDegraded);
        Assert.False(status.Current.EnforcementEnabled);
        Assert.Equal("pipe-failure", status.Current.DegradationCode);
    }

    [Fact]
    public async Task RuntimeStatus_RevisionedRecoveryRejectsStaleSuccessfulMaintenance()
    {
        InMemoryServiceStatus status = new();
        await status.PublishAsync(new(true, false, null, SuccessfulPolicy()));
        ServiceRuntimeStatusSnapshot stale = status.ReadSnapshot();
        await status.PublishAsync(ServiceRuntimeStatus.Degraded("concurrent-failure"));

        bool recovered = await status.TryRecoverAsync(
            stale.Revision,
            new(true, false, null, SuccessfulPolicy()));

        Assert.False(recovered);
        Assert.True(status.Current.IsDegraded);
        Assert.Equal("concurrent-failure", status.Current.DegradationCode);
    }

    [Fact]
    public async Task RuntimeStatus_RevisionedRecoveryAcceptsMatchingSuccessfulMaintenance()
    {
        InMemoryServiceStatus status = new();
        ServiceRuntimeStatusSnapshot starting = status.ReadSnapshot();

        bool recovered = await status.TryRecoverAsync(
            starting.Revision,
            new(true, false, null, SuccessfulPolicy()));

        Assert.True(recovered);
        Assert.False(status.Current.IsDegraded);
        Assert.True(status.Current.EnforcementEnabled);
    }

    private static PolicySnapshot SuccessfulPolicy()
    {
        NightWindow window = ScheduleEvaluator.CreateWindow(
            new DateOnly(2026, 7, 6),
            ScheduleProfile.Default.Steps[0],
            TimeZoneInfo.Utc);
        return new(
            new DateTimeOffset(2026, 7, 6, 23, 0, 0, TimeSpan.Zero),
            NightPhase.Free,
            window,
            [],
            []);
    }

    private sealed class ThrowingIteration : IServiceLoopIteration
    {
        public int ExecutionCount { get; private set; }

        public ValueTask ExecuteAsync(CancellationToken cancellationToken = default)
        {
            ExecutionCount++;
            throw new InvalidOperationException("simulated loop failure");
        }
    }

    private sealed class RecordingStatusPublisher : IServiceStatusPublisher
    {
        public TaskCompletionSource<ServiceRuntimeStatus> FirstStatus { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ValueTask PublishAsync(
            ServiceRuntimeStatus status,
            CancellationToken cancellationToken = default)
        {
            FirstStatus.TrySetResult(status);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class TwoStepBlockingIteration : IServiceLoopIteration
    {
        public TaskCompletionSource SecondIterationStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int ExecutionCount { get; private set; }

        public async ValueTask ExecuteAsync(CancellationToken cancellationToken = default)
        {
            ExecutionCount++;
            if (ExecutionCount == 1)
            {
                return;
            }

            SecondIterationStarted.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }
    }

    private sealed class AlwaysThrowingPolicyScheduler : IPolicyMaintenanceScheduler
    {
        public int ExecutionCount { get; private set; }

        public void MarkDirty()
        {
        }

        public ValueTask RefreshAsync(
            bool force,
            CancellationToken cancellationToken = default)
        {
            ExecutionCount++;
            throw new InvalidOperationException("simulated policy failure");
        }
    }

    private sealed class SignalingPolicyScheduler : IPolicyMaintenanceScheduler
    {
        public TaskCompletionSource SecondIterationStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int ExecutionCount { get; private set; }

        public void MarkDirty()
        {
        }

        public ValueTask RefreshAsync(
            bool force,
            CancellationToken cancellationToken = default)
        {
            ExecutionCount++;
            if (ExecutionCount == 2)
            {
                SecondIterationStarted.TrySetResult();
            }

            return ValueTask.CompletedTask;
        }
    }

    private sealed class ThrowingPolicyMaintenanceDelay : IPolicyMaintenanceDelay
    {
        public ValueTask DelayAsync(CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("simulated delay failure");
    }

    private sealed class BlockingPolicyMaintenanceDelay : IPolicyMaintenanceDelay
    {
        public TaskCompletionSource FirstDelay { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int CallCount { get; private set; }

        public async ValueTask DelayAsync(CancellationToken cancellationToken = default)
        {
            CallCount++;
            FirstDelay.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }
    }

    private sealed class FakeWindowsSidResolver : IWindowsSidResolver
    {
        public string ConfiguredUserSid { get; } = "S-1-5-21-1-2-3-1001";

        public string CurrentServiceSid { get; } = "S-1-5-19";

        public List<string> ResolvedAccounts { get; } = [];

        public int CurrentIdentityCallCount { get; private set; }

        public string ResolveAccountSid(string accountName)
        {
            ResolvedAccounts.Add(accountName);
            return ConfiguredUserSid;
        }

        public string GetCurrentIdentitySid()
        {
            CurrentIdentityCallCount++;
            return CurrentServiceSid;
        }
    }

    private sealed class RecordingLoopDelay : IServiceLoopDelay
    {
        public int CallCount { get; private set; }

        public ValueTask DelayAsync(CancellationToken cancellationToken = default)
        {
            CallCount++;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class SignalingBlockingLoopDelay : IServiceLoopDelay
    {
        public TaskCompletionSource FirstDelay { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int CallCount { get; private set; }

        public async ValueTask DelayAsync(CancellationToken cancellationToken = default)
        {
            CallCount++;
            FirstDelay.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }
    }

    private sealed class BlockingLoopDelay : IServiceLoopDelay
    {
        public async ValueTask DelayAsync(CancellationToken cancellationToken = default) =>
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
    }
}
