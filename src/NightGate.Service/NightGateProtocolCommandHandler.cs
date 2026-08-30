using NightGate.Core;
using System.Security.Cryptography;
using System.Text;

namespace NightGate.Service;

public sealed class NightGateProtocolCommandHandler(
    INightStateRepository stateRepository,
    IProgressRepository progressRepository,
    IHistoryRepository historyRepository,
    IServiceStatusReader statusReader,
    IServiceStatusPublisher statusPublisher,
    OverridePolicy overridePolicy,
    INightMutationGate mutationGate,
    IClock clock,
    IProcessPersistenceRepository? processPersistenceRepository = null,
    IBrowserEventRepository? browserEventRepository = null,
    ITimeZoneProvider? timeZoneProvider = null,
    IOnboardingRepository? onboardingRepository = null,
    IRuleSettingsRepository? ruleSettingsRepository = null,
    INightSelfReportRepository? selfReportRepository = null,
    INoticeClaimRepository? noticeClaimRepository = null,
    IActiveRuleSnapshotPublisher? activeRuleSnapshotPublisher = null,
    IChromeProtectionHealthRepository? chromeProtectionHealthRepository = null,
    ILegacyTaskMigrationRepository? legacyTaskMigrationRepository = null,
    TimeProvider? legacyRecoveryTimeProvider = null,
    IActiveProcessSnapshotPublisher? activeProcessSnapshotPublisher = null,
    IPolicyMaintenanceScheduler? policyMaintenanceScheduler = null,
    DesktopSessionLease? desktopSessionLease = null) : IProtocolCommandHandler
{
    private const int WeeklyReportOutcomeReadCount = 21;
    private const int LegacyTaskMigrationPageSize = 8;
    private const int MaximumLegacyRecoveryChallenges = 64;
    private static readonly TimeSpan LegacyRecoveryChallengeLifetime =
        TimeSpan.FromSeconds(10);
    private static readonly TimeSpan ChromeHealthMaximumAge = TimeSpan.FromSeconds(90);
    private readonly object _policyResponseLeaseSync = new();
    private long _policyResponseLeaseHighWaterUtcTicks = -1;
    private long _policyResponseAuthoritativeRevision = long.MinValue;
    private PolicySnapshot? _policyResponseAuthoritativePayload;
    private readonly TimeProvider _legacyRecoveryTimeProvider =
        legacyRecoveryTimeProvider ?? TimeProvider.System;
    private readonly Dictionary<string, LegacyRecoveryChallenge>
        _legacyRecoveryChallenges = new(StringComparer.Ordinal);

    public ValueTask<ProtocolCommandResult> ExecuteAsync(
        ServiceCommand command,
        CancellationToken cancellationToken = default) => command switch
        {
            GetPolicyCommand => GetPolicyAsync(cancellationToken),
            GetDesktopPolicyCommand getDesktopPolicy =>
                GetDesktopPolicyAsync(getDesktopPolicy, cancellationToken),
            EndDesktopSessionCommand endDesktopSession =>
                EndDesktopSession(endDesktopSession),
            GetStatusCommand => ValueTask.FromResult(StatusResult()),
            GetUserStateCommand => GetUserStateAsync(cancellationToken),
            RequestOverrideCommand request => RequestOverrideAsync(request, cancellationToken),
            RecordEventCommand record => RecordEventAsync(record, cancellationToken),
            ConfirmIPhoneStepCommand confirm =>
                ConfirmIPhoneStepAsync(confirm, cancellationToken),
            CompleteOnboardingStepCommand completeOnboarding =>
                CompleteOnboardingStepAsync(completeOnboarding, cancellationToken),
            SaveNightSelfReportCommand saveSelfReport =>
                SaveNightSelfReportAsync(saveSelfReport, cancellationToken),
            SaveRuleSettingsCommand saveRules =>
                SaveRuleSettingsAsync(saveRules, cancellationToken),
            ClaimDueNoticeCommand => ClaimDueNoticeAsync(cancellationToken),
            RecordBrowserEventCommand recordBrowser =>
                RecordBrowserEventAsync(recordBrowser, cancellationToken),
            RecordChromeHealthCommand recordChromeHealth =>
                RecordChromeHealthAsync(recordChromeHealth, cancellationToken),
            ListLegacyTaskMigrationsCommand listLegacyTasks =>
                ListLegacyTaskMigrationsAsync(listLegacyTasks, cancellationToken),
            FindLegacyTaskMigrationRecoveryCandidateCommand findLegacyRecovery =>
                FindLegacyTaskMigrationRecoveryCandidateAsync(
                    findLegacyRecovery,
                    cancellationToken),
            PrepareLegacyTaskMigrationCommand prepareLegacyTask =>
                PrepareLegacyTaskMigrationAsync(prepareLegacyTask, cancellationToken),
            CompleteLegacyTaskMigrationCommand completeLegacyTask =>
                CompleteLegacyTaskMigrationAsync(completeLegacyTask, cancellationToken),
            RecoverLegacyTaskMigrationDisabledCommand recoverLegacyTask =>
                RecoverLegacyTaskMigrationDisabledAsync(
                    recoverLegacyTask,
                    cancellationToken),
            ClearHistoryCommand => ClearHistoryAsync(cancellationToken),
            LoadProcessPersistenceCommand load =>
                LoadProcessPersistenceAsync(load, cancellationToken),
            CompareExchangeProcessPersistenceCommand compareExchange =>
                CompareExchangeProcessPersistenceAsync(compareExchange, cancellationToken),
            _ => ValueTask.FromResult(ProtocolCommandResult.Degraded(new { error = "unsupportedCommand" })),
        };

    private async ValueTask<ProtocolCommandResult> GetUserStateAsync(
        CancellationToken cancellationToken)
    {
        if (onboardingRepository is null
            || ruleSettingsRepository is null
            || selfReportRepository is null
            || chromeProtectionHealthRepository is null)
        {
            return await DegradedAsync("storage-unavailable", cancellationToken)
                .ConfigureAwait(false);
        }

        try
        {
            using IDisposable mutationLease = await mutationGate
                .EnterAsync(cancellationToken)
                .ConfigureAwait(false);
            DateTimeOffset observedAtUtc = clock.UtcNow.ToUniversalTime();
            TimeZoneInfo serviceTimeZone = GetServiceTimeZone();

            StorageResult<ProgressState> progressResult = await progressRepository
                .ReadProgressAsync(cancellationToken)
                .ConfigureAwait(false);
            StorageResult<NightState?> nightStateResult = await stateRepository
                .ReadActiveStateAsync(cancellationToken)
                .ConfigureAwait(false);
            StorageResult<OnboardingState> onboardingResult = await onboardingRepository
                .ReadOnboardingAsync(cancellationToken)
                .ConfigureAwait(false);
            StorageResult<RuleSettingsState> ruleSettingsResult = await ruleSettingsRepository
                .ReadRuleSettingsAsync(cancellationToken)
                .ConfigureAwait(false);
            StorageResult<ChromeProtectionHealth?> chromeHealthResult =
                await chromeProtectionHealthRepository
                    .ReadChromeProtectionHealthAsync(cancellationToken)
                    .ConfigureAwait(false);
            StorageResult<IReadOnlyList<NightOutcome>> outcomesResult = await historyRepository
                .ReadLatestOutcomesAsync(WeeklyReportOutcomeReadCount, cancellationToken)
                .ConfigureAwait(false);
            if (progressResult.IsDegraded
                || nightStateResult.IsDegraded
                || onboardingResult.IsDegraded
                || ruleSettingsResult.IsDegraded
                || outcomesResult.IsDegraded)
            {
                return await DegradedAsync("storage-unavailable", cancellationToken)
                    .ConfigureAwait(false);
            }

            ScheduleStep step = GetCurrentScheduleStep(progressResult.Value);
            TimeZoneInfo effectiveTimeZone = NightScheduleTimeZone.ResolveForActiveNight(
                nightStateResult.Value,
                serviceTimeZone);
            NightWindow currentWindow = ScheduleEvaluator.CreateWindowForInstant(
                observedAtUtc,
                step,
                effectiveTimeZone);
            StorageResult<NightSelfReport?> selfReportResult = await selfReportRepository
                .ReadSelfReportAsync(currentWindow.NightDate, cancellationToken)
                .ConfigureAwait(false);
            if (selfReportResult.IsDegraded)
            {
                return await DegradedAsync("storage-unavailable", cancellationToken)
                    .ConfigureAwait(false);
            }

            DateOnly localCurrentDate = DateOnly.FromDateTime(
                TimeZoneInfo.ConvertTime(observedAtUtc, effectiveTimeZone).DateTime);
            WeeklyReportSummary weeklyReport = WeeklyReportBuilder.Build(
                outcomesResult.Value,
                localCurrentDate,
                effectiveTimeZone);
            return ProtocolCommandResult.Success(new UserStateResponse(
                progressResult.Value,
                onboardingResult.Value,
                ruleSettingsResult.Value,
                weeklyReport,
                currentWindow.NightDate,
                selfReportResult.Value,
                ProjectChromeProtection(
                    chromeHealthResult,
                    observedAtUtc,
                    statusReader.Current)));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return await DegradedAsync("storage-unavailable", cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private async ValueTask<ProtocolCommandResult> CompleteOnboardingStepAsync(
        CompleteOnboardingStepCommand command,
        CancellationToken cancellationToken)
    {
        if (onboardingRepository is null)
        {
            return await DegradedAsync("storage-unavailable", cancellationToken)
                .ConfigureAwait(false);
        }

        try
        {
            using IDisposable mutationLease = await mutationGate
                .EnterAsync(cancellationToken)
                .ConfigureAwait(false);
            DateTimeOffset? completionObservationUtc = null;
            while (true)
            {
                StorageResult<OnboardingState> onboardingResult = await onboardingRepository
                    .ReadOnboardingAsync(cancellationToken)
                    .ConfigureAwait(false);
                if (onboardingResult.IsDegraded)
                {
                    return await DegradedAsync("storage-unavailable", cancellationToken)
                        .ConfigureAwait(false);
                }

                OnboardingState current = onboardingResult.Value;
                if (command.Step < current.CompletedStep
                    || command.Step > current.CompletedStep + 1)
                {
                    return OnboardingRejected("invalidStepSequence");
                }

                if (command.IPhoneConfirmedThroughStep
                        == current.IPhoneConfirmedThroughStep
                    && command.Step == current.CompletedStep
                    && (command.Step != 5 || current.CompletedAtUtc is not null))
                {
                    return OnboardingAccepted(current);
                }

                if (command.IPhoneConfirmedThroughStep
                        < current.IPhoneConfirmedThroughStep)
                {
                    return OnboardingRejected("factsNotMonotonic");
                }

                bool chromeVerified = current.ChromeVerified;
                bool incognitoProtected = current.IncognitoProtected;
                bool incognitoWarningAcknowledged =
                    current.IncognitoWarningAcknowledged;
                bool chromeDegradedAcknowledged =
                    current.ChromeDegradedAcknowledged;
                if (command.Step == 3 && current.CompletedStep == 2)
                {
                    if (chromeProtectionHealthRepository is null)
                    {
                        return await DegradedAsync("storage-unavailable", cancellationToken)
                            .ConfigureAwait(false);
                    }

                    DateTimeOffset chromeObservationUtc = clock.UtcNow.ToUniversalTime();
                    StorageResult<ChromeProtectionHealth?> healthResult =
                        await chromeProtectionHealthRepository
                            .ReadChromeProtectionHealthAsync(cancellationToken)
                            .ConfigureAwait(false);
                    if (healthResult.IsDegraded)
                    {
                        return await DegradedAsync("storage-unavailable", cancellationToken)
                            .ConfigureAwait(false);
                    }

                    ChromeProtectionHealth? health = healthResult.Value;
                    if (health is null
                        || !IsVerifiedChromeProtection(
                            health,
                            chromeObservationUtc,
                            statusReader.Current))
                    {
                        if (!command.ChromeDegradedAcknowledged)
                        {
                            return OnboardingRejected("chromeSetupIncomplete");
                        }

                        chromeDegradedAcknowledged = true;
                    }
                    else
                    {
                        chromeVerified = true;
                        incognitoProtected = health.IncognitoAllowed;
                        incognitoWarningAcknowledged |=
                            command.IncognitoWarningAcknowledged;
                        if (!incognitoProtected && !incognitoWarningAcknowledged)
                        {
                            return OnboardingRejected("chromeSetupIncomplete");
                        }
                    }
                }

                if (command.Step == 4)
                {
                    StorageResult<ProgressState> progressResult = await progressRepository
                        .ReadProgressAsync(cancellationToken)
                        .ConfigureAwait(false);
                    if (progressResult.IsDegraded)
                    {
                        return await DegradedAsync("storage-unavailable", cancellationToken)
                            .ConfigureAwait(false);
                    }

                    _ = GetCurrentScheduleStep(progressResult.Value);
                    if (command.IPhoneConfirmedThroughStep < progressResult.Value.CurrentStep)
                    {
                        return OnboardingRejected("iPhoneSetupIncomplete");
                    }
                }

                DateTimeOffset? completedAtUtc = current.CompletedAtUtc;
                if (command.Step == 5 && current.CompletedAtUtc is null)
                {
                    completionObservationUtc ??= clock.UtcNow.ToUniversalTime();
                    completedAtUtc = completionObservationUtc;
                }

                OnboardingState replacement = new(
                    command.Step,
                    chromeVerified,
                    incognitoProtected,
                    incognitoWarningAcknowledged,
                    command.IPhoneConfirmedThroughStep,
                    completedAtUtc,
                    ChromeDegradedAcknowledged: chromeDegradedAcknowledged);
                StorageWriteResult write = await onboardingRepository
                    .SaveOnboardingAsync(
                        replacement,
                        onboardingResult.Version,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (write.IsConflict)
                {
                    continue;
                }

                return write.IsDegraded
                    ? await DegradedAsync("storage-unavailable", cancellationToken)
                        .ConfigureAwait(false)
                    : OnboardingAccepted(replacement);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return await DegradedAsync("storage-unavailable", cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private async ValueTask<ProtocolCommandResult> SaveNightSelfReportAsync(
        SaveNightSelfReportCommand command,
        CancellationToken cancellationToken)
    {
        if (selfReportRepository is null)
        {
            return await DegradedAsync("storage-unavailable", cancellationToken)
                .ConfigureAwait(false);
        }

        try
        {
            using IDisposable mutationLease = await mutationGate
                .EnterAsync(cancellationToken)
                .ConfigureAwait(false);
            DateTimeOffset observedAtUtc = clock.UtcNow.ToUniversalTime();
            TimeZoneInfo serviceTimeZone = GetServiceTimeZone();
            StorageResult<ProgressState> progressResult = await progressRepository
                .ReadProgressAsync(cancellationToken)
                .ConfigureAwait(false);
            StorageResult<NightState?> nightStateResult = await stateRepository
                .ReadActiveStateAsync(cancellationToken)
                .ConfigureAwait(false);
            if (progressResult.IsDegraded || nightStateResult.IsDegraded)
            {
                return await DegradedAsync("storage-unavailable", cancellationToken)
                    .ConfigureAwait(false);
            }

            ScheduleStep step = GetCurrentScheduleStep(progressResult.Value);
            TimeZoneInfo effectiveTimeZone = NightScheduleTimeZone.ResolveForActiveNight(
                nightStateResult.Value,
                serviceTimeZone);
            NightWindow currentWindow = ScheduleEvaluator.CreateWindowForInstant(
                observedAtUtc,
                step,
                effectiveTimeZone);
            if (command.NightDate != currentWindow.NightDate)
            {
                return ProtocolCommandResult.Success(
                    new { saved = false, error = "nightDateMismatch" });
            }

            NightSelfReport report = new(
                currentWindow.NightDate,
                command.PhoneOutOfReach,
                command.WakeWithinWindow,
                observedAtUtc);
            StorageWriteResult write = await selfReportRepository
                .SaveSelfReportAsync(report, cancellationToken)
                .ConfigureAwait(false);
            return write.IsDegraded
                ? await DegradedAsync("storage-unavailable", cancellationToken)
                    .ConfigureAwait(false)
                : ProtocolCommandResult.Success(new { saved = true, selfReport = report });
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return await DegradedAsync("storage-unavailable", cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private async ValueTask<ProtocolCommandResult> SaveRuleSettingsAsync(
        SaveRuleSettingsCommand command,
        CancellationToken cancellationToken)
    {
        if (ruleSettingsRepository is null)
        {
            return await DegradedAsync("storage-unavailable", cancellationToken)
                .ConfigureAwait(false);
        }

        try
        {
            ProtocolCommandResult? result = null;
            bool refreshImmediately = false;
            using (IDisposable mutationLease = await mutationGate
                       .EnterAsync(cancellationToken)
                       .ConfigureAwait(false))
            {
                ClockObservation rawObservation = clock.Observe();
                ClockObservation observation = rawObservation with
                {
                    UtcNow = rawObservation.UtcNow.ToUniversalTime(),
                };
                TimeZoneInfo serviceTimeZone = GetServiceTimeZone();
                StorageResult<ProgressState> progressResult = await progressRepository
                    .ReadProgressAsync(cancellationToken)
                    .ConfigureAwait(false);
                if (progressResult.IsDegraded)
                {
                    return await DegradedAsync("storage-unavailable", cancellationToken)
                        .ConfigureAwait(false);
                }

                ScheduleStep step = GetCurrentScheduleStep(progressResult.Value);
                NightStateCoordinator coordinator = new(
                    stateRepository,
                    NoOpNightMutationGate.Instance);
                ScheduledCoordinatorObservation scheduled = await coordinator
                    .ObserveScheduleAsync(
                        step,
                        serviceTimeZone,
                        observation,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (scheduled.Observation.IsDegraded)
                {
                    return await DegradedAsync("storage-unavailable", cancellationToken)
                        .ConfigureAwait(false);
                }

                DateTimeOffset observedAtUtc = scheduled.EvaluatedAtUtc;
                TimeZoneInfo effectiveTimeZone = NightScheduleTimeZone.ResolveForActiveNight(
                    scheduled.Observation.State,
                    serviceTimeZone);
                DateOnly logicalLocalDate = DateOnly.FromDateTime(
                    TimeZoneInfo.ConvertTime(observedAtUtc, effectiveTimeZone).DateTime);
                while (true)
                {
                    StorageResult<RuleSettingsState> read = await ruleSettingsRepository
                        .ReadRuleSettingsAsync(cancellationToken)
                        .ConfigureAwait(false);
                    if (read.IsDegraded)
                    {
                        return await DegradedAsync("storage-unavailable", cancellationToken)
                            .ConfigureAwait(false);
                    }

                    RuleSettingsState replacement = RuleSettingsPolicy.Save(
                        read.Value,
                        command.AppRules,
                        command.SiteRules,
                        observedAtUtc,
                        effectiveTimeZone);
                    StorageWriteResult write = await ruleSettingsRepository
                        .SaveRuleSettingsAsync(
                            replacement,
                            read.Version,
                            cancellationToken)
                        .ConfigureAwait(false);
                    if (write.IsConflict)
                    {
                        continue;
                    }

                    if (write.IsDegraded)
                    {
                        return await DegradedAsync("storage-unavailable", cancellationToken)
                            .ConfigureAwait(false);
                    }

                    if (replacement.PendingEffectiveNightDate is not { } effectiveNight)
                    {
                        policyMaintenanceScheduler?.MarkDirty();
                        activeRuleSnapshotPublisher?.Publish(replacement.ActiveAppRules);
                        refreshImmediately = true;
                        result = ProtocolCommandResult.Success(new
                        {
                            saved = true,
                            rules = replacement,
                            appliesImmediately = true,
                            appliesTonight = true,
                        });
                        break;
                    }

                    result = ProtocolCommandResult.Success(new
                    {
                        saved = true,
                        rules = replacement,
                        appliesImmediately = false,
                        appliesTonight = effectiveNight == logicalLocalDate,
                        effectiveNight,
                    });
                    break;
                }
            }

            if (refreshImmediately)
            {
                _ = await TryRefreshPolicyAsync(force: true, cancellationToken)
                    .ConfigureAwait(false);
            }

            return result
                ?? throw new InvalidOperationException("Rule settings save produced no result.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return await DegradedAsync("storage-unavailable", cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private async ValueTask<ProtocolCommandResult> ClaimDueNoticeAsync(
        CancellationToken cancellationToken)
    {
        if (noticeClaimRepository is null)
        {
            return await DegradedAsync("storage-unavailable", cancellationToken)
                .ConfigureAwait(false);
        }

        try
        {
            using IDisposable mutationLease = await mutationGate
                .EnterAsync(cancellationToken)
                .ConfigureAwait(false);
            ClockObservation rawObservation = clock.Observe();
            ClockObservation observation = rawObservation with
            {
                UtcNow = rawObservation.UtcNow.ToUniversalTime(),
            };
            TimeZoneInfo serviceTimeZone = GetServiceTimeZone();
            StorageResult<ProgressState> progressResult = await progressRepository
                .ReadProgressAsync(cancellationToken)
                .ConfigureAwait(false);
            if (progressResult.IsDegraded)
            {
                return await DegradedAsync("storage-unavailable", cancellationToken)
                    .ConfigureAwait(false);
            }

            ScheduleStep step = GetCurrentScheduleStep(progressResult.Value);
            NightStateCoordinator coordinator = new(
                stateRepository,
                NoOpNightMutationGate.Instance);
            ScheduledCoordinatorObservation scheduled = await coordinator
                .ObserveScheduleAsync(
                    step,
                    serviceTimeZone,
                    observation,
                    cancellationToken)
                .ConfigureAwait(false);
            if (scheduled.Observation.IsDegraded)
            {
                return await DegradedAsync("storage-unavailable", cancellationToken)
                    .ConfigureAwait(false);
            }

            DateTimeOffset? earliestGameCutoff = null;
            if (ruleSettingsRepository is not null)
            {
                StorageResult<RuleSettingsState> rules = await ruleSettingsRepository
                    .ReadRuleSettingsAsync(cancellationToken)
                    .ConfigureAwait(false);
                if (rules.IsDegraded)
                {
                    return await DegradedAsync("storage-unavailable", cancellationToken)
                        .ConfigureAwait(false);
                }

                earliestGameCutoff = rules.Value.ActiveAppRules
                    .Where(rule => rule.IsConfigured && rule.Category == AppRuleCategory.Game)
                    .Select(rule =>
                        (DateTimeOffset?)scheduled.Window.Lock.AddMinutes(-rule.SessionMinutes))
                    .Min();
            }

            NightNoticeKind? due = NightNoticePolicy.GetDueNotice(
                scheduled.Window,
                scheduled.Observation.EffectivePhase,
                scheduled.EvaluatedAtUtc,
                earliestGameCutoff);
            if (due is null)
            {
                return ProtocolCommandResult.Success(new { claimed = false });
            }

            StorageResult<bool> claim = await noticeClaimRepository
                .TryClaimNoticeAsync(
                    new(scheduled.Window.NightDate, due.Value, scheduled.EvaluatedAtUtc),
                    cancellationToken)
                .ConfigureAwait(false);
            if (claim.IsDegraded)
            {
                return await DegradedAsync("storage-unavailable", cancellationToken)
                    .ConfigureAwait(false);
            }

            return claim.Value
                ? ProtocolCommandResult.Success(new
                {
                    claimed = true,
                    kind = due.Value,
                    nightDate = scheduled.Window.NightDate,
                })
                : ProtocolCommandResult.Success(new { claimed = false });
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return await DegradedAsync("storage-unavailable", cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private TimeZoneInfo GetServiceTimeZone() =>
        (timeZoneProvider ?? new SystemTimeZoneProvider()).Local
        ?? throw new InvalidOperationException("The service time zone is unavailable.");

    private static ScheduleStep GetCurrentScheduleStep(ProgressState progress) =>
        ScheduleProfile.Default.Steps.SingleOrDefault(
            step => step.Number == progress.CurrentStep)
        ?? throw new InvalidDataException("The progression step is invalid.");

    private static ProtocolCommandResult OnboardingAccepted(OnboardingState onboarding) =>
        ProtocolCommandResult.Success(new { accepted = true, onboarding });

    private static ProtocolCommandResult OnboardingRejected(string error) =>
        ProtocolCommandResult.Success(new { accepted = false, error });

    private async ValueTask<ProtocolCommandResult> ConfirmIPhoneStepAsync(
        ConfirmIPhoneStepCommand command,
        CancellationToken cancellationToken)
    {
        if (!command.Confirmation.IsComplete)
        {
            return ProtocolCommandResult.Success(
                new { accepted = false, error = "incompleteChecklist" });
        }

        try
        {
            using IDisposable mutationLease = await mutationGate
                .EnterAsync(cancellationToken)
                .ConfigureAwait(false);
            DateTimeOffset? commandObservedAtUtc = null;
            TimeZoneInfo? commandTimeZone = null;
            while (true)
            {
                StorageResult<ProgressState> progressResult = await progressRepository
                    .ReadProgressAsync(cancellationToken)
                    .ConfigureAwait(false);
                if (progressResult.IsDegraded)
                {
                    return await DegradedAsync("storage-unavailable", cancellationToken)
                        .ConfigureAwait(false);
                }

                ProgressState current = progressResult.Value;
                if (current.PendingStep is null)
                {
                    return ProtocolCommandResult.Success(
                        new { accepted = false, error = "noPendingStep" });
                }

                if (current.PendingStep != command.RequestedStep)
                {
                    return ProtocolCommandResult.Success(
                        new { accepted = false, error = "pendingStepMismatch" });
                }

                if (current.PendingStepConfirmedAtUtc is not null)
                {
                    return ConfirmationAccepted(current);
                }

                commandObservedAtUtc ??= clock.UtcNow.ToUniversalTime();
                if (commandTimeZone is null)
                {
                    TimeZoneInfo currentTimeZone = GetServiceTimeZone();
                    StorageResult<NightState?> nightStateResult = await stateRepository
                        .ReadActiveStateAsync(cancellationToken)
                        .ConfigureAwait(false);
                    if (nightStateResult.IsDegraded)
                    {
                        return await DegradedAsync(
                                "storage-unavailable",
                                cancellationToken)
                            .ConfigureAwait(false);
                    }

                    commandTimeZone = NightScheduleTimeZone.ResolveForActiveNight(
                        nightStateResult.Value,
                        currentTimeZone);
                }

                ProgressState confirmed;
                try
                {
                    confirmed = ProgressionEngine.ConfirmPendingStep(
                        current,
                        command.RequestedStep,
                        command.Confirmation,
                        commandObservedAtUtc.Value,
                        commandTimeZone);
                }
                catch (InvalidOperationException)
                {
                    return ProtocolCommandResult.Success(
                        new { accepted = false, error = "confirmationPredatesUnlock" });
                }

                if (confirmed == current)
                {
                    return ConfirmationAccepted(confirmed);
                }

                StorageWriteResult write = await progressRepository
                    .SaveProgressAsync(
                        confirmed,
                        progressResult.Version,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (write.IsConflict)
                {
                    continue;
                }

                return write.IsDegraded
                    ? await DegradedAsync("storage-unavailable", cancellationToken)
                        .ConfigureAwait(false)
                    : ConfirmationAccepted(confirmed);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return await DegradedAsync("storage-unavailable", cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private static ProtocolCommandResult ConfirmationAccepted(ProgressState progress) =>
        ProtocolCommandResult.Success(new
        {
            accepted = true,
            pendingStep = progress.PendingStep,
            effectiveNightDate = progress.PendingStepEffectiveNightDate,
        });

    private async ValueTask<ProtocolCommandResult> RecordBrowserEventAsync(
        RecordBrowserEventCommand command,
        CancellationToken cancellationToken)
    {
        if (DesktopSessionFailureCode() is not null)
        {
            return DesktopSessionCommandRejected();
        }

        if (browserEventRepository is null)
        {
            return await BrowserEventDegradedAsync(cancellationToken).ConfigureAwait(false);
        }

        try
        {
            if (command.Event.EventType != BrowserEventType.NavigationBlocked)
            {
                StorageWriteResult eventWrite = await browserEventRepository
                    .RecordBrowserEventAsync(command.Event, cancellationToken)
                    .ConfigureAwait(false);
                return eventWrite.IsDegraded
                    ? await BrowserEventDegradedAsync(cancellationToken).ConfigureAwait(false)
                    : ProtocolCommandResult.Success(new { status = "recorded" });
            }

            using IDisposable mutationLease = await mutationGate
                .EnterAsync(cancellationToken)
                .ConfigureAwait(false);
            while (true)
            {
                StorageResult<NightState?> stateResult = await stateRepository
                    .ReadActiveStateAsync(cancellationToken)
                    .ConfigureAwait(false);
                if (stateResult.IsDegraded)
                {
                    return await BrowserEventDegradedAsync(cancellationToken)
                        .ConfigureAwait(false);
                }

                if (stateResult.Value is null || stateResult.Value.IsClosed)
                {
                    StorageWriteResult eventWrite = await browserEventRepository
                        .RecordBrowserEventAsync(command.Event, cancellationToken)
                        .ConfigureAwait(false);
                    return eventWrite.IsDegraded
                        ? await BrowserEventDegradedAsync(cancellationToken).ConfigureAwait(false)
                        : ProtocolCommandResult.Success(new { status = "recorded" });
                }

                NightState updated = stateResult.Value with
                {
                    LateNewEntertainment = true,
                };
                StorageWriteResult write = await browserEventRepository
                    .SaveLateNewEntertainmentWithBrowserEventAsync(
                        updated,
                        command.Event,
                        stateResult.Version,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (write.IsConflict)
                {
                    continue;
                }

                return write.IsDegraded
                    ? await BrowserEventDegradedAsync(cancellationToken).ConfigureAwait(false)
                    : ProtocolCommandResult.Success(new { status = "recorded" });
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return await BrowserEventDegradedAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private async ValueTask<ProtocolCommandResult> BrowserEventDegradedAsync(
        CancellationToken cancellationToken)
    {
        await statusPublisher.PublishAsync(
            ServiceRuntimeStatus.Degraded("browser-event-storage-unavailable"),
            cancellationToken).ConfigureAwait(false);
        return ProtocolCommandResult.Degraded(new { status = "degraded" });
    }

    private async ValueTask<ProtocolCommandResult> LoadProcessPersistenceAsync(
        LoadProcessPersistenceCommand command,
        CancellationToken cancellationToken)
    {
        if (processPersistenceRepository is null)
        {
            return ProtocolCommandResult.Degraded(
                new ProcessPersistenceResponse("unavailable", null));
        }

        ProcessPersistenceLoadResult result = await processPersistenceRepository
            .LoadProcessPersistenceAsync(command.Slot, cancellationToken)
            .ConfigureAwait(false);
        ProcessPersistenceResponse response = new(
            ProcessPersistenceLoadStatusToken(result.Status),
            CreateProcessPersistenceRecordResponse(result.Record));
        return result.Status is ProcessPersistenceLoadStatus.Found
            or ProcessPersistenceLoadStatus.Missing
                ? ProtocolCommandResult.Success(response)
                : ProtocolCommandResult.Degraded(response);
    }

    private async ValueTask<ProtocolCommandResult> CompareExchangeProcessPersistenceAsync(
        CompareExchangeProcessPersistenceCommand command,
        CancellationToken cancellationToken)
    {
        if (processPersistenceRepository is null)
        {
            return ProtocolCommandResult.Degraded(
                new ProcessPersistenceResponse("unavailable", null));
        }

        ProcessPersistenceSaveResult result = await processPersistenceRepository
            .CompareExchangeProcessPersistenceAsync(
                command.Slot,
                command.ExpectedVersion,
                command.Replacement,
                cancellationToken)
            .ConfigureAwait(false);
        if (command.Slot == ProcessPersistenceSlot.ProcessGateEnvelope)
        {
            if (result.Status is ProcessPersistenceSaveStatus.Saved
                    or ProcessPersistenceSaveStatus.Conflict
                && result.Record is { } committed)
            {
                activeProcessSnapshotPublisher?.PublishProcessSnapshot(committed);
            }
            else
            {
                activeProcessSnapshotPublisher?.InvalidateProcessSnapshot();
            }
        }

        ProcessPersistenceResponse response = new(
            ProcessPersistenceSaveStatusToken(result.Status),
            CreateProcessPersistenceRecordResponse(result.Record));
        return result.Status is ProcessPersistenceSaveStatus.Saved
            or ProcessPersistenceSaveStatus.Conflict
                ? ProtocolCommandResult.Success(response)
                : ProtocolCommandResult.Degraded(response);
    }

    private static ProcessPersistenceRecordResponse?
        CreateProcessPersistenceRecordResponse(ProcessPersistenceRecord? record)
    {
        if (record is null)
        {
            return null;
        }

        using System.Text.Json.JsonDocument document =
            System.Text.Json.JsonDocument.Parse(record.PayloadJson);
        return new(
            ProcessPersistenceLimits.GetSlotToken(record.Slot),
            record.SchemaVersion,
            record.Version,
            document.RootElement.Clone());
    }

    private static string ProcessPersistenceLoadStatusToken(
        ProcessPersistenceLoadStatus status) => status switch
        {
            ProcessPersistenceLoadStatus.Found => "found",
            ProcessPersistenceLoadStatus.Missing => "missing",
            ProcessPersistenceLoadStatus.Unavailable => "unavailable",
            ProcessPersistenceLoadStatus.Corrupt => "corrupt",
            _ => "corrupt",
        };

    private static string ProcessPersistenceSaveStatusToken(
        ProcessPersistenceSaveStatus status) => status switch
        {
            ProcessPersistenceSaveStatus.Saved => "saved",
            ProcessPersistenceSaveStatus.Conflict => "conflict",
            ProcessPersistenceSaveStatus.Unavailable => "unavailable",
            ProcessPersistenceSaveStatus.Corrupt => "corrupt",
            _ => "corrupt",
        };

    private ProtocolCommandResult StatusResult()
    {
        ServiceRuntimeStatus status = statusReader.Current;
        return status.IsDegraded
            ? ProtocolCommandResult.Degraded(status)
            : ProtocolCommandResult.Success(status);
    }

    private async ValueTask<ProtocolCommandResult> GetPolicyAsync(
        CancellationToken cancellationToken)
    {
        if (DesktopSessionFailureCode() is { } sessionFailureCode)
        {
            return DesktopSessionFailOpen(sessionFailureCode);
        }

        ProtocolCommandResult result = await GetPolicyCoreAsync(cancellationToken)
            .ConfigureAwait(false);
        return DesktopSessionFailureCode() is { } postRefreshFailureCode
            ? DesktopSessionFailOpen(postRefreshFailureCode)
            : result;
    }

    private async ValueTask<ProtocolCommandResult> GetDesktopPolicyAsync(
        GetDesktopPolicyCommand command,
        CancellationToken cancellationToken)
    {
        if (desktopSessionLease is null)
        {
            return await GetPolicyCoreAsync(cancellationToken).ConfigureAwait(false);
        }

        DesktopSessionLeaseObservation renewal = desktopSessionLease.Renew(command.SessionId);
        if (!renewal.IsActive)
        {
            return DesktopSessionFailOpen(DesktopSessionFailureCode(renewal.State));
        }

        if (DesktopSessionFailureCode(command.SessionId) is { } sessionFailureCode)
        {
            return DesktopSessionFailOpen(sessionFailureCode);
        }

        ProtocolCommandResult result = await GetPolicyCoreAsync(cancellationToken)
            .ConfigureAwait(false);
        return DesktopSessionFailureCode(command.SessionId) is { } postRefreshFailureCode
            ? DesktopSessionFailOpen(postRefreshFailureCode)
            : result;
    }

    private ValueTask<ProtocolCommandResult> EndDesktopSession(
        EndDesktopSessionCommand command)
    {
        bool accepted = desktopSessionLease?.End(command.SessionId) ?? true;
        return ValueTask.FromResult(accepted
            ? ProtocolCommandResult.Success(new { accepted = true })
            : ProtocolCommandResult.Success(
                new { accepted = false, error = "sessionMismatch" }));
    }

    private async ValueTask<ProtocolCommandResult> GetPolicyCoreAsync(
        CancellationToken cancellationToken)
    {
        DateTimeOffset responseLeaseUtc = clock.UtcNow.ToUniversalTime();
        bool refreshed = await TryRefreshPolicyAsync(force: false, cancellationToken)
            .ConfigureAwait(false);
        return refreshed
            ? PolicyResult(responseLeaseUtc)
            : ProtocolCommandResult.Degraded(
                ServiceRuntimeStatus.Degraded("policy-maintenance-failure"));
    }

    private ProtocolCommandResult PolicyResult(DateTimeOffset responseLeaseUtc)
    {
        ServiceRuntimeStatus status = statusReader.Current;
        if (status is not
            {
                EnforcementEnabled: true,
                IsDegraded: false,
                Policy:
                {
                    EnforcementEnabled: true,
                    IsDegraded: false,
                } policy,
            })
        {
            return status.IsDegraded
                ? ProtocolCommandResult.Degraded(status)
                : ProtocolCommandResult.Success(status);
        }

        if (!TryReservePolicyResponseLease(
                policy,
                responseLeaseUtc,
                out DateTimeOffset leasedAtUtc,
                out string? leaseFailureCode))
        {
            return ProtocolCommandResult.Degraded(
                ServiceRuntimeStatus.Degraded(leaseFailureCode!));
        }

        PolicySnapshot leasedPolicy = policy.EvaluatedAt.EqualsExact(leasedAtUtc)
            ? policy
            : policy with { EvaluatedAt = leasedAtUtc };
        return ProtocolCommandResult.Success(status with { Policy = leasedPolicy });
    }

    private bool TryReservePolicyResponseLease(
        PolicySnapshot authoritativePolicy,
        DateTimeOffset sampledAtUtc,
        out DateTimeOffset leasedAtUtc,
        out string? failureCode)
    {
        DateTimeOffset cachedEvaluatedAt = authoritativePolicy.EvaluatedAt;
        long cachedUtcTicks = cachedEvaluatedAt.UtcTicks;
        long candidateUtcTicks = Math.Max(cachedUtcTicks, sampledAtUtc.UtcTicks);
        lock (_policyResponseLeaseSync)
        {
            PolicySnapshot? authoritativePayload = _policyResponseAuthoritativePayload;
            if (authoritativePayload is null)
            {
                _policyResponseLeaseHighWaterUtcTicks = candidateUtcTicks;
                _policyResponseAuthoritativeRevision = authoritativePolicy.Revision;
                _policyResponseAuthoritativePayload = authoritativePolicy;
                leasedAtUtc = candidateUtcTicks == cachedUtcTicks
                    ? cachedEvaluatedAt
                    : new DateTimeOffset(candidateUtcTicks, TimeSpan.Zero);
                failureCode = null;
                return true;
            }

            bool samePayload = authoritativePayload
                .HasEquivalentEnforcementTo(authoritativePolicy);
            long nextUtcTicks;
            if (samePayload)
            {
                _policyResponseAuthoritativeRevision = Math.Max(
                    _policyResponseAuthoritativeRevision,
                    authoritativePolicy.Revision);
                nextUtcTicks = Math.Max(
                    candidateUtcTicks,
                    _policyResponseLeaseHighWaterUtcTicks);
            }
            else
            {
                if (authoritativePolicy.Revision <= _policyResponseAuthoritativeRevision)
                {
                    leasedAtUtc = default;
                    failureCode = "policy-response-authority-conflict";
                    return false;
                }

                if (_policyResponseLeaseHighWaterUtcTicks
                    == DateTimeOffset.MaxValue.UtcTicks)
                {
                    leasedAtUtc = default;
                    failureCode = "policy-response-lease-exhausted";
                    return false;
                }

                nextUtcTicks = candidateUtcTicks > _policyResponseLeaseHighWaterUtcTicks
                    ? candidateUtcTicks
                    : _policyResponseLeaseHighWaterUtcTicks + 1;
                _policyResponseAuthoritativeRevision = authoritativePolicy.Revision;
                _policyResponseAuthoritativePayload = authoritativePolicy;
            }

            _policyResponseLeaseHighWaterUtcTicks = nextUtcTicks;
            leasedAtUtc = nextUtcTicks == cachedUtcTicks
                ? cachedEvaluatedAt
                : new DateTimeOffset(nextUtcTicks, TimeSpan.Zero);
            failureCode = null;
            return true;
        }
    }

    private async ValueTask<ProtocolCommandResult> RequestOverrideAsync(
        RequestOverrideCommand command,
        CancellationToken cancellationToken)
    {
        ActiveOverride? acceptedOverride = null;
        using (IDisposable mutationLease = await mutationGate
                   .EnterAsync(cancellationToken)
                   .ConfigureAwait(false))
        {
            while (true)
            {
                StorageResult<NightState?> stateResult = await stateRepository
                    .ReadActiveStateAsync(cancellationToken)
                    .ConfigureAwait(false);
                StorageResult<ProgressState> progressResult = await progressRepository
                    .ReadProgressAsync(cancellationToken)
                    .ConfigureAwait(false);
                if (stateResult.IsDegraded || progressResult.IsDegraded)
                {
                    return await DegradedAsync("storage-unavailable", cancellationToken)
                        .ConfigureAwait(false);
                }

                if (stateResult.Value is null || stateResult.Value.IsClosed)
                {
                    return ProtocolCommandResult.Success(new
                    {
                        accepted = false,
                        error = "noActiveNight",
                    });
                }

                ClockObservation requestObservation = clock.Observe();
                LogicalTimeResult logicalTime = LogicalTime.Advance(
                    stateResult.Value,
                    requestObservation);
                bool existingOverrideEnded = stateResult.Value.ActiveOverride is { } existingOverride
                    && logicalTime.UtcNow >= existingOverride.EndsAtUtc;
                NightState requestState = stateResult.Value with
                {
                    LastObservedUtc = logicalTime.UtcNow,
                    LastObservedUptime = logicalTime.Uptime,
                    LastObservedBootSessionId = logicalTime.BootSessionId,
                    ActiveOverride = existingOverrideEnded
                        ? null
                        : stateResult.Value.ActiveOverride,
                };
                DateTimeOffset requestedAtUtc = logicalTime.UtcNow;
                OverrideDecision decision = overridePolicy.Request(
                    requestState,
                    progressResult.Value,
                    command.Request,
                    requestedAtUtc);
                if (!decision.Accepted)
                {
                    return ProtocolCommandResult.Success(new
                    {
                        accepted = false,
                        error = decision.Error,
                    });
                }

                IDisposable? snapshotValidationLease = null;
                if (command.Request.Kind == OverrideKind.TeamRescue)
                {
                    try
                    {
                        snapshotValidationLease = overridePolicy
                            .TryAcquireTeamRescueSnapshotValidation(
                                decision.AllowedProcessSnapshotGeneration);
                    }
                    catch (Exception)
                    {
                        return ProtocolCommandResult.Success(new
                        {
                            accepted = false,
                            error = OverrideError.TeamRescueUnavailable,
                        });
                    }

                    if (snapshotValidationLease is null)
                    {
                        continue;
                    }
                }

                using (snapshotValidationLease)
                {
                    NightState acceptedState = decision.State with
                    {
                        LastObservedUtc = requestedAtUtc,
                        LastObservedUptime = logicalTime.Uptime,
                        LastObservedBootSessionId = logicalTime.BootSessionId,
                    };
                    NightEvent nightEvent = new(
                        Guid.NewGuid(),
                        acceptedState.NightId,
                        requestedAtUtc,
                        NightEventKind.OverrideRequested,
                        acceptedState.HighestBasePhaseReached,
                        command.Request.Kind);
                    StorageWriteResult stateWrite = await stateRepository
                        .SaveActiveStateProgressWithEventAsync(
                            acceptedState,
                            decision.Progress,
                            nightEvent,
                            stateResult.Version,
                            progressResult.Version,
                            cancellationToken)
                        .ConfigureAwait(false);
                    if (stateWrite.IsConflict)
                    {
                        continue;
                    }

                    if (stateWrite.IsDegraded)
                    {
                        return await DegradedAsync(
                                "storage-unavailable",
                                cancellationToken)
                            .ConfigureAwait(false);
                    }

                    policyMaintenanceScheduler?.MarkDirty();
                    acceptedOverride = acceptedState.ActiveOverride!;
                    break;
                }
            }
        }

        _ = await TryRefreshPolicyAsync(force: true, cancellationToken)
            .ConfigureAwait(false);
        ActiveOverride activeOverride = acceptedOverride
            ?? throw new InvalidOperationException("Accepted override was not persisted.");
        return ProtocolCommandResult.Success(new
        {
            accepted = true,
            kind = activeOverride.Kind,
            startsAtUtc = activeOverride.StartsAtUtc,
            endsAtUtc = activeOverride.EndsAtUtc,
        });
    }

    private async ValueTask<bool> TryRefreshPolicyAsync(
        bool force,
        CancellationToken cancellationToken)
    {
        if (policyMaintenanceScheduler is null)
        {
            return true;
        }

        try
        {
            await policyMaintenanceScheduler
                .RefreshAsync(force, cancellationToken)
                .ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            try
            {
                await statusPublisher.PublishAsync(
                        ServiceRuntimeStatus.Degraded("policy-maintenance-failure"),
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception)
            {
                // Returning an explicit degraded response keeps the client fail-open
                // even when the in-memory status publisher is unavailable.
            }

            return false;
        }
    }

    private async ValueTask<ProtocolCommandResult> RecordEventAsync(
        RecordEventCommand command,
        CancellationToken cancellationToken)
    {
        if (command.Kind == NightEventKind.WorkstationLocked)
        {
            return await RecordWorkstationLockAsync(cancellationToken).ConfigureAwait(false);
        }

        if (command.Kind is NightEventKind.DeliberateBypass
            or NightEventKind.LateNewEntertainment
            or NightEventKind.MissedLock)
        {
            return await RecordOutcomeFlagAsync(command, cancellationToken).ConfigureAwait(false);
        }

        StorageWriteResult write = await historyRepository.RecordEventAsync(
            new(
                Guid.NewGuid(),
                null,
                clock.UtcNow,
                command.Kind,
                command.BasePhase,
                command.OverrideKind),
            cancellationToken).ConfigureAwait(false);
        return write.IsDegraded
            ? await DegradedAsync("storage-unavailable", cancellationToken).ConfigureAwait(false)
            : ProtocolCommandResult.Success(new { recorded = true });
    }

    private async ValueTask<ProtocolCommandResult> RecordWorkstationLockAsync(
        CancellationToken cancellationToken)
    {
        try
        {
            using IDisposable mutationLease = await mutationGate
                .EnterAsync(cancellationToken)
                .ConfigureAwait(false);
            while (true)
            {
                StorageResult<NightState?> stateResult = await stateRepository
                    .ReadActiveStateAsync(cancellationToken)
                    .ConfigureAwait(false);
                if (stateResult.IsDegraded)
                {
                    return await DegradedAsync("storage-unavailable", cancellationToken)
                        .ConfigureAwait(false);
                }

                NightState? state = stateResult.Value;
                if (state is null || state.IsClosed)
                {
                    return ProtocolCommandResult.Success(
                        new { recorded = false, error = "noActiveNight" });
                }

                if (state.FirstLockObservedAtUtc is not null)
                {
                    return ProtocolCommandResult.Success(new { recorded = true });
                }

                ClockObservation rawObservation = clock.Observe();
                ClockObservation utcObservation = rawObservation with
                {
                    UtcNow = rawObservation.UtcNow.ToUniversalTime(),
                };
                LogicalTimeResult logicalTime = LogicalTime.Advance(state, utcObservation);
                bool overrideEnded = state.ActiveOverride is { } activeOverride
                    && logicalTime.UtcNow >= activeOverride.EndsAtUtc;
                NightState updated = state with
                {
                    LastObservedUtc = logicalTime.UtcNow,
                    LastObservedUptime = logicalTime.Uptime,
                    LastObservedBootSessionId = logicalTime.BootSessionId,
                    ActiveOverride = overrideEnded ? null : state.ActiveOverride,
                    FirstLockObservedAtUtc = logicalTime.UtcNow,
                };
                NightEvent nightEvent = new(
                    Guid.NewGuid(),
                    state.NightId,
                    logicalTime.UtcNow,
                    NightEventKind.WorkstationLocked);
                StorageWriteResult write = await stateRepository
                    .SaveActiveStateWithEventAsync(
                        updated,
                        nightEvent,
                        stateResult.Version,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (write.IsConflict)
                {
                    continue;
                }

                return write.IsDegraded
                    ? await DegradedAsync("storage-unavailable", cancellationToken)
                        .ConfigureAwait(false)
                    : ProtocolCommandResult.Success(new { recorded = true });
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return await DegradedAsync("storage-unavailable", cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private async ValueTask<ProtocolCommandResult> RecordOutcomeFlagAsync(
        RecordEventCommand command,
        CancellationToken cancellationToken)
    {
        using IDisposable mutationLease = await mutationGate
            .EnterAsync(cancellationToken)
            .ConfigureAwait(false);
        while (true)
        {
            StorageResult<NightState?> stateResult = await stateRepository
                .ReadActiveStateAsync(cancellationToken)
                .ConfigureAwait(false);
            if (stateResult.IsDegraded)
            {
                return await DegradedAsync("storage-unavailable", cancellationToken).ConfigureAwait(false);
            }

            if (stateResult.Value is null || stateResult.Value.IsClosed)
            {
                return ProtocolCommandResult.Success(new { recorded = false, error = "noActiveNight" });
            }

            NightState updated = command.Kind switch
            {
                NightEventKind.DeliberateBypass => stateResult.Value with { DeliberateBypass = true },
                NightEventKind.LateNewEntertainment => stateResult.Value with { LateNewEntertainment = true },
                NightEventKind.MissedLock => stateResult.Value with { MissedLock = true },
                _ => stateResult.Value,
            };
            NightEvent nightEvent = new(
                Guid.NewGuid(),
                updated.NightId,
                clock.UtcNow,
                command.Kind,
                command.BasePhase,
                command.OverrideKind);
            StorageWriteResult write = await stateRepository
                .SaveActiveStateWithEventAsync(
                    updated,
                    nightEvent,
                    stateResult.Version,
                    cancellationToken)
                .ConfigureAwait(false);
            if (write.IsConflict)
            {
                continue;
            }

            return write.IsDegraded
                ? await DegradedAsync("storage-unavailable", cancellationToken).ConfigureAwait(false)
                : ProtocolCommandResult.Success(new { recorded = true });
        }
    }

    private async ValueTask<ProtocolCommandResult> RecordChromeHealthAsync(
        RecordChromeHealthCommand command,
        CancellationToken cancellationToken)
    {
        if (DesktopSessionFailureCode() is not null)
        {
            return DesktopSessionCommandRejected();
        }

        if (chromeProtectionHealthRepository is null)
        {
            return await ChromeHealthStorageDegradedAsync(cancellationToken)
                .ConfigureAwait(false);
        }

        if (!string.Equals(
                command.ExtensionId,
                ChromeProtectionHealth.ExpectedExtensionId,
                StringComparison.Ordinal))
        {
            return ChromeHealthRejected();
        }

        try
        {
            using IDisposable mutationLease = await mutationGate
                .EnterAsync(cancellationToken)
                .ConfigureAwait(false);
            DateTimeOffset observedAtUtc = clock.UtcNow.ToUniversalTime();
            for (int attempt = 0; attempt < 3; attempt++)
            {
                StorageResult<ChromeProtectionHealth?> read =
                    await chromeProtectionHealthRepository
                        .ReadChromeProtectionHealthAsync(cancellationToken)
                        .ConfigureAwait(false);
                if (read.IsDegraded)
                {
                    return await ChromeHealthStorageDegradedAsync(cancellationToken)
                        .ConfigureAwait(false);
                }

                if (read.Value is { } current
                    && (observedAtUtc < current.ObservedAtUtc
                        || (!string.Equals(
                                current.ProfileTokenSha256,
                                command.ProfileTokenSha256,
                                StringComparison.Ordinal)
                            && current.IsFreshAt(
                                observedAtUtc,
                                ChromeHealthMaximumAge))))
                {
                    return ChromeHealthRejected();
                }

                ChromeProtectionHealth replacement = new(
                    command.ExtensionId,
                    command.ExtensionVersion,
                    command.ProfileTokenSha256,
                    command.PolicyRevision,
                    command.IncognitoAllowed,
                    observedAtUtc,
                    command.ProtectionReady);
                StorageWriteResult write = await chromeProtectionHealthRepository
                    .SaveChromeProtectionHealthAsync(
                        replacement,
                        read.Version,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (write.IsConflict)
                {
                    continue;
                }

                if (write.IsDegraded)
                {
                    return await ChromeHealthStorageDegradedAsync(cancellationToken)
                        .ConfigureAwait(false);
                }

                return replacement.IsExpectedExtension
                    ? ProtocolCommandResult.Success(new { status = "recorded" })
                    : ChromeHealthRejected();
            }

            return ChromeHealthRejected();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return await ChromeHealthStorageDegradedAsync(cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private static ProtocolCommandResult ChromeHealthRejected() =>
        ProtocolCommandResult.Degraded(new { status = "degraded" });

    private async ValueTask<ProtocolCommandResult> ChromeHealthStorageDegradedAsync(
        CancellationToken cancellationToken)
    {
        await statusPublisher.PublishAsync(
            ServiceRuntimeStatus.Degraded("storage-unavailable"),
            cancellationToken).ConfigureAwait(false);
        return ChromeHealthRejected();
    }

    private async ValueTask<ProtocolCommandResult> ListLegacyTaskMigrationsAsync(
        ListLegacyTaskMigrationsCommand command,
        CancellationToken cancellationToken)
    {
        if (legacyTaskMigrationRepository is null)
        {
            return await DegradedAsync("storage-unavailable", cancellationToken)
                .ConfigureAwait(false);
        }

        try
        {
            using IDisposable mutationLease = await mutationGate
                .EnterAsync(cancellationToken)
                .ConfigureAwait(false);
            StorageResult<IReadOnlyList<LegacyTaskMigrationRecord>> read =
                await legacyTaskMigrationRepository
                    .ReadLegacyTaskMigrationsAsync(cancellationToken)
                    .ConfigureAwait(false);
            if (read.IsDegraded)
            {
                return await DegradedAsync("storage-unavailable", cancellationToken)
                    .ConfigureAwait(false);
            }

            LegacyTaskMigrationRecord[] active = read.Value
                .Where(record => record.Status is LegacyTaskMigrationStatus.Prepared
                    or LegacyTaskMigrationStatus.Disabled
                    or LegacyTaskMigrationStatus.RestorePrepared)
                .ToArray();
            int start = 0;
            if (command.Cursor is { } cursor)
            {
                int cursorIndex = Array.FindIndex(
                    active,
                    record => string.Equals(
                        record.MigrationId,
                        cursor,
                        StringComparison.Ordinal));
                if (cursorIndex < 0)
                {
                    return ProtocolCommandResult.Degraded(new { error = "invalidCursor" });
                }

                start = cursorIndex + 1;
            }

            LegacyTaskMigrationRecord[] page = active
                .Skip(start)
                .Take(LegacyTaskMigrationPageSize)
                .ToArray();
            string? nextCursor = start + page.Length < active.Length
                ? page[^1].MigrationId
                : null;
            return ProtocolCommandResult.Success(new
            {
                migrations = page.Select(ProjectLegacyTaskMigration).ToArray(),
                nextCursor,
                failedCount = read.Value.Count(record =>
                    record.Status == LegacyTaskMigrationStatus.Failed),
            });
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return await DegradedAsync("storage-unavailable", cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private async ValueTask<ProtocolCommandResult> PrepareLegacyTaskMigrationAsync(
        PrepareLegacyTaskMigrationCommand command,
        CancellationToken cancellationToken)
    {
        if (legacyTaskMigrationRepository is null)
        {
            return await DegradedAsync("storage-unavailable", cancellationToken)
                .ConfigureAwait(false);
        }

        try
        {
            using IDisposable mutationLease = await mutationGate
                .EnterAsync(cancellationToken)
                .ConfigureAwait(false);
            StorageResult<IReadOnlyList<LegacyTaskMigrationRecord>> read =
                await legacyTaskMigrationRepository
                    .ReadLegacyTaskMigrationsAsync(cancellationToken)
                    .ConfigureAwait(false);
            if (read.IsDegraded)
            {
                return await DegradedAsync("storage-unavailable", cancellationToken)
                    .ConfigureAwait(false);
            }

            LegacyTaskMigrationRecord[] activeRecords = read.Value
                .Where(record => record.Status is LegacyTaskMigrationStatus.Prepared
                    or LegacyTaskMigrationStatus.Disabled
                    or LegacyTaskMigrationStatus.RestorePrepared)
                .ToArray();
            LegacyTaskMigrationRecord? active = activeRecords.LastOrDefault(record =>
                string.Equals(
                    record.TaskPath,
                    command.TaskPath,
                    StringComparison.OrdinalIgnoreCase));
            if (active is not null)
            {
                if (active.Status == LegacyTaskMigrationStatus.RestorePrepared)
                {
                    return ProtocolCommandResult.Success(new
                    {
                        accepted = false,
                        error = "taskRestorePending",
                    });
                }

                return string.Equals(
                        active.ActionFingerprint,
                        command.ActionFingerprint,
                        StringComparison.Ordinal)
                    && active.OriginalEnabled == command.OriginalEnabled
                    ? ProtocolCommandResult.Success(new
                    {
                        accepted = true,
                        migration = ProjectLegacyTaskMigration(active),
                    })
                    : ProtocolCommandResult.Success(new
                    {
                        accepted = false,
                        error = "taskAlreadyTracked",
                    });
            }

            if (activeRecords.Length >= LegacyTaskMigrationRecord.MaximumActiveRecords)
            {
                return ProtocolCommandResult.Success(new
                {
                    accepted = false,
                    error = "migrationCapacityReached",
                });
            }

            DateTimeOffset preparedAtUtc = clock.UtcNow.ToUniversalTime();
            LegacyTaskMigrationRecord prepared = new(
                Guid.NewGuid().ToString("N"),
                command.TaskPath,
                command.ActionFingerprint,
                command.OriginalEnabled,
                LegacyTaskMigrationStatus.Prepared,
                preparedAtUtc);
            StorageWriteResult write = await legacyTaskMigrationRepository
                .SaveLegacyTaskMigrationAsync(prepared, cancellationToken)
                .ConfigureAwait(false);
            return write.IsDegraded
                ? await DegradedAsync("storage-unavailable", cancellationToken)
                    .ConfigureAwait(false)
                : ProtocolCommandResult.Success(new
                {
                    accepted = true,
                    migration = ProjectLegacyTaskMigration(prepared),
                });
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return await DegradedAsync("storage-unavailable", cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private async ValueTask<ProtocolCommandResult>
        FindLegacyTaskMigrationRecoveryCandidateAsync(
            FindLegacyTaskMigrationRecoveryCandidateCommand command,
            CancellationToken cancellationToken)
    {
        if (legacyTaskMigrationRepository is null)
        {
            return await DegradedAsync("storage-unavailable", cancellationToken)
                .ConfigureAwait(false);
        }

        try
        {
            using IDisposable mutationLease = await mutationGate
                .EnterAsync(cancellationToken)
                .ConfigureAwait(false);
            StorageResult<IReadOnlyList<LegacyTaskMigrationRecord>> read =
                await legacyTaskMigrationRepository
                    .ReadLegacyTaskMigrationsAsync(cancellationToken)
                    .ConfigureAwait(false);
            if (read.IsDegraded)
            {
                return await DegradedAsync("storage-unavailable", cancellationToken)
                    .ConfigureAwait(false);
            }

            LegacyTaskMigrationRecord? candidate = UniqueFailedRecoveryCandidateForPath(
                read.Value,
                command.TaskPath);
            if (candidate is not
                {
                    Status: LegacyTaskMigrationStatus.Failed,
                    OriginalEnabled: true,
                    DisabledStateVerified: false,
                })
            {
                return ProtocolCommandResult.Success(new { found = false });
            }

            long issuedTimestamp = _legacyRecoveryTimeProvider.GetTimestamp();
            PurgeExpiredLegacyRecoveryChallenges(issuedTimestamp);
            _legacyRecoveryChallenges.Remove(candidate.MigrationId);
            if (_legacyRecoveryChallenges.Count >= MaximumLegacyRecoveryChallenges)
            {
                return ProtocolCommandResult.Success(new { found = false });
            }

            string recoveryToken = Convert.ToHexString(
                    RandomNumberGenerator.GetBytes(
                        LegacyTaskMigrationRecord.RecoveryTokenLength / 2))
                .ToLowerInvariant();
            _legacyRecoveryChallenges[candidate.MigrationId] = new(
                candidate.MigrationId,
                candidate.TaskPath,
                candidate.ActionFingerprint,
                candidate.OriginalEnabled,
                recoveryToken,
                issuedTimestamp);

            return ProtocolCommandResult.Success(new
            {
                found = true,
                migration = ProjectLegacyTaskMigration(candidate),
                recoveryToken,
            });
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return await DegradedAsync("storage-unavailable", cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private async ValueTask<ProtocolCommandResult> CompleteLegacyTaskMigrationAsync(
        CompleteLegacyTaskMigrationCommand command,
        CancellationToken cancellationToken)
    {
        if (legacyTaskMigrationRepository is null)
        {
            return await DegradedAsync("storage-unavailable", cancellationToken)
                .ConfigureAwait(false);
        }

        try
        {
            using IDisposable mutationLease = await mutationGate
                .EnterAsync(cancellationToken)
                .ConfigureAwait(false);
            StorageResult<LegacyTaskMigrationRecord?> read =
                await legacyTaskMigrationRepository
                    .ReadLegacyTaskMigrationAsync(command.MigrationId, cancellationToken)
                    .ConfigureAwait(false);
            if (read.IsDegraded)
            {
                return await DegradedAsync("storage-unavailable", cancellationToken)
                    .ConfigureAwait(false);
            }

            if (read.Value is not { } current)
            {
                return ProtocolCommandResult.Success(new
                {
                    accepted = false,
                    error = "migrationNotFound",
                });
            }

            bool isDisabledVerificationRefresh = current.Status ==
                    LegacyTaskMigrationStatus.Disabled
                && command.Status == LegacyTaskMigrationStatus.Disabled
                && !current.DisabledStateVerified;
            if (current.Status == command.Status && !isDisabledVerificationRefresh)
            {
                return ProtocolCommandResult.Success(new
                {
                    accepted = true,
                    migration = ProjectLegacyTaskMigration(current),
                });
            }

            // Direct Prepared/Disabled -> Restored is retained only so a 0.3.3
            // Desktop that overlaps a service-first rolling upgrade can record a
            // restore it already performed. The current Desktop always persists
            // RestorePrepared before touching the scheduled task.
            bool allowed = isDisabledVerificationRefresh || (current.Status switch
            {
                LegacyTaskMigrationStatus.Prepared => command.Status is
                    LegacyTaskMigrationStatus.Disabled or
                    LegacyTaskMigrationStatus.RestorePrepared or
                    LegacyTaskMigrationStatus.Restored or
                    LegacyTaskMigrationStatus.Failed,
                LegacyTaskMigrationStatus.Disabled => command.Status is
                    LegacyTaskMigrationStatus.RestorePrepared or
                    LegacyTaskMigrationStatus.Restored or
                    LegacyTaskMigrationStatus.Failed,
                LegacyTaskMigrationStatus.RestorePrepared => command.Status is
                    LegacyTaskMigrationStatus.Restored or
                    LegacyTaskMigrationStatus.Failed,
                _ => false,
            });
            if (!allowed)
            {
                return ProtocolCommandResult.Success(new
                {
                    accepted = false,
                    error = "invalidTransition",
                });
            }

            DateTimeOffset? completedAtUtc = command.Status ==
                    LegacyTaskMigrationStatus.RestorePrepared
                ? null
                : isDisabledVerificationRefresh
                    && current.CompletedAtUtc is { } existingCompletedAtUtc
                        ? existingCompletedAtUtc
                        : clock.UtcNow.ToUniversalTime();
            if (completedAtUtc is { } completion
                && completion < current.PreparedAtUtc)
            {
                completedAtUtc = current.PreparedAtUtc;
            }

            LegacyTaskMigrationRecord replacement = new(
                current.MigrationId,
                current.TaskPath,
                current.ActionFingerprint,
                current.OriginalEnabled,
                command.Status,
                current.PreparedAtUtc,
                completedAtUtc,
                command.Status == LegacyTaskMigrationStatus.Disabled
                    || current.DisabledStateVerified);
            StorageWriteResult write = await legacyTaskMigrationRepository
                .SaveLegacyTaskMigrationAsync(replacement, cancellationToken)
                .ConfigureAwait(false);
            return write.IsDegraded
                ? await DegradedAsync("storage-unavailable", cancellationToken)
                    .ConfigureAwait(false)
                : ProtocolCommandResult.Success(new
                {
                    accepted = true,
                    migration = ProjectLegacyTaskMigration(replacement),
                });
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return await DegradedAsync("storage-unavailable", cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private async ValueTask<ProtocolCommandResult>
        RecoverLegacyTaskMigrationDisabledAsync(
            RecoverLegacyTaskMigrationDisabledCommand command,
            CancellationToken cancellationToken)
    {
        if (legacyTaskMigrationRepository is null)
        {
            return await DegradedAsync("storage-unavailable", cancellationToken)
                .ConfigureAwait(false);
        }

        try
        {
            using IDisposable mutationLease = await mutationGate
                .EnterAsync(cancellationToken)
                .ConfigureAwait(false);
            long observedTimestamp = _legacyRecoveryTimeProvider.GetTimestamp();
            PurgeExpiredLegacyRecoveryChallenges(observedTimestamp);
            LegacyRecoveryChallenge? challenge =
                ConsumeLegacyRecoveryChallenge(command);
            if (challenge is null
                || !RecoveryTokensEqual(
                    challenge.RecoveryToken,
                    command.RecoveryToken)
                || !string.Equals(
                    challenge.MigrationId,
                    command.MigrationId,
                    StringComparison.Ordinal)
                || !string.Equals(
                    challenge.TaskPath,
                    command.TaskPath,
                    StringComparison.Ordinal)
                || !string.Equals(
                    challenge.ActionFingerprint,
                    command.ActionFingerprint,
                    StringComparison.Ordinal)
                || challenge.OriginalEnabled != command.OriginalEnabled
                || !IsFreshLegacyRecoveryChallenge(challenge, observedTimestamp))
            {
                return ProtocolCommandResult.Success(new
                {
                    accepted = false,
                    error = "invalidRecoveryProof",
                });
            }

            StorageResult<IReadOnlyList<LegacyTaskMigrationRecord>> read =
                await legacyTaskMigrationRepository
                    .ReadLegacyTaskMigrationsAsync(cancellationToken)
                    .ConfigureAwait(false);
            if (read.IsDegraded)
            {
                return await DegradedAsync("storage-unavailable", cancellationToken)
                    .ConfigureAwait(false);
            }

            LegacyTaskMigrationRecord? current = read.Value.SingleOrDefault(record =>
                string.Equals(
                    record.MigrationId,
                    command.MigrationId,
                    StringComparison.Ordinal));
            if (current is null)
            {
                return ProtocolCommandResult.Success(new
                {
                    accepted = false,
                    error = "migrationNotFound",
                });
            }

            LegacyTaskMigrationRecord? candidate = UniqueFailedRecoveryCandidateForPath(
                read.Value,
                current.TaskPath);
            if (current.Status != LegacyTaskMigrationStatus.Failed
                || !current.OriginalEnabled
                || current.DisabledStateVerified
                || !string.Equals(
                    current.TaskPath,
                    command.TaskPath,
                    StringComparison.Ordinal)
                || !string.Equals(
                    current.ActionFingerprint,
                    command.ActionFingerprint,
                    StringComparison.Ordinal)
                || current.OriginalEnabled != command.OriginalEnabled
                || !string.Equals(
                    candidate?.MigrationId,
                    current.MigrationId,
                    StringComparison.Ordinal))
            {
                return ProtocolCommandResult.Success(new
                {
                    accepted = false,
                    error = "invalidTransition",
                });
            }

            LegacyTaskMigrationRecord replacement = new(
                current.MigrationId,
                current.TaskPath,
                current.ActionFingerprint,
                current.OriginalEnabled,
                LegacyTaskMigrationStatus.Disabled,
                current.PreparedAtUtc,
                current.CompletedAtUtc,
                DisabledStateVerified: true);
            StorageWriteResult write = await legacyTaskMigrationRepository
                .SaveLegacyTaskMigrationAsync(replacement, cancellationToken)
                .ConfigureAwait(false);
            return write.IsDegraded
                ? await DegradedAsync("storage-unavailable", cancellationToken)
                    .ConfigureAwait(false)
                : ProtocolCommandResult.Success(new
                {
                    accepted = true,
                    migration = ProjectLegacyTaskMigration(replacement),
                });
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return await DegradedAsync("storage-unavailable", cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private static LegacyTaskMigrationRecord? UniqueFailedRecoveryCandidateForPath(
        IReadOnlyList<LegacyTaskMigrationRecord> records,
        string taskPath)
    {
        LegacyTaskMigrationRecord[] samePath = records
            .Where(record => string.Equals(
                record.TaskPath,
                taskPath,
                StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (samePath.Any(record => record.Status is
                LegacyTaskMigrationStatus.Prepared or
                LegacyTaskMigrationStatus.Disabled or
                LegacyTaskMigrationStatus.RestorePrepared))
        {
            return null;
        }

        LegacyTaskMigrationRecord[] failed = samePath
            .Where(record => record.Status == LegacyTaskMigrationStatus.Failed)
            .ToArray();
        return failed.Length == 1 ? failed[0] : null;
    }

    private void PurgeExpiredLegacyRecoveryChallenges(long observedTimestamp)
    {
        foreach (string migrationId in _legacyRecoveryChallenges
                     .Where(pair => !IsFreshLegacyRecoveryChallenge(
                         pair.Value,
                         observedTimestamp))
                     .Select(pair => pair.Key)
                     .ToArray())
        {
            _legacyRecoveryChallenges.Remove(migrationId);
        }
    }

    private LegacyRecoveryChallenge? ConsumeLegacyRecoveryChallenge(
        RecoverLegacyTaskMigrationDisabledCommand command)
    {
        if (_legacyRecoveryChallenges.Remove(
                command.MigrationId,
                out LegacyRecoveryChallenge? challenge))
        {
            return challenge;
        }

        KeyValuePair<string, LegacyRecoveryChallenge> match =
            _legacyRecoveryChallenges.FirstOrDefault(pair => RecoveryTokensEqual(
                pair.Value.RecoveryToken,
                command.RecoveryToken));
        if (match.Key is null)
        {
            return null;
        }

        _legacyRecoveryChallenges.Remove(match.Key);
        return match.Value;
    }

    private static bool RecoveryTokensEqual(string expected, string actual) =>
        CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(expected),
            Encoding.ASCII.GetBytes(actual));

    private bool IsFreshLegacyRecoveryChallenge(
        LegacyRecoveryChallenge challenge,
        long observedTimestamp)
    {
        TimeSpan age = _legacyRecoveryTimeProvider.GetElapsedTime(
            challenge.IssuedTimestamp,
            observedTimestamp);
        return age >= TimeSpan.Zero && age <= LegacyRecoveryChallengeLifetime;
    }

    private static LegacyTaskMigrationResponse ProjectLegacyTaskMigration(
        LegacyTaskMigrationRecord record) => new(
        record.MigrationId,
        record.TaskPath,
        record.ActionFingerprint,
        record.OriginalEnabled,
        record.Status switch
        {
            LegacyTaskMigrationStatus.Prepared => "prepared",
            LegacyTaskMigrationStatus.Disabled => "disabled",
            LegacyTaskMigrationStatus.RestorePrepared => "restorePrepared",
            LegacyTaskMigrationStatus.Restored => "restored",
            LegacyTaskMigrationStatus.Failed => "failed",
            _ => throw new InvalidDataException("Unknown legacy migration status."),
        },
        record.PreparedAtUtc,
        record.CompletedAtUtc,
        record.DisabledStateVerified);

    private sealed record LegacyRecoveryChallenge(
        string MigrationId,
        string TaskPath,
        string ActionFingerprint,
        bool OriginalEnabled,
        string RecoveryToken,
        long IssuedTimestamp);

    private async ValueTask<ProtocolCommandResult> ClearHistoryAsync(
        CancellationToken cancellationToken)
    {
        StorageWriteResult write = await historyRepository
            .ClearHistoryAsync(cancellationToken)
            .ConfigureAwait(false);
        return write.IsDegraded
            ? await DegradedAsync("storage-unavailable", cancellationToken).ConfigureAwait(false)
            : ProtocolCommandResult.Success(new { cleared = true });
    }

    private string? DesktopSessionFailureCode(string? expectedSessionId = null)
    {
        if (desktopSessionLease is null)
        {
            return null;
        }

        return desktopSessionLease.Observe(expectedSessionId).State switch
        {
            DesktopSessionLeaseState.Active => null,
            DesktopSessionLeaseState.Expired => "desktop-session-expired",
            DesktopSessionLeaseState.Retired => "desktop-session-retired",
            DesktopSessionLeaseState.Invalid => "desktop-session-invalid",
            _ => "desktop-session-inactive",
        };
    }

    private static string DesktopSessionFailureCode(DesktopSessionLeaseState state) =>
        state switch
        {
            DesktopSessionLeaseState.Expired => "desktop-session-expired",
            DesktopSessionLeaseState.Retired => "desktop-session-retired",
            DesktopSessionLeaseState.Invalid => "desktop-session-invalid",
            _ => "desktop-session-inactive",
        };

    private static ProtocolCommandResult DesktopSessionFailOpen(string code) =>
        ProtocolCommandResult.Degraded(ServiceRuntimeStatus.Degraded(code));

    private static ProtocolCommandResult DesktopSessionCommandRejected() =>
        ProtocolCommandResult.Degraded(new { status = "degraded" });

    private async ValueTask<ProtocolCommandResult> DegradedAsync(
        string code,
        CancellationToken cancellationToken)
    {
        await statusPublisher.PublishAsync(
            ServiceRuntimeStatus.Degraded(code),
            cancellationToken).ConfigureAwait(false);
        return ProtocolCommandResult.Degraded(new
        {
            enforcementEnabled = false,
            isDegraded = true,
            degradationCode = code,
        });
    }

    private static ChromeProtectionStatusResponse ProjectChromeProtection(
        StorageResult<ChromeProtectionHealth?> read,
        DateTimeOffset observedAtUtc,
        ServiceRuntimeStatus runtimeStatus)
    {
        if (read.IsDegraded)
        {
            return new("degraded", false, false, null, null);
        }

        if (read.Value is not { } health)
        {
            return new("missing", false, false, null, null);
        }

        string status;
        bool isHealthy;
        if (!health.IsExpectedExtension)
        {
            status = "extensionMismatch";
            isHealthy = false;
        }
        else if (!health.IsFreshAt(observedAtUtc, ChromeHealthMaximumAge))
        {
            status = "stale";
            isHealthy = false;
        }
        else if (!IsVerifiedChromeProtection(health, observedAtUtc, runtimeStatus))
        {
            status = "protectionDegraded";
            isHealthy = false;
        }
        else
        {
            status = "healthy";
            isHealthy = true;
        }

        return new(
            status,
            isHealthy,
            health.IncognitoAllowed,
            health.ObservedAtUtc,
            health.ExtensionVersion);
    }

    private static bool IsVerifiedChromeProtection(
        ChromeProtectionHealth health,
        DateTimeOffset observedAtUtc,
        ServiceRuntimeStatus runtimeStatus)
    {
        PolicySnapshot? policy = runtimeStatus.Policy;
        return health.IsExpectedExtension
            && health.IsFreshAt(observedAtUtc, ChromeHealthMaximumAge)
            && health.ProtectionReady
            && runtimeStatus.EnforcementEnabled
            && !runtimeStatus.IsDegraded
            && policy is { EnforcementEnabled: true, IsDegraded: false }
            && health.PolicyRevision == policy.Revision;
    }

    private sealed record UserStateResponse(
        ProgressState Progress,
        OnboardingState Onboarding,
        RuleSettingsState Rules,
        WeeklyReportSummary WeeklyReport,
        DateOnly CurrentNightDate,
        NightSelfReport? SelfReport,
        ChromeProtectionStatusResponse ChromeProtection);

    private sealed record ChromeProtectionStatusResponse(
        string Status,
        bool IsHealthy,
        bool IncognitoProtected,
        DateTimeOffset? LastHeartbeatAtUtc,
        string? ExtensionVersion);

    private sealed record LegacyTaskMigrationResponse(
        string MigrationId,
        string TaskPath,
        string ActionFingerprint,
        bool OriginalEnabled,
        string Status,
        DateTimeOffset PreparedAtUtc,
        DateTimeOffset? CompletedAtUtc,
        bool DisabledStateVerified);

    private sealed record ProcessPersistenceResponse(
        string Status,
        ProcessPersistenceRecordResponse? Record);

    private sealed record ProcessPersistenceRecordResponse(
        string Slot,
        int SchemaVersion,
        long Version,
        System.Text.Json.JsonElement Payload);
}
