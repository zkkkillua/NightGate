namespace NightGate.Core;

public static class NightNoticePolicy
{
    private static readonly TimeSpan LastStartWindow = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan PlanLeadTime = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan GraceTenWindow = TimeSpan.FromMinutes(10);

    public static NightNoticeKind? GetDueNotice(
        NightWindow window,
        NightPhase phase,
        DateTimeOffset observedAtUtc,
        DateTimeOffset? earliestGameCutoff = null)
    {
        ArgumentNullException.ThrowIfNull(window);
        if (observedAtUtc == default || observedAtUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException(
                "Notice evaluation requires a nondefault service-observed UTC time.",
                nameof(observedAtUtc));
        }

        DateTimeOffset effectiveLastStart = window.LastStart;
        if (earliestGameCutoff is { } gameCutoff && gameCutoff < effectiveLastStart)
        {
            effectiveLastStart = gameCutoff < window.ProtectedStart
                ? window.ProtectedStart
                : gameCutoff;
        }

        bool lastStartCatchupIsDue = observedAtUtc >= effectiveLastStart
            && observedAtUtc < window.Lock.Subtract(GraceTenWindow);
        if (phase == NightPhase.Free)
        {
            if (lastStartCatchupIsDue)
            {
                return NightNoticeKind.LastStart;
            }

            DateTimeOffset planWindowStart = effectiveLastStart.Subtract(PlanLeadTime);
            if (planWindowStart < window.ProtectedStart)
            {
                planWindowStart = window.ProtectedStart;
            }

            return observedAtUtc >= planWindowStart
                && observedAtUtc < effectiveLastStart
                    ? NightNoticeKind.IfThenPlan
                    : null;
        }

        if (phase == NightPhase.LastStart)
        {
            return lastStartCatchupIsDue
                && observedAtUtc < window.LastStart.Add(LastStartWindow)
                    ? NightNoticeKind.LastStart
                    : null;
        }

        if (phase != NightPhase.Grace
            || observedAtUtc < window.LastStart.Add(LastStartWindow)
            || observedAtUtc >= window.Lock)
        {
            return null;
        }

        if (window.Lock - observedAtUtc <= GraceTenWindow)
        {
            // The final ten minutes are continuously represented by the compact
            // countdown overlay. Avoid adding disposable 10/2 minute balloons.
            return null;
        }

        return lastStartCatchupIsDue ? NightNoticeKind.LastStart : null;
    }
}
