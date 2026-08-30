using NightGate.Desktop;

namespace NightGate.Desktop.Tests;

public sealed class DesktopUserExperienceRuntimeTests
{
    [Fact]
    public async Task Polling_RecoversAfterInitialUnavailableResult()
    {
        RecoveringGateway gateway = new();
        UserExperienceViewModel viewModel = new(gateway);
        FourTickClock clock = new();
        DesktopUserExperienceRuntime runtime = new(
            viewModel,
            new ImmediateDispatcher(),
            clock,
            TimeSpan.FromSeconds(15));

        await runtime.StartAsync();
        await gateway.Recovered.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.True(viewModel.IsAvailable);
        Assert.Equal(2, gateway.StateReads);
        await runtime.StopAsync().WaitAsync(TimeSpan.FromSeconds(2));
        await runtime.DisposeAsync();
    }

    [Fact]
    public async Task Start_PerformsInitialRefreshAndNoticeClaimThenStopCancelsLoop()
    {
        CountingGateway gateway = new();
        UserExperienceViewModel viewModel = new(gateway);
        ControlledClock clock = new();
        DesktopUserExperienceRuntime runtime = new(
            viewModel,
            new ImmediateDispatcher(),
            clock,
            TimeSpan.FromSeconds(15));

        await runtime.StartAsync();
        await clock.DelayStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(1, gateway.StateReads);
        Assert.Equal(1, gateway.NoticeClaims);
        await runtime.StopAsync().WaitAsync(TimeSpan.FromSeconds(2));
        await runtime.DisposeAsync();
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

    private sealed class ControlledClock : IDesktopRuntimeClock
    {
        public TaskCompletionSource DelayStarted { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public DateTimeOffset Now => new(2026, 7, 14, 21, 0, 0, TimeSpan.FromHours(8));

        public TimeSpan MonotonicNow => TimeSpan.Zero;

        public ValueTask DelayAsync(
            TimeSpan delay,
            CancellationToken cancellationToken = default)
        {
            DelayStarted.TrySetResult();
            return new(Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken));
        }
    }

    private sealed class FourTickClock : IDesktopRuntimeClock
    {
        private int _delays;

        public DateTimeOffset Now =>
            new(2026, 7, 14, 21, 0, 0, TimeSpan.FromHours(8));

        public TimeSpan MonotonicNow => TimeSpan.Zero;

        public ValueTask DelayAsync(
            TimeSpan delay,
            CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref _delays) <= 4)
            {
                return ValueTask.CompletedTask;
            }

            return new(Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken));
        }
    }

    private sealed class RecoveringGateway : IUserExperienceGateway
    {
        public TaskCompletionSource Recovered { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public int StateReads { get; private set; }

        public ValueTask<DesktopUserStateResult> GetUserStateAsync(
            CancellationToken cancellationToken = default)
        {
            StateReads++;
            if (StateReads == 1)
            {
                return ValueTask.FromResult(DesktopUserStateResult.Unavailable("restart"));
            }

            Recovered.TrySetResult();
            return ValueTask.FromResult(new DesktopUserStateResult(true, null, State()));
        }

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

        private static DesktopUserStateDto State() => new(
            new(1, null, null, null, null, null, null),
            new(1, 0, false, false, false, 0, null),
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
    }

    private sealed class CountingGateway : IUserExperienceGateway
    {
        public int StateReads { get; private set; }

        public int NoticeClaims { get; private set; }

        public ValueTask<DesktopUserStateResult> GetUserStateAsync(
            CancellationToken cancellationToken = default)
        {
            StateReads++;
            return ValueTask.FromResult(DesktopUserStateResult.Unavailable("test"));
        }

        public ValueTask<DesktopNoticeClaimResult> ClaimDueNoticeAsync(
            CancellationToken cancellationToken = default)
        {
            NoticeClaims++;
            return ValueTask.FromResult(new DesktopNoticeClaimResult(false, null, null, null));
        }

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
}
