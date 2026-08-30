namespace NightGate.Core;

public sealed class NightStateCoordinator(
    INightStateRepository repository,
    INightMutationGate? mutationGate = null)
{
    private readonly INightMutationGate _mutationGate = mutationGate ?? NoOpNightMutationGate.Instance;

    public ValueTask<CoordinatorObservation> ObserveAsync(
        NightWindow window,
        NightPhase observedBasePhase,
        DateTimeOffset observedAtUtc,
        CancellationToken cancellationToken = default) => ObserveAsync(
            window,
            observedBasePhase,
            new ClockObservation(observedAtUtc),
            cancellationToken);

    public async ValueTask<CoordinatorObservation> ObserveAsync(
        NightWindow window,
        NightPhase observedBasePhase,
        ClockObservation observedTime,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(window);
        EnsureBasePhase(observedBasePhase);
        ScheduledCoordinatorObservation scheduled = await ObserveCoreAsync(
            observedTime,
            (_, _) => new(window, observedBasePhase, null),
            preserveOnRawRollback: true,
            cancellationToken).ConfigureAwait(false);
        return scheduled.Observation;
    }

    public ValueTask<ScheduledCoordinatorObservation> ObserveScheduleAsync(
        ScheduleStep step,
        TimeZoneInfo timeZone,
        ClockObservation observedTime,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(step);
        ArgumentNullException.ThrowIfNull(timeZone);
        string currentTimeZoneSerialized = NightScheduleTimeZone.Capture(timeZone);
        return ObserveCoreAsync(
            observedTime,
            (instant, state) =>
            {
                TimeZoneInfo effectiveTimeZone = NightScheduleTimeZone.ResolveForActiveNight(
                    state,
                    timeZone);
                NightWindow window = ScheduleEvaluator.CreateWindowForInstant(
                    instant,
                    step,
                    effectiveTimeZone);
                NightPhase phase = ScheduleEvaluator.EvaluatePhase(
                    instant,
                    step,
                    effectiveTimeZone);
                string serialized = state is
                    { IsClosed: false, ScheduleTimeZoneSerialized: { } pinned }
                        ? pinned
                        : currentTimeZoneSerialized;
                return new ScheduleEvaluation(window, phase, serialized);
            },
            preserveOnRawRollback: false,
            cancellationToken);
    }

    private async ValueTask<ScheduledCoordinatorObservation> ObserveCoreAsync(
        ClockObservation observedTime,
        Func<DateTimeOffset, NightState?, ScheduleEvaluation> evaluateSchedule,
        bool preserveOnRawRollback,
        CancellationToken cancellationToken)
    {
        using IDisposable mutationLease = await _mutationGate
            .EnterAsync(cancellationToken)
            .ConfigureAwait(false);

        while (true)
        {
            StorageResult<NightState?> read = await repository
                .ReadActiveStateAsync(cancellationToken)
                .ConfigureAwait(false);
            if (read.IsDegraded)
            {
                ScheduleEvaluation degraded = evaluateSchedule(observedTime.UtcNow, null);
                EnsureBasePhase(degraded.Phase);
                return Scheduled(
                    Degraded(degraded.Phase, read.DegradationCode),
                    observedTime.UtcNow,
                    degraded.Window);
            }

            NightState? state = read.Value;
            LogicalTimeResult logicalTime = state is null
                ? new(
                    observedTime.UtcNow,
                    observedTime.Uptime,
                    observedTime.BootSessionId,
                    false)
                : LogicalTime.Advance(state, observedTime);
            ScheduleEvaluation evaluation = evaluateSchedule(logicalTime.UtcNow, state);
            NightWindow window = evaluation.Window;
            NightPhase observedBasePhase = evaluation.Phase;
            EnsureBasePhase(observedBasePhase);

            if (logicalTime.IsStaleObservation)
            {
                if (state!.IsClosed)
                {
                    return Scheduled(
                        Successful(null, NightPhase.Morning, NightPhase.Morning),
                        logicalTime.UtcNow,
                        window);
                }

                NightPhase preservedBasePhase = state.HighestBasePhaseReached;
                NightPhase preservedEffectivePhase = OverridePolicy.ResolvePhase(
                    state,
                    preservedBasePhase,
                    logicalTime.UtcNow);
                return Scheduled(
                    Successful(state, preservedBasePhase, preservedEffectivePhase),
                    logicalTime.UtcNow,
                    window);
            }

            if (state is null)
            {
                if (observedBasePhase == NightPhase.Morning)
                {
                    return Scheduled(
                        Successful(null, NightPhase.Morning, NightPhase.Morning),
                        logicalTime.UtcNow,
                        window);
                }

                (CoordinatorObservation? Observation, bool Conflict) started = await StartNightAsync(
                    window,
                    observedBasePhase,
                    logicalTime.UtcNow,
                    logicalTime.Uptime,
                    logicalTime.BootSessionId,
                    evaluation.ScheduleTimeZoneSerialized,
                    read.Version,
                    cancellationToken).ConfigureAwait(false);
                if (started.Conflict)
                {
                    continue;
                }

                return Scheduled(started.Observation!, logicalTime.UtcNow, window);
            }

            bool overrideEnded = state.ActiveOverride is { } activeOverride
                && logicalTime.UtcNow >= activeOverride.EndsAtUtc;
            NightState timeAdvancedState = state with
            {
                LastObservedUtc = logicalTime.UtcNow,
                LastObservedUptime = logicalTime.Uptime,
                LastObservedBootSessionId = logicalTime.BootSessionId,
                ActiveOverride = overrideEnded ? null : state.ActiveOverride,
                ScheduleTimeZoneSerialized = state.IsClosed
                    ? state.ScheduleTimeZoneSerialized
                    : state.ScheduleTimeZoneSerialized
                        ?? evaluation.ScheduleTimeZoneSerialized,
            };

            if (preserveOnRawRollback
                && (observedTime.UtcNow < state.LastObservedUtc || window.NightDate < state.NightDate))
            {
                StorageWriteResult rollbackWrite = await SaveObservedTimeAsync(
                    state,
                    timeAdvancedState,
                    overrideEnded,
                    read.Version,
                    cancellationToken).ConfigureAwait(false);
                if (rollbackWrite.IsConflict)
                {
                    continue;
                }

                if (rollbackWrite.IsDegraded)
                {
                    return Scheduled(
                        Degraded(state.HighestBasePhaseReached, rollbackWrite.DegradationCode),
                        logicalTime.UtcNow,
                        window);
                }

                if (timeAdvancedState.IsClosed)
                {
                    return Scheduled(
                        Successful(null, NightPhase.Morning, NightPhase.Morning),
                        logicalTime.UtcNow,
                        window);
                }

                NightPhase rollbackBase = timeAdvancedState.HighestBasePhaseReached;
                NightPhase rollbackEffective = OverridePolicy.ResolvePhase(
                    timeAdvancedState,
                    rollbackBase,
                    logicalTime.UtcNow);
                return Scheduled(
                    Successful(timeAdvancedState, rollbackBase, rollbackEffective),
                    logicalTime.UtcNow,
                    window);
            }

            if (state.IsClosed)
            {
                if (window.NightDate <= state.NightDate || observedBasePhase == NightPhase.Morning)
                {
                    StorageWriteResult closedWrite = await SaveObservedTimeAsync(
                        state,
                        timeAdvancedState,
                        overrideEnded,
                        read.Version,
                        cancellationToken).ConfigureAwait(false);
                    if (closedWrite.IsConflict)
                    {
                        continue;
                    }

                    if (closedWrite.IsDegraded)
                    {
                        return Scheduled(
                            Degraded(NightPhase.Morning, closedWrite.DegradationCode),
                            logicalTime.UtcNow,
                            window);
                    }

                    return Scheduled(
                        Successful(null, NightPhase.Morning, NightPhase.Morning),
                        logicalTime.UtcNow,
                        window);
                }

                (CoordinatorObservation? Observation, bool Conflict) started = await StartNightAsync(
                    window,
                    observedBasePhase,
                    logicalTime.UtcNow,
                    logicalTime.Uptime,
                    logicalTime.BootSessionId,
                    evaluation.ScheduleTimeZoneSerialized,
                    read.Version,
                    cancellationToken).ConfigureAwait(false);
                if (started.Conflict)
                {
                    continue;
                }

                return Scheduled(started.Observation!, logicalTime.UtcNow, window);
            }

            if (window.NightDate > state.NightDate)
            {
                (CoordinatorObservation? Observation, bool Conflict) closed = await CloseNightAsync(
                    timeAdvancedState,
                    logicalTime.UtcNow,
                    read.Version,
                    cancellationToken).ConfigureAwait(false);
                if (closed.Conflict)
                {
                    continue;
                }

                if (closed.Observation!.IsDegraded)
                {
                    return Scheduled(closed.Observation, logicalTime.UtcNow, window);
                }

                ScheduleEvaluation nextNight = evaluateSchedule(logicalTime.UtcNow, null);
                EnsureBasePhase(nextNight.Phase);
                if (nextNight.Phase == NightPhase.Morning
                    || nextNight.Window.NightDate <= state.NightDate)
                {
                    // A time-zone change can make the newly selected local zone
                    // point at the same date that was just closed. Starting it
                    // again would mint a new NightId and refresh nightly tokens.
                    // Closed night dates are therefore strictly monotonic.
                    return Scheduled(
                        closed.Observation,
                        logicalTime.UtcNow,
                        nextNight.Window);
                }

                (CoordinatorObservation? Observation, bool Conflict) started = await StartNightAsync(
                    nextNight.Window,
                    nextNight.Phase,
                    logicalTime.UtcNow,
                    logicalTime.Uptime,
                    logicalTime.BootSessionId,
                    nextNight.ScheduleTimeZoneSerialized,
                    read.Version + 1,
                    cancellationToken).ConfigureAwait(false);
                if (started.Conflict)
                {
                    continue;
                }

                return Scheduled(started.Observation!, logicalTime.UtcNow, nextNight.Window);
            }

            if (observedBasePhase == NightPhase.Morning && logicalTime.UtcNow >= window.Wake)
            {
                (CoordinatorObservation? Observation, bool Conflict) closed = await CloseNightAsync(
                    timeAdvancedState,
                    logicalTime.UtcNow,
                    read.Version,
                    cancellationToken).ConfigureAwait(false);
                if (closed.Conflict)
                {
                    continue;
                }

                return Scheduled(closed.Observation!, logicalTime.UtcNow, window);
            }

            NightPhase highest = HigherBasePhase(state.HighestBasePhaseReached, observedBasePhase);
            NightState updated = timeAdvancedState with
            {
                HighestBasePhaseReached = highest,
            };
            NightEventKind eventKind = overrideEnded
                ? NightEventKind.OverrideEnded
                : highest != state.HighestBasePhaseReached
                    ? NightEventKind.BasePhaseAdvanced
                    : NightEventKind.StateObserved;
            NightEvent nightEvent = new(
                Guid.NewGuid(),
                state.NightId,
                logicalTime.UtcNow,
                eventKind,
                highest,
                overrideEnded ? state.ActiveOverride!.Kind : null);

            StorageWriteResult write = await repository
                .SaveActiveStateWithEventAsync(
                    updated,
                    nightEvent,
                    read.Version,
                    cancellationToken)
                .ConfigureAwait(false);
            if (write.IsConflict)
            {
                continue;
            }

            if (write.IsDegraded)
            {
                return Scheduled(
                    Degraded(highest, write.DegradationCode),
                    logicalTime.UtcNow,
                    window);
            }

            NightPhase effective = OverridePolicy.ResolvePhase(updated, highest, logicalTime.UtcNow);
            return Scheduled(
                Successful(updated, highest, effective),
                logicalTime.UtcNow,
                window);
        }
    }

    private async ValueTask<(CoordinatorObservation? Observation, bool Conflict)> StartNightAsync(
        NightWindow window,
        NightPhase observedBasePhase,
        DateTimeOffset observedAtUtc,
        TimeSpan? observedUptime,
        Guid? observedBootSessionId,
        string? scheduleTimeZoneSerialized,
        long expectedVersion,
        CancellationToken cancellationToken)
    {
        NightState state = new(
            Guid.NewGuid(),
            window.NightDate,
            observedAtUtc,
            observedBasePhase,
            null,
            false,
            false,
            false,
            false,
            false,
            false,
            LastObservedUptime: observedUptime,
            LastObservedBootSessionId: observedBootSessionId,
            ScheduledLockAtUtc: window.Lock.ToUniversalTime(),
            ScheduleTimeZoneSerialized: scheduleTimeZoneSerialized);
        NightEvent nightEvent = new(
            Guid.NewGuid(),
            state.NightId,
            observedAtUtc,
            NightEventKind.NightStarted,
            observedBasePhase);
        StorageWriteResult write = await repository
            .SaveActiveStateWithEventAsync(state, nightEvent, expectedVersion, cancellationToken)
            .ConfigureAwait(false);

        if (write.IsConflict)
        {
            return (null, true);
        }

        CoordinatorObservation observation = write.IsDegraded
            ? Degraded(observedBasePhase, write.DegradationCode)
            : Successful(state, observedBasePhase, observedBasePhase);
        return (observation, false);
    }

    private async ValueTask<StorageWriteResult> SaveObservedTimeAsync(
        NightState originalState,
        NightState timeAdvancedState,
        bool overrideEnded,
        long expectedVersion,
        CancellationToken cancellationToken)
    {
        NightEvent nightEvent = new(
            Guid.NewGuid(),
            originalState.NightId,
            timeAdvancedState.LastObservedUtc,
            overrideEnded ? NightEventKind.OverrideEnded : NightEventKind.StateObserved,
            originalState.HighestBasePhaseReached,
            overrideEnded ? originalState.ActiveOverride!.Kind : null);
        return await repository
            .SaveActiveStateWithEventAsync(
                timeAdvancedState,
                nightEvent,
                expectedVersion,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private async ValueTask<(CoordinatorObservation? Observation, bool Conflict)> CloseNightAsync(
        NightState state,
        DateTimeOffset observedAtUtc,
        long expectedVersion,
        CancellationToken cancellationToken)
    {
        NightOutcome outcome = new(
            state.NightId,
            state.NightDate,
            observedAtUtc,
            state.EmergencyUsed,
            state.TeamRescueUsed,
            state.EntertainmentUsed,
            state.DeliberateBypass,
            state.LateNewEntertainment,
            state.MissedLock,
            state.OverrideReasons,
            state.FirstLockObservedAtUtc,
            state.ScheduledLockAtUtc,
            ProtectionGapObserved: state.ProtectionGapObserved,
            ScheduleTimeZoneSerialized: state.ScheduleTimeZoneSerialized);
        NightState closedState = state with
        {
            LastObservedUtc = observedAtUtc,
            ActiveOverride = null,
            IsClosed = true,
        };
        NightEvent nightEvent = new(
            Guid.NewGuid(),
            state.NightId,
            observedAtUtc,
            NightEventKind.NightClosed,
            state.HighestBasePhaseReached);
        StorageWriteResult write = await repository
            .CloseActiveStateWithOutcomeAndEventAsync(
                closedState,
                outcome,
                nightEvent,
                expectedVersion,
                cancellationToken)
            .ConfigureAwait(false);

        if (write.IsConflict)
        {
            return (null, true);
        }

        CoordinatorObservation observation = write.IsDegraded
            ? Degraded(NightPhase.Morning, write.DegradationCode)
            : Successful(null, NightPhase.Morning, NightPhase.Morning);
        return (observation, false);
    }

    private static ScheduledCoordinatorObservation Scheduled(
        CoordinatorObservation observation,
        DateTimeOffset evaluatedAtUtc,
        NightWindow window) => new(observation, evaluatedAtUtc, window);

    private static CoordinatorObservation Successful(
        NightState? state,
        NightPhase basePhase,
        NightPhase effectivePhase) =>
        new(StorageMode.Success, state, basePhase, effectivePhase);

    private static CoordinatorObservation Degraded(
        NightPhase observedBasePhase,
        string? code) =>
        new(StorageMode.Degraded, null, observedBasePhase, observedBasePhase, code);

    private static NightPhase HigherBasePhase(NightPhase current, NightPhase observed) =>
        BaseSeverity(observed) > BaseSeverity(current) ? observed : current;

    private static int BaseSeverity(NightPhase phase) => phase switch
    {
        NightPhase.Free => 0,
        NightPhase.LastStart => 1,
        NightPhase.Grace => 2,
        NightPhase.LandingLocked => 3,
        NightPhase.Morning => -1,
        _ => throw new ArgumentOutOfRangeException(nameof(phase), phase, "Temporary phases are not base phases."),
    };

    private static void EnsureBasePhase(NightPhase phase) => _ = BaseSeverity(phase);

    private sealed record ScheduleEvaluation(
        NightWindow Window,
        NightPhase Phase,
        string? ScheduleTimeZoneSerialized);
}
