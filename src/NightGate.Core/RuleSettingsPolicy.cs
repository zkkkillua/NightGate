using System.Collections.Immutable;

namespace NightGate.Core;

public static class RuleSettingsPolicy
{
    private static readonly TimeOnly ProtectedStart = new(21, 0);
    private static readonly TimeOnly EditingCutoff = new(22, 30);

    public static RuleSettingsState Save(
        RuleSettingsState current,
        ImmutableArray<AppRule> appRules,
        ImmutableArray<SiteRule> siteRules,
        DateTimeOffset observedAtUtc,
        TimeZoneInfo timeZone)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(timeZone);
        if (observedAtUtc == default || observedAtUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException(
                "Rule settings require a nondefault service-observed UTC time.",
                nameof(observedAtUtc));
        }

        DateTimeOffset local = TimeZoneInfo.ConvertTime(observedAtUtc, timeZone);
        DateOnly localDate = DateOnly.FromDateTime(local.DateTime);
        TimeOnly localTime = TimeOnly.FromDateTime(local.DateTime);
        if (localTime >= ProtectedStart && localTime < EditingCutoff)
        {
            return new(appRules, siteRules);
        }

        DateOnly effectiveNight = localTime < ProtectedStart
            ? localDate
            : localDate.AddDays(1);
        return new(
            current.ActiveAppRules,
            current.ActiveSiteRules,
            appRules,
            siteRules,
            effectiveNight,
            observedAtUtc);
    }

    public static RuleSettingsState Activate(
        RuleSettingsState current,
        DateOnly evaluatedNightDate)
    {
        ArgumentNullException.ThrowIfNull(current);
        if (evaluatedNightDate == default)
        {
            throw new ArgumentOutOfRangeException(nameof(evaluatedNightDate));
        }

        if (current.PendingAppRules is not { } appRules
            || current.PendingSiteRules is not { } siteRules
            || current.PendingEffectiveNightDate is not { } effectiveNight
            || evaluatedNightDate < effectiveNight)
        {
            return current;
        }

        return new(appRules, siteRules);
    }
}
