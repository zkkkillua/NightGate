namespace NightGate.Core;

public interface INightStateRepository
{
    ValueTask<StorageResult<NightState?>> ReadActiveStateAsync(
        CancellationToken cancellationToken = default);

    ValueTask<StorageWriteResult> SaveActiveStateWithEventAsync(
        NightState state,
        NightEvent nightEvent,
        long? expectedVersion = null,
        CancellationToken cancellationToken = default);

    ValueTask<StorageWriteResult> SaveActiveStateProgressWithEventAsync(
        NightState state,
        ProgressState progress,
        NightEvent nightEvent,
        long? expectedStateVersion = null,
        long? expectedProgressVersion = null,
        CancellationToken cancellationToken = default);

    ValueTask<StorageWriteResult> CloseActiveStateWithOutcomeAndEventAsync(
        NightState closedState,
        NightOutcome outcome,
        NightEvent nightEvent,
        long? expectedVersion = null,
        CancellationToken cancellationToken = default);
}

public interface IProgressRepository
{
    ValueTask<StorageResult<ProgressState>> ReadProgressAsync(
        CancellationToken cancellationToken = default);

    ValueTask<StorageWriteResult> SaveProgressAsync(
        ProgressState progress,
        long? expectedVersion = null,
        CancellationToken cancellationToken = default);
}

public interface IHistoryRepository
{
    ValueTask<StorageResult<IReadOnlyList<NightOutcome>>> ReadLatestOutcomesAsync(
        int count,
        CancellationToken cancellationToken = default);

    ValueTask<StorageResult<IReadOnlyList<NightOutcome>>> ReadLatestEligibleOutcomesAsync(
        int count,
        CancellationToken cancellationToken = default);

    ValueTask<StorageWriteResult> SaveOutcomeAsync(
        NightOutcome outcome,
        CancellationToken cancellationToken = default);

    ValueTask<StorageWriteResult> RecordEventAsync(
        NightEvent nightEvent,
        CancellationToken cancellationToken = default);

    ValueTask<StorageWriteResult> PurgeEventsOlderThanAsync(
        DateTimeOffset cutoffUtc,
        CancellationToken cancellationToken = default);

    ValueTask<StorageWriteResult> ClearHistoryAsync(
        CancellationToken cancellationToken = default);
}

public interface IBrowserEventRepository
{
    ValueTask<StorageWriteResult> RecordBrowserEventAsync(
        BrowserPrivacyEvent browserEvent,
        CancellationToken cancellationToken = default);

    ValueTask<StorageWriteResult> SaveLateNewEntertainmentWithBrowserEventAsync(
        NightState state,
        BrowserPrivacyEvent browserEvent,
        long? expectedVersion = null,
        CancellationToken cancellationToken = default);
}

public interface IOnboardingRepository
{
    ValueTask<StorageResult<OnboardingState>> ReadOnboardingAsync(
        CancellationToken cancellationToken = default);

    ValueTask<StorageWriteResult> SaveOnboardingAsync(
        OnboardingState state,
        long? expectedVersion = null,
        CancellationToken cancellationToken = default);
}

public interface IChromeProtectionHealthRepository
{
    ValueTask<StorageResult<ChromeProtectionHealth?>> ReadChromeProtectionHealthAsync(
        CancellationToken cancellationToken = default);

    ValueTask<StorageWriteResult> SaveChromeProtectionHealthAsync(
        ChromeProtectionHealth health,
        long? expectedVersion = null,
        CancellationToken cancellationToken = default);
}

public interface IRuleSettingsRepository
{
    ValueTask<StorageResult<RuleSettingsState>> ReadRuleSettingsAsync(
        CancellationToken cancellationToken = default);

    ValueTask<StorageWriteResult> SaveRuleSettingsAsync(
        RuleSettingsState state,
        long? expectedVersion = null,
        CancellationToken cancellationToken = default);
}

public interface INightSelfReportRepository
{
    ValueTask<StorageResult<NightSelfReport?>> ReadSelfReportAsync(
        DateOnly nightDate,
        CancellationToken cancellationToken = default);

    ValueTask<StorageWriteResult> SaveSelfReportAsync(
        NightSelfReport report,
        CancellationToken cancellationToken = default);
}

public interface INoticeClaimRepository
{
    ValueTask<StorageResult<bool>> TryClaimNoticeAsync(
        NoticeClaim claim,
        CancellationToken cancellationToken = default);

    ValueTask<StorageWriteResult> PurgeNoticeClaimsOlderThanAsync(
        DateTimeOffset cutoffUtc,
        CancellationToken cancellationToken = default);
}

public interface ILegacyTaskMigrationRepository
{
    ValueTask<StorageResult<LegacyTaskMigrationRecord?>> ReadLegacyTaskMigrationAsync(
        string migrationId,
        CancellationToken cancellationToken = default);

    ValueTask<StorageResult<IReadOnlyList<LegacyTaskMigrationRecord>>>
        ReadLegacyTaskMigrationsAsync(CancellationToken cancellationToken = default);

    ValueTask<StorageWriteResult> SaveLegacyTaskMigrationAsync(
        LegacyTaskMigrationRecord record,
        CancellationToken cancellationToken = default);
}
