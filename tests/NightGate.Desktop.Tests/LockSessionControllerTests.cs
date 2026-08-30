using NightGate.Desktop;

namespace NightGate.Desktop.Tests;

public sealed class LockSessionControllerTests
{
    [Fact]
    public void LandingLocked_AttemptsTheInitialLockOnlyOncePerNight()
    {
        RecordingLocker locker = new();
        LockSessionController controller = new(
            locker,
            new RecordingOverlayPresenter(),
            new RecordingLockEventSink());

        controller.ObservePolicy(Policy(DesktopNightPhase.LandingLocked), TimeSpan.Zero);
        controller.ObservePolicy(Policy(DesktopNightPhase.LandingLocked), TimeSpan.FromSeconds(1));

        Assert.Equal(1, locker.CallCount);

        controller.ObservePolicy(
            Policy(DesktopNightPhase.LandingLocked, new DateOnly(2026, 7, 7)),
            TimeSpan.FromDays(1));

        Assert.Equal(2, locker.CallCount);
    }

    [Theory]
    [InlineData(DesktopNightPhase.Free)]
    [InlineData(DesktopNightPhase.LastStart)]
    [InlineData(DesktopNightPhase.Grace)]
    [InlineData(DesktopNightPhase.OverrideActive)]
    [InlineData(DesktopNightPhase.Morning)]
    public void NonLockingPhases_DoNotAttemptAnInitialLock(DesktopNightPhase phase)
    {
        RecordingLocker locker = new();
        LockSessionController controller = new(
            locker,
            new RecordingOverlayPresenter(),
            new RecordingLockEventSink());

        controller.ObservePolicy(Policy(phase), TimeSpan.Zero);

        Assert.Equal(0, locker.CallCount);
    }

    [Fact]
    public void FailedInitialLock_FailsOpenAndDoesNotRetryBeforeBackoff()
    {
        RecordingLocker locker = new() { Result = false };
        RecordingLockEventSink events = new();
        LockSessionController controller = new(
            locker,
            new RecordingOverlayPresenter(),
            events);

        controller.ObservePolicy(Policy(DesktopNightPhase.LandingLocked), TimeSpan.Zero);
        controller.ObservePolicy(Policy(DesktopNightPhase.LandingLocked), TimeSpan.FromSeconds(1));

        Assert.Equal(1, locker.CallCount);
        Assert.Equal([LockAttemptKind.Initial], events.Attempts);
    }

    [Fact]
    public void FailedInitialLock_RetriesTwiceAtThirtySecondIntervalsThenStops()
    {
        RecordingLocker locker = new() { Result = false };
        RecordingLockEventSink events = new();
        LockSessionController controller = new(
            locker,
            new RecordingOverlayPresenter(),
            events);

        controller.ObservePolicy(Policy(DesktopNightPhase.LandingLocked), TimeSpan.Zero);
        controller.Tick(TimeSpan.FromSeconds(29));
        controller.Tick(TimeSpan.FromSeconds(30));
        controller.Tick(TimeSpan.FromSeconds(59));
        controller.Tick(TimeSpan.FromSeconds(60));
        controller.Tick(TimeSpan.FromDays(1));

        Assert.Equal(3, locker.CallCount);
        Assert.Equal(
            [
                LockAttemptKind.Initial,
                LockAttemptKind.Initial,
                LockAttemptKind.Initial,
            ],
            events.Attempts);
    }

    [Fact]
    public void FailedInitialLock_DegradedPolicyCancelsPendingRetryFailOpen()
    {
        RecordingLocker locker = new() { Result = false };
        LockSessionController controller = new(
            locker,
            new RecordingOverlayPresenter(),
            new RecordingLockEventSink());

        controller.ObservePolicy(Policy(DesktopNightPhase.LandingLocked), TimeSpan.Zero);
        controller.ObservePolicy(
            Policy(DesktopNightPhase.LandingLocked, canEnforce: false),
            TimeSpan.FromSeconds(1));
        controller.Tick(TimeSpan.FromDays(1));

        Assert.Equal(1, locker.CallCount);
    }

    [Fact]
    public void ThrowingInitialLockAndEventSink_AreContainedWithoutImmediateRetry()
    {
        RecordingLocker locker = new() { Exception = new InvalidOperationException("native") };
        RecordingLockEventSink events = new() { Exception = new InvalidOperationException("event") };
        LockSessionController controller = new(
            locker,
            new RecordingOverlayPresenter(),
            events);

        controller.ObservePolicy(Policy(DesktopNightPhase.CoolingOff), TimeSpan.Zero);
        controller.ObservePolicy(Policy(DesktopNightPhase.CoolingOff), TimeSpan.FromSeconds(1));

        Assert.Equal(1, locker.CallCount);
        Assert.Equal([LockAttemptKind.Initial], events.Attempts);
    }

    [Fact]
    public void RestrictedUnlock_ShowsOneTenSecondEpisodeAndRelocksOnceAtExpiry()
    {
        RecordingLocker locker = new();
        RecordingOverlayPresenter overlay = new();
        LockSessionController controller = new(
            locker,
            overlay,
            new RecordingLockEventSink());
        controller.ObservePolicy(Policy(DesktopNightPhase.LandingLocked), TimeSpan.Zero);
        controller.OnSessionChanged(CurrentSessionEventKind.Locked, TimeSpan.Zero);

        controller.OnSessionChanged(CurrentSessionEventKind.Unlocked, TimeSpan.FromSeconds(3));
        controller.OnSessionChanged(CurrentSessionEventKind.Unlocked, TimeSpan.FromSeconds(8));
        controller.Tick(TimeSpan.FromSeconds(12));

        Assert.Single(overlay.Shown);
        Assert.Equal(TimeSpan.FromSeconds(10), overlay.Shown[0].Remaining);
        Assert.Equal(TimeSpan.FromSeconds(1), Assert.Single(overlay.Updated).Remaining);
        Assert.Equal(1, locker.CallCount);

        controller.Tick(TimeSpan.FromSeconds(13));
        controller.Tick(TimeSpan.FromSeconds(20));

        Assert.Equal(1, overlay.HideCount);
        Assert.Equal(2, locker.CallCount);
    }

    [Fact]
    public void OverrideRequestHold_RelocksAtTwentyFiveSecondHardLimit()
    {
        RecordingLocker locker = new();
        LockSessionController controller = new(
            locker,
            new RecordingOverlayPresenter(),
            new RecordingLockEventSink());
        controller.ObservePolicy(Policy(DesktopNightPhase.LandingLocked), TimeSpan.Zero);
        controller.OnSessionChanged(CurrentSessionEventKind.Unlocked, TimeSpan.FromSeconds(1));

        controller.OnOverrideRequestStarted(TimeSpan.FromSeconds(2));
        controller.Tick(TimeSpan.FromSeconds(26.999));
        Assert.Equal(1, locker.CallCount);

        controller.Tick(TimeSpan.FromSeconds(27));
        Assert.Equal(2, locker.CallCount);
    }

    [Fact]
    public void RejectedOverrideRequest_GivesFreshTenSecondsFromCompletion()
    {
        RecordingLocker locker = new();
        RecordingOverlayPresenter overlay = new();
        LockSessionController controller = new(
            locker,
            overlay,
            new RecordingLockEventSink());
        DesktopPolicyResult restricted = Policy(DesktopNightPhase.LandingLocked);
        controller.ObservePolicy(restricted, TimeSpan.Zero);
        controller.OnSessionChanged(CurrentSessionEventKind.Unlocked, TimeSpan.FromSeconds(1));
        controller.OnOverrideRequestStarted(TimeSpan.FromSeconds(2));

        controller.OnOverrideRequestCompleted(
            new(false, "teamRescueCooldownActive", null, restricted),
            TimeSpan.FromSeconds(8));
        controller.Tick(TimeSpan.FromSeconds(17.999));
        Assert.Equal(1, locker.CallCount);

        controller.Tick(TimeSpan.FromSeconds(18));
        Assert.Equal(2, locker.CallCount);
        Assert.Contains(overlay.Updated, item => item.Remaining == TimeSpan.FromSeconds(10));
    }

    [Theory]
    [InlineData(OverlayFailurePoint.Show)]
    [InlineData(OverlayFailurePoint.Update)]
    [InlineData(OverlayFailurePoint.Hide)]
    public void OverlayFailure_DoesNotCancelTheTenSecondRelockDeadline(
        OverlayFailurePoint failurePoint)
    {
        RecordingLocker locker = new();
        RecordingOverlayPresenter overlay = new()
        {
            FailurePoint = failurePoint,
        };
        LockSessionController controller = new(
            locker,
            overlay,
            new RecordingLockEventSink());
        controller.ObservePolicy(Policy(DesktopNightPhase.LandingLocked), TimeSpan.Zero);

        controller.OnSessionChanged(
            CurrentSessionEventKind.Unlocked,
            TimeSpan.FromSeconds(1));
        controller.Tick(TimeSpan.FromSeconds(6));
        controller.Tick(TimeSpan.FromSeconds(11));

        Assert.Equal(2, locker.CallCount);
    }

    [Fact]
    public void LockedSession_ReportsActualLockFactAndContainsSinkFailure()
    {
        RecordingLockEventSink events = new();
        LockSessionController controller = new(
            new RecordingLocker(),
            new RecordingOverlayPresenter(),
            events);

        controller.ObservePolicy(Policy(DesktopNightPhase.LandingLocked), TimeSpan.Zero);
        controller.OnSessionChanged(CurrentSessionEventKind.Locked, TimeSpan.FromSeconds(1));
        events.Exception = new InvalidOperationException("event");
        controller.OnSessionChanged(CurrentSessionEventKind.Locked, TimeSpan.FromSeconds(2));

        Assert.Equal(2, events.WorkstationLockCount);
    }

    [Fact]
    public void DuplicateUnlockAfterCompletedEpisode_DoesNotCreateALockStorm()
    {
        RecordingLocker locker = new();
        RecordingOverlayPresenter overlay = new();
        LockSessionController controller = new(
            locker,
            overlay,
            new RecordingLockEventSink());
        controller.ObservePolicy(Policy(DesktopNightPhase.LandingLocked), TimeSpan.Zero);
        controller.OnSessionChanged(CurrentSessionEventKind.Locked, TimeSpan.Zero);
        controller.OnSessionChanged(CurrentSessionEventKind.Unlocked, TimeSpan.FromSeconds(1));
        controller.Tick(TimeSpan.FromSeconds(11));

        controller.OnSessionChanged(CurrentSessionEventKind.Unlocked, TimeSpan.FromSeconds(12));
        controller.Tick(TimeSpan.FromSeconds(22));

        Assert.Single(overlay.Shown);
        Assert.Equal(2, locker.CallCount);
    }

    [Fact]
    public void AuthoritativeOverrideEnd_StartsOneNewRestrictedLockEntry()
    {
        RecordingLocker locker = new();
        LockSessionController controller = new(
            locker,
            new RecordingOverlayPresenter(),
            new RecordingLockEventSink());
        controller.ObservePolicy(Policy(DesktopNightPhase.LandingLocked), TimeSpan.Zero);

        controller.ObservePolicy(Policy(DesktopNightPhase.OverrideActive), TimeSpan.FromMinutes(1));
        controller.ObservePolicy(Policy(DesktopNightPhase.LandingLocked), TimeSpan.FromMinutes(21));
        controller.ObservePolicy(Policy(DesktopNightPhase.LandingLocked), TimeSpan.FromMinutes(22));

        Assert.Equal(2, locker.CallCount);
    }

    [Theory]
    [InlineData(DesktopNightPhase.OverrideActive)]
    [InlineData(DesktopNightPhase.Morning)]
    public void AuthoritativeNonRestrictedPolicy_CancelsPendingRelock(
        DesktopNightPhase cancelingPhase)
    {
        RecordingLocker locker = new();
        RecordingOverlayPresenter overlay = new();
        LockSessionController controller = new(
            locker,
            overlay,
            new RecordingLockEventSink());
        controller.ObservePolicy(Policy(DesktopNightPhase.LandingLocked), TimeSpan.Zero);
        controller.OnSessionChanged(CurrentSessionEventKind.Unlocked, TimeSpan.FromSeconds(1));

        controller.ObservePolicy(Policy(cancelingPhase), TimeSpan.FromSeconds(2));
        controller.Tick(TimeSpan.FromSeconds(20));

        Assert.Equal(1, overlay.HideCount);
        Assert.Equal(1, locker.CallCount);
    }

    [Fact]
    public void DegradedPolicy_CancelsPendingRelockFailOpen()
    {
        RecordingLocker locker = new();
        RecordingOverlayPresenter overlay = new();
        LockSessionController controller = new(
            locker,
            overlay,
            new RecordingLockEventSink());
        controller.ObservePolicy(Policy(DesktopNightPhase.LandingLocked), TimeSpan.Zero);
        controller.OnSessionChanged(CurrentSessionEventKind.Logon, TimeSpan.FromSeconds(1));

        controller.ObservePolicy(
            Policy(DesktopNightPhase.LandingLocked, canEnforce: false),
            TimeSpan.FromSeconds(2));
        controller.Tick(TimeSpan.FromSeconds(20));

        Assert.Equal(1, overlay.HideCount);
        Assert.Equal(1, locker.CallCount);
    }

    [Fact]
    public void DegradedPolicyThenSameNightRecovery_AttemptsOneNewInitialLock()
    {
        RecordingLocker locker = new();
        RecordingOverlayPresenter overlay = new();
        LockSessionController controller = new(
            locker,
            overlay,
            new RecordingLockEventSink());
        controller.ObservePolicy(Policy(DesktopNightPhase.LandingLocked), TimeSpan.Zero);
        controller.OnSessionChanged(CurrentSessionEventKind.Logon, TimeSpan.FromSeconds(1));

        controller.ObservePolicy(
            Policy(DesktopNightPhase.LandingLocked, canEnforce: false),
            TimeSpan.FromSeconds(2));
        controller.Tick(TimeSpan.FromSeconds(20));

        Assert.Equal(1, overlay.HideCount);
        Assert.Equal(1, locker.CallCount);

        controller.ObservePolicy(
            Policy(DesktopNightPhase.LandingLocked),
            TimeSpan.FromSeconds(21));
        controller.ObservePolicy(
            Policy(DesktopNightPhase.LandingLocked),
            TimeSpan.FromSeconds(22));

        Assert.Equal(2, locker.CallCount);
    }

    [Fact]
    public void CoolingOff_RemainsRestrictedAndRelocksAfterTenSeconds()
    {
        RecordingLocker locker = new();
        RecordingOverlayPresenter overlay = new();
        LockSessionController controller = new(
            locker,
            overlay,
            new RecordingLockEventSink());
        controller.ObservePolicy(Policy(DesktopNightPhase.CoolingOff), TimeSpan.Zero);
        controller.OnSessionChanged(CurrentSessionEventKind.Unlocked, TimeSpan.FromSeconds(4));

        controller.Tick(TimeSpan.FromSeconds(14));

        Assert.Single(overlay.Shown);
        Assert.Equal(2, locker.CallCount);
    }

    [Fact]
    public void MonotonicRollback_DoesNotExtendTheRelockDeadline()
    {
        RecordingLocker locker = new();
        RecordingOverlayPresenter overlay = new();
        LockSessionController controller = new(
            locker,
            overlay,
            new RecordingLockEventSink());
        controller.ObservePolicy(Policy(DesktopNightPhase.LandingLocked), TimeSpan.FromSeconds(100));
        controller.OnSessionChanged(CurrentSessionEventKind.Unlocked, TimeSpan.FromSeconds(101));

        controller.Tick(TimeSpan.FromSeconds(106));
        controller.Tick(TimeSpan.FromSeconds(90));
        controller.Tick(TimeSpan.FromSeconds(111));

        Assert.Equal(TimeSpan.FromSeconds(5), overlay.Updated[0].Remaining);
        Assert.Equal(TimeSpan.FromSeconds(5), overlay.Updated[1].Remaining);
        Assert.Equal(2, locker.CallCount);
    }

    [Fact]
    public void FailedRelock_RepeatedUnlockCannotBypassRetryBackoff()
    {
        RecordingLocker locker = new() { Results = new Queue<bool>([true, false]) };
        RecordingLockEventSink events = new();
        LockSessionController controller = new(
            locker,
            new RecordingOverlayPresenter(),
            events);
        controller.ObservePolicy(Policy(DesktopNightPhase.LandingLocked), TimeSpan.Zero);
        controller.OnSessionChanged(CurrentSessionEventKind.Unlocked, TimeSpan.FromSeconds(1));

        controller.Tick(TimeSpan.FromSeconds(11));
        controller.OnSessionChanged(CurrentSessionEventKind.Unlocked, TimeSpan.FromSeconds(12));
        controller.Tick(TimeSpan.FromSeconds(30));

        Assert.Equal(2, locker.CallCount);
        Assert.Equal([LockAttemptKind.Relock], events.Attempts);
    }

    [Fact]
    public void FailedRelock_RetriesTwiceAtThirtySecondIntervalsThenStops()
    {
        RecordingLocker locker = new()
        {
            Results = new Queue<bool>([true, false, false, false]),
        };
        RecordingLockEventSink events = new();
        LockSessionController controller = new(
            locker,
            new RecordingOverlayPresenter(),
            events);
        controller.ObservePolicy(Policy(DesktopNightPhase.LandingLocked), TimeSpan.Zero);
        controller.OnSessionChanged(CurrentSessionEventKind.Unlocked, TimeSpan.FromSeconds(1));

        controller.Tick(TimeSpan.FromSeconds(11));
        controller.Tick(TimeSpan.FromSeconds(40));
        controller.Tick(TimeSpan.FromSeconds(41));
        controller.Tick(TimeSpan.FromSeconds(70));
        controller.Tick(TimeSpan.FromSeconds(71));
        controller.Tick(TimeSpan.FromDays(1));

        Assert.Equal(4, locker.CallCount);
        Assert.Equal(
            [
                LockAttemptKind.Relock,
                LockAttemptKind.Relock,
                LockAttemptKind.Relock,
            ],
            events.Attempts);
    }

    private static DesktopPolicyResult Policy(
        DesktopNightPhase phase,
        DateOnly? nightDate = null,
        bool canEnforce = true)
    {
        DateOnly date = nightDate ?? new DateOnly(2026, 7, 6);
        DateTimeOffset protectedStart = new(
            date.ToDateTime(new TimeOnly(21, 0)),
            TimeSpan.Zero);
        DateTimeOffset lastStart = new(
            date.AddDays(1).ToDateTime(new TimeOnly(0, 5)),
            TimeSpan.Zero);
        DesktopNightWindowDto window = new(
            date,
            protectedStart,
            lastStart,
            lastStart.AddMinutes(35),
            lastStart.AddMinutes(55),
            lastStart.AddHours(8).AddMinutes(55));
        DesktopPolicySnapshotDto policy = new(
            lastStart.AddMinutes(35),
            phase,
            window,
            [],
            [],
            canEnforce,
            !canEnforce,
            null);
        DesktopServiceRuntimeStatusDto status = new(
            canEnforce,
            !canEnforce,
            canEnforce ? null : "test-degraded",
            policy);
        return new(canEnforce, !canEnforce, status.DegradationCode, status);
    }

    private sealed class RecordingLocker : IWorkstationLocker
    {
        public int CallCount { get; private set; }

        public bool Result { get; init; } = true;

        public Exception? Exception { get; init; }

        public Queue<bool>? Results { get; init; }

        public bool TryLock()
        {
            CallCount++;
            if (Exception is not null)
            {
                throw Exception;
            }

            return Results is { Count: > 0 } results ? results.Dequeue() : Result;
        }
    }

    private sealed class RecordingOverlayPresenter : IRestrictedOverlayPresenter
    {
        public List<RestrictedOverlayPresentation> Shown { get; } = [];

        public List<RestrictedOverlayPresentation> Updated { get; } = [];

        public int HideCount { get; private set; }

        public OverlayFailurePoint? FailurePoint { get; init; }

        public void Show(RestrictedOverlayPresentation presentation)
        {
            Shown.Add(presentation);
            ThrowIfConfigured(OverlayFailurePoint.Show);
        }

        public void Update(RestrictedOverlayPresentation presentation)
        {
            Updated.Add(presentation);
            ThrowIfConfigured(OverlayFailurePoint.Update);
        }

        public void Hide()
        {
            HideCount++;
            ThrowIfConfigured(OverlayFailurePoint.Hide);
        }

        private void ThrowIfConfigured(OverlayFailurePoint failurePoint)
        {
            if (FailurePoint == failurePoint)
            {
                throw new InvalidOperationException($"overlay-{failurePoint}");
            }
        }
    }

    public enum OverlayFailurePoint
    {
        Show,
        Update,
        Hide,
    }

    private sealed class RecordingLockEventSink : ILockWorkflowEventSink
    {
        public List<LockAttemptKind> Attempts { get; } = [];

        public Exception? Exception { get; set; }

        public int WorkstationLockCount { get; private set; }

        public void ReportMissedLock(LockAttemptKind attemptKind)
        {
            Attempts.Add(attemptKind);
            if (Exception is not null)
            {
                throw Exception;
            }
        }

        public void ReportWorkstationLocked()
        {
            WorkstationLockCount++;
            if (Exception is not null)
            {
                throw Exception;
            }
        }
    }
}
