using System.Collections.Immutable;
using System.Xml.Linq;
using NightGate.Desktop;

namespace NightGate.Desktop.Tests;

public sealed class OnboardingReliabilityRegressionTests
{
    [Fact]
    public async Task FirstUseDashboard_IsRequestedAfterDelayedServiceRecovery()
    {
        RecoveringGateway gateway = new();
        UserExperienceViewModel viewModel = new(gateway);
        EightTickClock clock = new();
        DesktopUserExperienceRuntime runtime = new(
            viewModel,
            new ImmediateDispatcher(),
            clock,
            TimeSpan.FromMilliseconds(1));
        TaskCompletionSource requested = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        int requestCount = 0;
        SubscribeToFirstUseDashboardRequest(runtime, () =>
        {
            Interlocked.Increment(ref requestCount);
            requested.TrySetResult();
        });

        await runtime.StartAsync();
        await requested.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await clock.Exhausted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.True(viewModel.IsAvailable);
        Assert.Equal(1, requestCount);
        await runtime.StopAsync().WaitAsync(TimeSpan.FromSeconds(2));
        await runtime.DisposeAsync();
    }

    [Fact]
    public async Task FirstUseDashboard_IsRequestedBeforeSlowGameDiscoveryCompletes()
    {
        AlwaysAvailableGateway gateway = new();
        BlockingGameDiscovery discovery = new();
        UserExperienceViewModel viewModel = new(gateway, null, discovery);
        DesktopUserExperienceRuntime runtime = new(
            viewModel,
            new ImmediateDispatcher(),
            new BlockingClock(),
            TimeSpan.FromSeconds(15));
        TaskCompletionSource requested = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        SubscribeToFirstUseDashboardRequest(runtime, () => requested.TrySetResult());

        Task start = runtime.StartAsync();
        await discovery.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await requested.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.False(start.IsCompleted);
        discovery.Complete();
        await start.WaitAsync(TimeSpan.FromSeconds(2));
        await runtime.StopAsync().WaitAsync(TimeSpan.FromSeconds(2));
        await runtime.DisposeAsync();
    }

    [Fact]
    public async Task SaveAndContinue_UsesCommandStateWhenReviewingCompletedStep()
    {
        UserExperienceViewModel viewModel = new(
            new AlwaysAvailableGateway(State(completedStep: 3)));
        await viewModel.RefreshAsync();
        viewModel.SelectOnboardingStep(2);

        Assert.True(viewModel.NextOnboardingCommand.CanExecute(null));

        XDocument wizard = XDocument.Load(Path.Combine(
            FindRepositoryRoot(),
            "src",
            "NightGate.Desktop",
            "Views",
            "OnboardingWizardView.xaml"));
        XElement button = Assert.Single(
            wizard.Descendants(),
            element => element.Name.LocalName == "Button"
                && element.Attributes().Any(attribute =>
                attribute.Name.LocalName == "Command"
                && attribute.Value.Contains(
                    "NextOnboardingCommand",
                    StringComparison.Ordinal)));

        Assert.Null(button.Attributes().SingleOrDefault(attribute =>
            attribute.Name.LocalName == "IsEnabled"));
    }

    private static void SubscribeToFirstUseDashboardRequest(
        DesktopUserExperienceRuntime runtime,
        Action requested) =>
        runtime.FirstUseDashboardRequested += (_, _) => requested();

    private static DesktopUserStateDto State(int completedStep = 0) => new(
        new(1, null, null, null, null, null, null),
        new(1, completedStep, false, false, false, 0, null),
        new([], [], null, null, null, null),
        new(
            new DateOnly(2026, 7, 9),
            new DateOnly(2026, 7, 15),
            0,
            0,
            0,
            0,
            null,
            null,
            new(0, 0, 0, 0, 0, 0)),
        new DateOnly(2026, 7, 14),
        null,
        new("missing", false, false, null, null));

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? current = new(AppContext.BaseDirectory);
        while (current is not null
            && !File.Exists(Path.Combine(current.FullName, "NightGate.slnx")))
        {
            current = current.Parent;
        }

        return current?.FullName
            ?? throw new DirectoryNotFoundException("Could not locate NightGate.slnx.");
    }

    private sealed class ImmediateDispatcher : IDesktopUiDispatcher
    {
        public ValueTask InvokeAsync(
            Action action,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            action();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class EightTickClock : IDesktopRuntimeClock
    {
        private int _delays;

        public TaskCompletionSource Exhausted { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public DateTimeOffset Now =>
            new(2026, 7, 14, 21, 0, 0, TimeSpan.FromHours(8));

        public TimeSpan MonotonicNow => TimeSpan.Zero;

        public ValueTask DelayAsync(
            TimeSpan delay,
            CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref _delays) <= 8)
            {
                return ValueTask.CompletedTask;
            }

            Exhausted.TrySetResult();
            return new(Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken));
        }
    }

    private sealed class BlockingClock : IDesktopRuntimeClock
    {
        public DateTimeOffset Now =>
            new(2026, 7, 14, 21, 0, 0, TimeSpan.FromHours(8));

        public TimeSpan MonotonicNow => TimeSpan.Zero;

        public ValueTask DelayAsync(
            TimeSpan delay,
            CancellationToken cancellationToken = default) =>
            new(Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken));
    }

    private sealed class BlockingGameDiscovery : IGameDiscovery
    {
        private readonly TaskCompletionSource<GameDiscoverySnapshot> _completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Started { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public ValueTask<GameDiscoverySnapshot> DiscoverAsync(
            CancellationToken cancellationToken = default)
        {
            Started.TrySetResult();
            return new(_completion.Task.WaitAsync(cancellationToken));
        }

        public void Complete() => _completion.TrySetResult(new(
            ImmutableArray<DiscoveredGame>.Empty,
            ImmutableArray<GameDiscoverySourceStatus>.Empty));
    }

    private abstract class GatewayBase : IUserExperienceGateway
    {
        public abstract ValueTask<DesktopUserStateResult> GetUserStateAsync(
            CancellationToken cancellationToken = default);

        public ValueTask<DesktopNoticeClaimResult> ClaimDueNoticeAsync(
            CancellationToken cancellationToken = default) => ValueTask.FromResult(
                new DesktopNoticeClaimResult(false, null, null, null));

        public ValueTask<DesktopOnboardingMutationResult> CompleteOnboardingStepAsync(
            DesktopOnboardingStepRequest request,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public ValueTask<DesktopRuleSettingsMutationResult> SaveRuleSettingsAsync(
            IReadOnlyList<DesktopAppRuleDraft> appRules,
            IReadOnlyList<string> siteDomains,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public ValueTask<DesktopSelfReportMutationResult> SaveNightSelfReportAsync(
            DateOnly nightDate,
            bool? phoneOutOfReach,
            bool? wakeWithinWindow,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public ValueTask<DesktopIPhoneProgressionResult> ConfirmIPhoneProgressionAsync(
            int step,
            DesktopIPhoneChecklist checklist,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public ValueTask<DesktopClearHistoryResult> ClearHistoryAsync(
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class RecoveringGateway : GatewayBase
    {
        private int _reads;

        public override ValueTask<DesktopUserStateResult> GetUserStateAsync(
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(Interlocked.Increment(ref _reads) == 1
                ? DesktopUserStateResult.Unavailable("service-starting")
                : new DesktopUserStateResult(true, null, State()));
    }

    private sealed class AlwaysAvailableGateway : GatewayBase
    {
        private readonly DesktopUserStateDto _state;

        public AlwaysAvailableGateway(DesktopUserStateDto? state = null)
        {
            _state = state ?? State();
        }

        public override ValueTask<DesktopUserStateResult> GetUserStateAsync(
            CancellationToken cancellationToken = default) => ValueTask.FromResult(
                new DesktopUserStateResult(true, null, _state));
    }
}
