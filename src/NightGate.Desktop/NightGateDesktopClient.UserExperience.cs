using System.Text.Json;
using NightGate.Core;

namespace NightGate.Desktop;

public sealed partial class NightGateDesktopClient
{
    public async ValueTask<DesktopUserStateResult> GetUserStateAsync(
        CancellationToken cancellationToken = default)
    {
        try
        {
            string requestId = NextRequestId();
            ReadOnlyMemory<byte> response = await _transport
                .ExchangeAsync(
                    CreateRequest("getUserState", requestId, new { }),
                    cancellationToken)
                .ConfigureAwait(false);
            ResponseWrapper<DesktopUserStateDto> wrapper = DecodeResponse<DesktopUserStateDto>(
                response,
                "getUserStateResult",
                requestId);
            if (wrapper.Status != "success" || !IsUsableUserState(wrapper.Data))
            {
                return DesktopUserStateResult.Unavailable("service-degraded");
            }

            return new(true, null, wrapper.Data);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return DesktopUserStateResult.Unavailable(FailOpenCode);
        }
    }

    public async ValueTask<DesktopOnboardingMutationResult> CompleteOnboardingStepAsync(
        DesktopOnboardingStepRequest request,
        CancellationToken cancellationToken = default)
    {
        ValidateOnboardingRequest(request);
        try
        {
            string requestId = NextRequestId();
            ReadOnlyMemory<byte> response = await _transport
                .ExchangeAsync(
                    CreateRequest("completeOnboardingStep", requestId, request),
                    cancellationToken)
                .ConfigureAwait(false);
            ResponseWrapper<OnboardingMutationResponseDto> wrapper =
                DecodeResponse<OnboardingMutationResponseDto>(
                    response,
                    "completeOnboardingStepResult",
                    requestId);
            if (wrapper.Status != "success")
            {
                return new(false, "service-degraded", null);
            }

            OnboardingMutationResponseDto data = wrapper.Data;
            if (data.Accepted)
            {
                if (data.Error is not null
                    || data.Onboarding is null
                    || !IsUsableOnboarding(data.Onboarding)
                    || data.Onboarding.CompletedStep != request.Step)
                {
                    throw new JsonException("Accepted onboarding response is malformed.");
                }

                return new(true, null, data.Onboarding);
            }

            if (string.IsNullOrWhiteSpace(data.Error) || data.Onboarding is not null)
            {
                throw new JsonException("Rejected onboarding response is malformed.");
            }

            return new(false, data.Error, null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return new(false, FailOpenCode, null);
        }
    }

    public async ValueTask<DesktopSelfReportMutationResult> SaveNightSelfReportAsync(
        DateOnly nightDate,
        bool? phoneOutOfReach,
        bool? wakeWithinWindow,
        CancellationToken cancellationToken = default)
    {
        if (nightDate == default)
        {
            throw new ArgumentOutOfRangeException(nameof(nightDate));
        }

        try
        {
            string requestId = NextRequestId();
            SelfReportRequestPayload payload = new(
                nightDate,
                JsonSerializer.SerializeToElement(phoneOutOfReach, DesktopJson.Options),
                JsonSerializer.SerializeToElement(wakeWithinWindow, DesktopJson.Options));
            ReadOnlyMemory<byte> response = await _transport
                .ExchangeAsync(
                    CreateRequest("saveNightSelfReport", requestId, payload),
                    cancellationToken)
                .ConfigureAwait(false);
            ResponseWrapper<SelfReportMutationResponseDto> wrapper =
                DecodeResponse<SelfReportMutationResponseDto>(
                    response,
                    "saveNightSelfReportResult",
                    requestId);
            if (wrapper.Status != "success")
            {
                return new(false, "service-degraded", null);
            }

            SelfReportMutationResponseDto data = wrapper.Data;
            if (data.Saved)
            {
                if (data.Error is not null
                    || data.SelfReport is null
                    || !IsUsableSelfReport(data.SelfReport)
                    || data.SelfReport.NightDate != nightDate
                    || data.SelfReport.PhoneOutOfReach != phoneOutOfReach
                    || data.SelfReport.WakeWithinWindow != wakeWithinWindow)
                {
                    throw new JsonException("Saved self-report response is malformed.");
                }

                return new(true, null, data.SelfReport);
            }

            if (string.IsNullOrWhiteSpace(data.Error) || data.SelfReport is not null)
            {
                throw new JsonException("Rejected self-report response is malformed.");
            }

            return new(false, data.Error, null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return new(false, FailOpenCode, null);
        }
    }

    public async ValueTask<DesktopRuleSettingsMutationResult> SaveRuleSettingsAsync(
        IReadOnlyList<DesktopAppRuleDraft> appRules,
        IReadOnlyList<string> siteDomains,
        CancellationToken cancellationToken = default)
    {
        RuleSettingsRequestPayload payload = CreateRuleSettingsPayload(appRules, siteDomains);
        try
        {
            string requestId = NextRequestId();
            ReadOnlyMemory<byte> response = await _transport
                .ExchangeAsync(
                    CreateRequest("saveRuleSettings", requestId, payload),
                    cancellationToken)
                .ConfigureAwait(false);
            ResponseWrapper<RuleSettingsMutationResponseDto> wrapper =
                DecodeResponse<RuleSettingsMutationResponseDto>(
                    response,
                    "saveRuleSettingsResult",
                    requestId);
            if (wrapper.Status != "success")
            {
                return new(false, "service-degraded", null, false, false, null);
            }

            RuleSettingsMutationResponseDto data = wrapper.Data;
            if (!data.Saved
                || data.Error is not null
                || data.Rules is null
                || !IsUsableRuleSettings(data.Rules)
                || data.AppliesImmediately is not { } appliesImmediately
                || data.AppliesTonight is not { } appliesTonight
                || data.EffectiveNight is { } effective && effective == default
                || appliesImmediately
                    && (!appliesTonight
                        || data.EffectiveNight is not null
                        || data.Rules.PendingAppRules is not null
                        || data.Rules.PendingSiteRules is not null
                        || data.Rules.PendingEffectiveNightDate is not null
                        || data.Rules.PendingSavedAtUtc is not null)
                || !appliesImmediately
                    && (data.EffectiveNight is null
                        || data.Rules.PendingEffectiveNightDate != data.EffectiveNight
                        || data.Rules.PendingSavedAtUtc is null)
                || !MatchesSavedRules(data.Rules, payload, appliesImmediately))
            {
                throw new JsonException("Saved rule-settings response is malformed.");
            }

            return new(
                true,
                null,
                data.Rules,
                appliesImmediately,
                appliesTonight,
                data.EffectiveNight);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return new(false, FailOpenCode, null, false, false, null);
        }
    }

    public async ValueTask<DesktopNoticeClaimResult> ClaimDueNoticeAsync(
        CancellationToken cancellationToken = default)
    {
        try
        {
            string requestId = NextRequestId();
            ReadOnlyMemory<byte> response = await _transport
                .ExchangeAsync(
                    CreateRequest("claimDueNotice", requestId, new { }),
                    cancellationToken)
                .ConfigureAwait(false);
            ResponseWrapper<NoticeClaimResponseDto> wrapper =
                DecodeResponse<NoticeClaimResponseDto>(
                    response,
                    "claimDueNoticeResult",
                    requestId);
            if (wrapper.Status != "success")
            {
                return new(false, "service-degraded", null, null);
            }

            NoticeClaimResponseDto data = wrapper.Data;
            if (!data.Claimed)
            {
                if (data.Kind is not null || data.NightDate is not null)
                {
                    throw new JsonException("Empty notice claim is malformed.");
                }

                return new(false, null, null, null);
            }

            if (data.Kind is not { } kind
                || !Enum.IsDefined(kind)
                || data.NightDate is not { } nightDate
                || nightDate == default)
            {
                throw new JsonException("Claimed notice is malformed.");
            }

            return new(true, null, kind, nightDate);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return new(false, FailOpenCode, null, null);
        }
    }

    public async ValueTask<DesktopIPhoneProgressionResult> ConfirmIPhoneProgressionAsync(
        int step,
        DesktopIPhoneChecklist checklist,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(checklist);
        if (step is < 2 or > 4 || !checklist.IsComplete)
        {
            throw new ArgumentException("iPhone progression confirmation is incomplete.");
        }

        try
        {
            string requestId = NextRequestId();
            ReadOnlyMemory<byte> response = await _transport
                .ExchangeAsync(
                    CreateRequest(
                        "confirmIPhoneStep",
                        requestId,
                        new IPhoneProgressionRequestPayload(
                            step,
                            new(
                                checklist.HealthSleepScheduleConfigured,
                                checklist.SleepFocusConfigured,
                                checklist.DowntimeConfigured,
                                checklist.BlockAtDowntimeEnabled,
                                checklist.RequiredAppsAllowed,
                                checklist.SafariNotAllowlisted,
                                checklist.DistinctRecoverableScreenTimePasscodeAcknowledged,
                                checklist.OldAlarmsChecked,
                                checklist.PhonePlacementPlanned,
                                checklist.EntertainmentCategoriesRestricted))),
                    cancellationToken)
                .ConfigureAwait(false);
            ResponseWrapper<IPhoneProgressionResponseDto> wrapper =
                DecodeResponse<IPhoneProgressionResponseDto>(
                    response,
                    "confirmIPhoneStepResult",
                    requestId);
            if (wrapper.Status != "success")
            {
                return new(false, "service-degraded", null, null);
            }

            IPhoneProgressionResponseDto data = wrapper.Data;
            if (data.Accepted)
            {
                if (data.Error is not null
                    || data.PendingStep != step
                    || data.EffectiveNightDate is not { } effectiveNight
                    || effectiveNight == default)
                {
                    throw new JsonException("Accepted iPhone confirmation is malformed.");
                }

                return new(true, null, data.PendingStep, effectiveNight);
            }

            if (string.IsNullOrWhiteSpace(data.Error)
                || data.PendingStep is not null
                || data.EffectiveNightDate is not null)
            {
                throw new JsonException("Rejected iPhone confirmation is malformed.");
            }

            return new(false, data.Error, null, null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return new(false, FailOpenCode, null, null);
        }
    }

    public async ValueTask<DesktopClearHistoryResult> ClearHistoryAsync(
        CancellationToken cancellationToken = default)
    {
        try
        {
            string requestId = NextRequestId();
            ReadOnlyMemory<byte> response = await _transport
                .ExchangeAsync(
                    CreateRequest("clearHistory", requestId, new { }),
                    cancellationToken)
                .ConfigureAwait(false);
            ResponseWrapper<ClearHistoryResponseDto> wrapper =
                DecodeResponse<ClearHistoryResponseDto>(
                    response,
                    "clearHistoryResult",
                    requestId);
            return wrapper.Status == "success" && wrapper.Data.Cleared
                ? new(true, null)
                : new(false, "service-degraded");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return new(false, FailOpenCode);
        }
    }

    private static bool IsUsableUserState(DesktopUserStateDto state) =>
        state.Progress is not null
        && state.Onboarding is not null
        && state.Rules is not null
        && state.WeeklyReport is not null
        && state.ChromeProtection is not null
        && state.CurrentNightDate != default
        && IsUsableProgress(state.Progress)
        && IsUsableOnboarding(state.Onboarding)
        && IsUsableRuleSettings(state.Rules)
        && IsUsableWeeklyReport(state.WeeklyReport)
        && IsUsableChromeProtection(state.ChromeProtection)
        && (state.SelfReport is null
            || IsUsableSelfReport(state.SelfReport)
                && state.SelfReport.NightDate == state.CurrentNightDate);

    private static bool IsUsableProgress(DesktopProgressStateDto progress)
    {
        if (progress.CurrentStep is < 1 or > 4
            || !IsOptionalUtc(progress.LastTeamRescueAtUtc)
            || !IsOptionalUtc(progress.PendingStepConfirmedAtUtc)
            || progress.LastProgressionNightDate is { } lastProgression
                && lastProgression == default
            || progress.PendingStepUnlockedByNightDate is { } pendingUnlocked
                && pendingUnlocked == default
            || progress.PendingStepEffectiveNightDate is { } pendingEffective
                && pendingEffective == default)
        {
            return false;
        }

        if (progress.PendingStep is null)
        {
            return progress.PendingStepUnlockedByNightDate is null
                && progress.PendingStepConfirmedAtUtc is null
                && progress.PendingStepEffectiveNightDate is null;
        }

        return progress.CurrentStep < 4
            && progress.PendingStep == progress.CurrentStep + 1
            && progress.PendingStepUnlockedByNightDate is { } unlockedBy
            && progress.LastProgressionNightDate is { } lastEvaluated
            && lastEvaluated >= unlockedBy
            && (progress.PendingStepConfirmedAtUtc is null)
                == (progress.PendingStepEffectiveNightDate is null)
            && (progress.PendingStepEffectiveNightDate is not { } effective
                || effective >= unlockedBy);
    }

    private static bool IsUsableOnboarding(DesktopOnboardingStateDto onboarding) =>
        onboarding.WizardVersion == 1
        && onboarding.CompletedStep is >= 0 and <= 5
        && onboarding.IPhoneConfirmedThroughStep is >= 0 and <= 4
        && (onboarding.CompletedStep < 3
            || (onboarding.ChromeVerified
                    && (onboarding.IncognitoProtected
                        || onboarding.IncognitoWarningAcknowledged))
                || onboarding.ChromeDegradedAcknowledged)
        && IsOptionalUtc(onboarding.CompletedAtUtc)
        && (onboarding.CompletedAtUtc is null || onboarding.CompletedStep == 5);

    private static bool IsUsableRuleSettings(DesktopRuleSettingsStateDto settings)
    {
        if (!IsUsableAppRules(settings.ActiveAppRules)
            || !IsUsableSiteRules(settings.ActiveSiteRules))
        {
            return false;
        }

        bool hasPendingApps = settings.PendingAppRules is not null;
        if (hasPendingApps != (settings.PendingSiteRules is not null)
            || hasPendingApps != (settings.PendingEffectiveNightDate is not null)
            || hasPendingApps != (settings.PendingSavedAtUtc is not null))
        {
            return false;
        }

        return !hasPendingApps
            || settings.PendingEffectiveNightDate != default
                && IsOptionalUtc(settings.PendingSavedAtUtc)
                && IsUsableAppRules(settings.PendingAppRules!)
                && IsUsableSiteRules(settings.PendingSiteRules!);
    }

    private static bool IsUsableAppRules(IReadOnlyList<DesktopAppRuleDto>? rules)
    {
        if (rules is null || rules.Count > RuleSettingsState.MaximumRulesPerSet)
        {
            return false;
        }

        HashSet<string> ids = new(StringComparer.OrdinalIgnoreCase);
        HashSet<string> roots = new(StringComparer.OrdinalIgnoreCase);
        return rules.All(rule =>
            rule is not null
            && IsFullyConfiguredRule(rule)
            && ids.Add(rule.Id)
            && roots.Add(rule.RootExecutablePath!));
    }

    private static bool IsUsableSiteRules(IReadOnlyList<DesktopSiteRuleDto>? rules)
    {
        if (rules is null || rules.Count > RuleSettingsState.MaximumRulesPerSet)
        {
            return false;
        }

        string? previous = null;
        foreach (DesktopSiteRuleDto? rule in rules)
        {
            if (rule is null
                || !SiteRuleDomainNormalizer.TryNormalize(rule.Domain, out string normalized)
                || !string.Equals(rule.Domain, normalized, StringComparison.Ordinal)
                || !SupportedEntertainmentSiteCatalog.IsSupported(normalized)
                || previous is not null && string.CompareOrdinal(previous, normalized) >= 0)
            {
                return false;
            }

            previous = normalized;
        }

        return true;
    }

    private static bool IsUsableWeeklyReport(DesktopWeeklyReportSummaryDto report)
    {
        if (report.OverrideReasons is null
            || report.PeriodStart == default
            || report.PeriodEnd == default
            || report.PeriodEnd.DayNumber - report.PeriodStart.DayNumber != 6
            || report.ObservedWorkNights is < 0 or > 5
            || report.EligibleWorkNights is < 0 or > 5
            || report.QualifyingWorkNights is < 0 or > 5
            || report.EligibleWorkNights > report.ObservedWorkNights
            || report.QualifyingWorkNights > report.EligibleWorkNights
            || report.LockObservations is < 0 or > 5
            || report.LockObservations > report.ObservedWorkNights
            || (report.LockObservations == 0) != (report.MedianLockTime is null)
            || report.MedianLockChangeMinutes is not null && report.MedianLockTime is null)
        {
            return false;
        }

        int[] counts =
        [
            report.OverrideReasons.TeamRescueCount,
            report.OverrideReasons.EntertainmentCount,
            report.OverrideReasons.EmergencyHealthCount,
            report.OverrideReasons.EmergencySafetyCount,
            report.OverrideReasons.EmergencyUrgentWorkCount,
            report.OverrideReasons.EmergencyOtherCount,
        ];
        return counts.All(value => value is >= 0 and <= OverrideReasonSummary.MaximumCount);
    }

    private static bool IsUsableSelfReport(DesktopNightSelfReportDto report) =>
        report.NightDate != default
        && report.UpdatedAtUtc != default
        && report.UpdatedAtUtc.Offset == TimeSpan.Zero;

    private static bool IsUsableChromeProtection(
        DesktopChromeProtectionStatusDto protection)
    {
        if (protection.Status is not (
                "healthy" or
                "missing" or
                "stale" or
                "extensionMismatch" or
                "protectionDegraded" or
                "degraded")
            || protection.IsHealthy != (protection.Status == "healthy"))
        {
            return false;
        }

        bool hasObservation = protection.Status is
            "healthy" or "stale" or "extensionMismatch" or "protectionDegraded";
        if (hasObservation != (protection.LastHeartbeatAtUtc is not null)
            || hasObservation != (protection.ExtensionVersion is not null)
            || !IsOptionalUtc(protection.LastHeartbeatAtUtc))
        {
            return false;
        }

        if (!hasObservation)
        {
            return true;
        }

        string version = protection.ExtensionVersion!;
        string[] components = version.Split('.', StringSplitOptions.None);
        return version.Length <= ChromeProtectionHealth.MaximumExtensionVersionLength
            && components.Length is >= 1 and <= 4
            && components.All(component =>
                component.Length > 0
                && component.All(char.IsAsciiDigit)
                && ushort.TryParse(component, out _));
    }

    private static bool IsOptionalUtc(DateTimeOffset? value) =>
        value is null || value.Value != default && value.Value.Offset == TimeSpan.Zero;

    private static RuleSettingsRequestPayload CreateRuleSettingsPayload(
        IReadOnlyList<DesktopAppRuleDraft> appRules,
        IReadOnlyList<string> siteDomains)
    {
        ArgumentNullException.ThrowIfNull(appRules);
        ArgumentNullException.ThrowIfNull(siteDomains);
        if (appRules.Count > RuleSettingsState.MaximumRulesPerSet
            || siteDomains.Count > RuleSettingsState.MaximumRulesPerSet)
        {
            throw new ArgumentException("Too many rule settings were supplied.");
        }

        var projectedApps = new List<AppRuleRequestPayload>(appRules.Count);
        HashSet<string> ids = new(StringComparer.OrdinalIgnoreCase);
        HashSet<string> roots = new(StringComparer.OrdinalIgnoreCase);
        foreach (DesktopAppRuleDraft? draft in appRules)
        {
            if (draft is null || draft.HelperExecutablePaths is null)
            {
                throw new ArgumentException("An application rule is incomplete.", nameof(appRules));
            }

            AppRuleCategory category = draft.Category switch
            {
                DesktopAppRuleCategory.Game => AppRuleCategory.Game,
                DesktopAppRuleCategory.Voice => AppRuleCategory.Voice,
                _ => throw new ArgumentOutOfRangeException(nameof(appRules)),
            };
            AppRule canonical = new(
                draft.Id,
                draft.RootExecutablePath,
                draft.HelperExecutablePaths,
                category,
                draft.SessionMinutes);
            if (!ids.Add(canonical.Id) || !roots.Add(canonical.RootExecutablePath!))
            {
                throw new ArgumentException("Application rules must be unique.", nameof(appRules));
            }

            projectedApps.Add(new(
                canonical.Id,
                canonical.RootExecutablePath!,
                canonical.HelperExecutablePaths,
                draft.Category,
                canonical.SessionMinutes));
        }

        SiteRuleRequestPayload[] projectedSites = siteDomains
            .Select(domain =>
            {
                if (!SiteRuleDomainNormalizer.TryNormalize(domain, out string normalized)
                    || !SupportedEntertainmentSiteCatalog.IsSupported(normalized))
                {
                    throw new ArgumentException(
                        "Only supported entertainment sites can be selected.",
                        nameof(siteDomains));
                }

                return normalized;
            })
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .Select(domain => new SiteRuleRequestPayload(domain))
            .ToArray();
        return new(projectedApps, projectedSites);
    }

    private static bool MatchesSavedRules(
        DesktopRuleSettingsStateDto settings,
        RuleSettingsRequestPayload expected,
        bool appliesImmediately)
    {
        IReadOnlyList<DesktopAppRuleDto>? apps = appliesImmediately
            ? settings.ActiveAppRules
            : settings.PendingAppRules;
        IReadOnlyList<DesktopSiteRuleDto>? sites = appliesImmediately
            ? settings.ActiveSiteRules
            : settings.PendingSiteRules;
        if (apps is null
            || sites is null
            || apps.Count != expected.AppRules.Count
            || sites.Count != expected.SiteRules.Count)
        {
            return false;
        }

        for (int index = 0; index < apps.Count; index++)
        {
            DesktopAppRuleDto actual = apps[index];
            AppRuleRequestPayload wanted = expected.AppRules[index];
            if (!string.Equals(actual.Id, wanted.Id, StringComparison.Ordinal)
                || !string.Equals(
                    actual.RootExecutablePath,
                    wanted.RootExecutablePath,
                    StringComparison.OrdinalIgnoreCase)
                || actual.Category != wanted.Category
                || actual.SessionMinutes != wanted.SessionMinutes
                || !actual.HelperExecutablePaths.SequenceEqual(
                    wanted.HelperExecutablePaths,
                    StringComparer.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return sites.Select(site => site.Domain)
            .SequenceEqual(expected.SiteRules.Select(site => site.Domain), StringComparer.Ordinal);
    }

    private static void ValidateOnboardingRequest(DesktopOnboardingStepRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Step is < 0 or > 5
            || request.IPhoneConfirmedThroughStep is < 0 or > 4
            || request.Step >= 3
                && !((request.ChromeVerified
                        && (request.IncognitoProtected
                            || request.IncognitoWarningAcknowledged))
                    || request.ChromeDegradedAcknowledged))
        {
            throw new ArgumentException("Onboarding step request is invalid.", nameof(request));
        }
    }

    private sealed record OnboardingMutationResponseDto(
        bool Accepted,
        string? Error = null,
        DesktopOnboardingStateDto? Onboarding = null);

    private sealed record SelfReportMutationResponseDto(
        bool Saved,
        string? Error = null,
        DesktopNightSelfReportDto? SelfReport = null);

    private sealed record SelfReportRequestPayload(
        DateOnly NightDate,
        JsonElement PhoneOutOfReach,
        JsonElement WakeWithinWindow);

    private sealed record AppRuleRequestPayload(
        string Id,
        string RootExecutablePath,
        IReadOnlyList<string> HelperExecutablePaths,
        DesktopAppRuleCategory Category,
        int SessionMinutes);

    private sealed record SiteRuleRequestPayload(string Domain);

    private sealed record RuleSettingsRequestPayload(
        IReadOnlyList<AppRuleRequestPayload> AppRules,
        IReadOnlyList<SiteRuleRequestPayload> SiteRules);

    private sealed record RuleSettingsMutationResponseDto(
        bool Saved,
        DesktopRuleSettingsStateDto? Rules = null,
        bool? AppliesImmediately = null,
        bool? AppliesTonight = null,
        DateOnly? EffectiveNight = null,
        string? Error = null);

    private sealed record NoticeClaimResponseDto(
        bool Claimed,
        DesktopNightNoticeKind? Kind = null,
        DateOnly? NightDate = null);

    private sealed record IPhoneProgressionRequestPayload(
        int Step,
        IPhoneChecklistRequestPayload Checklist);

    private sealed record IPhoneChecklistRequestPayload(
        bool HealthSleepScheduleConfigured,
        bool SleepFocusConfigured,
        bool DowntimeConfigured,
        bool BlockAtDowntimeEnabled,
        bool RequiredAppsAllowed,
        bool SafariNotAllowlisted,
        bool DistinctRecoverableScreenTimePasscodeAcknowledged,
        bool OldAlarmsChecked,
        bool PhonePlacementPlanned,
        bool EntertainmentCategoriesRestricted);

    private sealed record IPhoneProgressionResponseDto(
        bool Accepted,
        int? PendingStep = null,
        DateOnly? EffectiveNightDate = null,
        string? Error = null);

    private sealed record ClearHistoryResponseDto(bool Cleared);
}
