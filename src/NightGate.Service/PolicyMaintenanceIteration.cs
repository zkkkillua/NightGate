using System.Collections.Immutable;
using NightGate.Core;

namespace NightGate.Service;

public interface ITimeZoneProvider
{
    TimeZoneInfo Local { get; }
}

public sealed class SystemTimeZoneProvider : ITimeZoneProvider
{
    private static readonly object LocalTimeZoneCacheGate = new();
    private readonly Action _clearCachedData;
    private readonly Func<TimeZoneInfo> _readLocal;

    public SystemTimeZoneProvider()
        : this(TimeZoneInfo.ClearCachedData, () => TimeZoneInfo.Local)
    {
    }

    internal SystemTimeZoneProvider(
        Action clearCachedData,
        Func<TimeZoneInfo> readLocal)
    {
        ArgumentNullException.ThrowIfNull(clearCachedData);
        ArgumentNullException.ThrowIfNull(readLocal);
        _clearCachedData = clearCachedData;
        _readLocal = readLocal;
    }

    public TimeZoneInfo Local
    {
        get
        {
            // TimeZoneInfo.Local is process-cached. Serialize refresh + read so
            // concurrent service commands cannot observe the cache between those
            // operations. Return a detached snapshot because a later cache clear
            // invalidates references obtained directly from TimeZoneInfo.Local.
            lock (LocalTimeZoneCacheGate)
            {
                _clearCachedData();
                TimeZoneInfo local = _readLocal()
                    ?? throw new InvalidOperationException(
                        "The local system time zone is unavailable.");
                return NightScheduleTimeZone.Restore(
                    NightScheduleTimeZone.Capture(local));
            }
        }
    }
}

public interface IPolicyMaintenanceIteration
{
    ValueTask ExecuteAsync(CancellationToken cancellationToken = default);
}

public sealed class PolicyMaintenanceIteration(
    INightStateRepository stateRepository,
    IProgressRepository progressRepository,
    IHistoryRepository historyRepository,
    INightMutationGate mutationGate,
    IClock clock,
    ITimeZoneProvider timeZoneProvider,
    IServiceStatusPublisher statusPublisher,
    IConfiguredRuleProvider? configuredRuleProvider = null,
    IConfiguredSiteRuleProvider? configuredSiteRuleProvider = null,
    IRuleSettingsRepository? ruleSettingsRepository = null,
    IActiveRuleSnapshotPublisher? activeRuleSnapshotPublisher = null,
    INoticeClaimRepository? noticeClaimRepository = null,
    IServiceStatusReader? serviceStatusReader = null) : IPolicyMaintenanceIteration
{
    private const int ProgressionWindowSize = 4;
    private static readonly TimeSpan DefaultLastStartLeadTime = TimeSpan.FromMinutes(35);
    private readonly IConfiguredRuleProvider _configuredRuleProvider =
        configuredRuleProvider ?? MissingConfiguredRuleProvider.Instance;
    private readonly IConfiguredSiteRuleProvider _configuredSiteRuleProvider =
        configuredSiteRuleProvider ?? MissingConfiguredSiteRuleProvider.Instance;
    private readonly IServiceStatusReader? _serviceStatusReader =
        serviceStatusReader ?? statusPublisher as IServiceStatusReader;
    private readonly IServiceStatusRecovery? _serviceStatusRecovery =
        statusPublisher as IServiceStatusRecovery;

    public async ValueTask ExecuteAsync(CancellationToken cancellationToken = default)
    {
        ClockObservation clockObservation = clock.Observe();
        DateTimeOffset now = clockObservation.UtcNow;
        StorageResult<ProgressState> initialProgress = await progressRepository
            .ReadProgressAsync(cancellationToken)
            .ConfigureAwait(false);
        EnsureAvailable(initialProgress.Mode);

        ScheduleStep step = ScheduleProfile.Default.Steps.Single(
            candidate => candidate.Number == Math.Clamp(initialProgress.Value.CurrentStep, 1, 4));
        TimeZoneInfo timeZone = timeZoneProvider.Local;
        ServiceRuntimeStatusSnapshot? recoverySnapshot =
            _serviceStatusRecovery?.ReadSnapshot();
        ServiceRuntimeStatus? currentStatus = recoverySnapshot?.Status
            ?? _serviceStatusReader?.Current;
        if (currentStatus?.IsDegraded == true)
        {
            await MarkProtectionGapIfNeededAsync(
                    step,
                    timeZone,
                    clockObservation,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        NightStateCoordinator coordinator = new(stateRepository, mutationGate);
        ScheduledCoordinatorObservation scheduled = await coordinator
            .ObserveScheduleAsync(step, timeZone, clockObservation, cancellationToken)
            .ConfigureAwait(false);
        CoordinatorObservation observation = scheduled.Observation;
        EnsureAvailable(observation.Mode);

        ProgressState activatedProgress;
        using (IDisposable activationLease = await mutationGate
            .EnterAsync(cancellationToken)
            .ConfigureAwait(false))
        {
            while (true)
            {
                StorageResult<ProgressState> currentProgress = await progressRepository
                    .ReadProgressAsync(cancellationToken)
                    .ConfigureAwait(false);
                EnsureAvailable(currentProgress.Mode);
                ProgressState activated = ProgressionEngine.ActivatePendingStep(
                    currentProgress.Value,
                    scheduled.Window.NightDate);
                if (activated == currentProgress.Value)
                {
                    activatedProgress = currentProgress.Value;
                    break;
                }

                StorageWriteResult activationWrite = await progressRepository
                    .SaveProgressAsync(
                        activated,
                        currentProgress.Version,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (activationWrite.IsConflict)
                {
                    continue;
                }

                EnsureAvailable(activationWrite.Mode);
                activatedProgress = activated;
                break;
            }
        }

        if (activatedProgress.CurrentStep != step.Number)
        {
            step = ScheduleProfile.Default.Steps.Single(
                candidate => candidate.Number == activatedProgress.CurrentStep);
            scheduled = await coordinator
                .ObserveScheduleAsync(step, timeZone, clockObservation, cancellationToken)
                .ConfigureAwait(false);
            observation = scheduled.Observation;
            EnsureAvailable(observation.Mode);
        }

        (ImmutableArray<AppRule> activeAppRules, ImmutableArray<SiteRule> activeSiteRules) =
            await LoadActiveRulesAsync(scheduled.Window.NightDate, cancellationToken)
                .ConfigureAwait(false);
        activeRuleSnapshotPublisher?.Publish(activeAppRules);

        using (IDisposable progressionLease = await mutationGate
            .EnterAsync(cancellationToken)
            .ConfigureAwait(false))
        {
            while (true)
            {
                StorageResult<ProgressState> currentProgress = await progressRepository
                    .ReadProgressAsync(cancellationToken)
                    .ConfigureAwait(false);
                StorageResult<IReadOnlyList<NightOutcome>> outcomes = await historyRepository
                    .ReadLatestEligibleOutcomesAsync(ProgressionWindowSize, cancellationToken)
                    .ConfigureAwait(false);
                EnsureAvailable(currentProgress.Mode);
                EnsureAvailable(outcomes.Mode);
                ProgressState advanced = ProgressionEngine.Advance(currentProgress.Value, outcomes.Value);
                if (advanced == currentProgress.Value)
                {
                    break;
                }

                StorageWriteResult progressWrite = await progressRepository
                    .SaveProgressAsync(advanced, currentProgress.Version, cancellationToken)
                    .ConfigureAwait(false);
                if (progressWrite.IsConflict)
                {
                    continue;
                }

                EnsureAvailable(progressWrite.Mode);
                break;
            }
        }

        StorageWriteResult purge = await historyRepository
            .PurgeEventsOlderThanAsync(now.AddDays(-90), cancellationToken)
            .ConfigureAwait(false);
        EnsureAvailable(purge.Mode);
        if (noticeClaimRepository is not null)
        {
            StorageWriteResult noticePurge = await noticeClaimRepository
                .PurgeNoticeClaimsOlderThanAsync(
                    scheduled.EvaluatedAtUtc.AddDays(-90),
                    cancellationToken)
                .ConfigureAwait(false);
            EnsureAvailable(noticePurge.Mode);
        }

        PolicySnapshot policy = new(
            scheduled.EvaluatedAtUtc,
            observation.EffectivePhase,
            scheduled.Window,
            activeAppRules,
            activeSiteRules,
            true,
            false,
            observation.State?.ActiveOverride);
        ServiceRuntimeStatus healthyStatus = new(true, false, null, policy)
        {
            NextProtectedStartAtUtc = CalculateNextProtectedStartAtUtc(
                scheduled.EvaluatedAtUtc,
                step,
                timeZone),
        };
        if (_serviceStatusRecovery is not null && recoverySnapshot is { } expected)
        {
            bool recovered = await _serviceStatusRecovery.TryRecoverAsync(
                    expected.Revision,
                    healthyStatus,
                    cancellationToken)
                .ConfigureAwait(false);
            if (!recovered)
            {
                throw new IOException("policy-status-recovery-conflict");
            }
        }
        else
        {
            await statusPublisher.PublishAsync(healthyStatus, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private static DateTimeOffset CalculateNextProtectedStartAtUtc(
        DateTimeOffset evaluatedAtUtc,
        ScheduleStep step,
        TimeZoneInfo timeZone)
    {
        DateTimeOffset local = TimeZoneInfo.ConvertTime(evaluatedAtUtc, timeZone);
        DateOnly localDate = DateOnly.FromDateTime(local.DateTime);
        DateTimeOffset candidate = ScheduleEvaluator.CreateWindow(
            localDate,
            step,
            timeZone).ProtectedStart;
        if (candidate.ToUniversalTime() <= evaluatedAtUtc)
        {
            candidate = ScheduleEvaluator.CreateWindow(
                localDate.AddDays(1),
                step,
                timeZone).ProtectedStart;
        }

        return candidate.ToUniversalTime();
    }

    private async ValueTask MarkProtectionGapIfNeededAsync(
        ScheduleStep step,
        TimeZoneInfo timeZone,
        ClockObservation clockObservation,
        CancellationToken cancellationToken)
    {
        using IDisposable mutationLease = await mutationGate
            .EnterAsync(cancellationToken)
            .ConfigureAwait(false);

        while (true)
        {
            StorageResult<NightState?> read = await stateRepository
                .ReadActiveStateAsync(cancellationToken)
                .ConfigureAwait(false);
            EnsureAvailable(read.Mode);
            NightState? state = read.Value;
            if (state is null || state.IsClosed || state.ProtectionGapObserved)
            {
                return;
            }

            LogicalTimeResult logicalTime = LogicalTime.Advance(state, clockObservation);
            TimeZoneInfo effectiveTimeZone = NightScheduleTimeZone.ResolveForActiveNight(
                state,
                timeZone);
            NightWindow stateWindow = ScheduleEvaluator.CreateWindow(
                state.NightDate,
                step,
                effectiveTimeZone);
            ImmutableArray<AppRule> activeAppRules =
                await LoadActiveAppRulesUnderMutationLeaseAsync(
                        state.NightDate,
                        cancellationToken)
                    .ConfigureAwait(false);
            if (!WasInRestrictedPeriod(
                    state,
                    stateWindow,
                    activeAppRules,
                    logicalTime.UtcNow))
            {
                return;
            }

            NightState updated = state with { ProtectionGapObserved = true };
            NightEvent nightEvent = new(
                Guid.NewGuid(),
                state.NightId,
                logicalTime.UtcNow.ToUniversalTime(),
                NightEventKind.ServiceDegraded,
                state.HighestBasePhaseReached);
            StorageWriteResult write = await stateRepository
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

            EnsureAvailable(write.Mode);
            return;
        }
    }

    private static bool WasInRestrictedPeriod(
        NightState state,
        NightWindow window,
        ImmutableArray<AppRule> activeAppRules,
        DateTimeOffset logicalUtc) =>
        logicalUtc >= EarliestRestrictionStart(state, window, activeAppRules);

    private static DateTimeOffset EarliestRestrictionStart(
        NightState state,
        NightWindow window,
        ImmutableArray<AppRule> activeAppRules)
    {
        DateTimeOffset persistedLock;
        DateTimeOffset earliest;
        if (state.ScheduledLockAtUtc is { } scheduledLock)
        {
            persistedLock = scheduledLock;
            earliest = persistedLock - DefaultLastStartLeadTime;
        }
        else
        {
            persistedLock = window.Lock.ToUniversalTime();
            earliest = window.LastStart.ToUniversalTime();
        }

        foreach (AppRule rule in activeAppRules)
        {
            DateTimeOffset ruleCutoff = ScheduleEvaluator.CalculateLastStart(
                persistedLock,
                rule);
            if (ruleCutoff < earliest)
            {
                earliest = ruleCutoff;
            }
        }

        return earliest;
    }

    private async ValueTask<ImmutableArray<AppRule>>
        LoadActiveAppRulesUnderMutationLeaseAsync(
            DateOnly logicalNightDate,
            CancellationToken cancellationToken)
    {
        if (ruleSettingsRepository is null)
        {
            ConfiguredRuleProviderResult configuredRules = _configuredRuleProvider.GetRules();
            if (configuredRules.IsDegraded
                || !ConfiguredRuleSetValidator.IsValid(configuredRules.Rules))
            {
                throw new IOException(
                    configuredRules.DegradationCode ?? "configured-rules-invalid");
            }

            return configuredRules.Rules;
        }

        while (true)
        {
            StorageResult<RuleSettingsState> read = await ruleSettingsRepository
                .ReadRuleSettingsAsync(cancellationToken)
                .ConfigureAwait(false);
            EnsureAvailable(read.Mode);
            RuleSettingsState activated = RuleSettingsPolicy.Activate(
                read.Value,
                logicalNightDate);
            if (activated == read.Value)
            {
                return read.Value.ActiveAppRules;
            }

            StorageWriteResult write = await ruleSettingsRepository
                .SaveRuleSettingsAsync(activated, read.Version, cancellationToken)
                .ConfigureAwait(false);
            if (write.IsConflict)
            {
                continue;
            }

            EnsureAvailable(write.Mode);
            return activated.ActiveAppRules;
        }
    }

    private async ValueTask<(ImmutableArray<AppRule> AppRules, ImmutableArray<SiteRule> SiteRules)>
        LoadActiveRulesAsync(
            DateOnly logicalNightDate,
            CancellationToken cancellationToken)
    {
        if (ruleSettingsRepository is null)
        {
            ConfiguredRuleProviderResult configuredRules = _configuredRuleProvider.GetRules();
            if (configuredRules.IsDegraded
                || !ConfiguredRuleSetValidator.IsValid(configuredRules.Rules))
            {
                throw new IOException(
                    configuredRules.DegradationCode ?? "configured-rules-invalid");
            }

            ConfiguredSiteRuleProviderResult configuredSiteRules =
                _configuredSiteRuleProvider.GetRules();
            if (configuredSiteRules.IsDegraded
                || !ConfiguredSiteRuleSetValidator.IsValid(configuredSiteRules.Rules))
            {
                throw new IOException(
                    configuredSiteRules.DegradationCode
                    ?? "configured-site-rules-invalid");
            }

            return (configuredRules.Rules, configuredSiteRules.Rules);
        }

        using IDisposable activationLease = await mutationGate
            .EnterAsync(cancellationToken)
            .ConfigureAwait(false);
        while (true)
        {
            StorageResult<RuleSettingsState> read = await ruleSettingsRepository
                .ReadRuleSettingsAsync(cancellationToken)
                .ConfigureAwait(false);
            EnsureAvailable(read.Mode);
            RuleSettingsState activated = RuleSettingsPolicy.Activate(
                read.Value,
                logicalNightDate);
            if (activated == read.Value)
            {
                return (read.Value.ActiveAppRules, read.Value.ActiveSiteRules);
            }

            StorageWriteResult write = await ruleSettingsRepository
                .SaveRuleSettingsAsync(activated, read.Version, cancellationToken)
                .ConfigureAwait(false);
            if (write.IsConflict)
            {
                continue;
            }

            EnsureAvailable(write.Mode);
            return (activated.ActiveAppRules, activated.ActiveSiteRules);
        }
    }

    private sealed class MissingConfiguredRuleProvider : IConfiguredRuleProvider
    {
        public static MissingConfiguredRuleProvider Instance { get; } = new();

        public ConfiguredRuleProviderResult GetRules() =>
            ConfiguredRuleProviderResult.Degraded("configured-rules-unavailable");
    }

    private sealed class MissingConfiguredSiteRuleProvider : IConfiguredSiteRuleProvider
    {
        public static MissingConfiguredSiteRuleProvider Instance { get; } = new();

        public ConfiguredSiteRuleProviderResult GetRules() =>
            ConfiguredSiteRuleProviderResult.Degraded("configured-site-rules-unavailable");
    }

    private static void EnsureAvailable(StorageMode mode)
    {
        if (mode == StorageMode.Degraded)
        {
            throw new IOException("storage-unavailable");
        }
    }
}
