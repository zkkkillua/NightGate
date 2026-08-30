namespace NightGate.Core;

public static class ProgressionEngine
{
    private const int MaximumStep = 4;
    private const int RequiredOutcomeCount = 4;
    private const int RequiredQualifyingCount = 3;
    private static readonly TimeOnly ConfirmationCutoff = new(22, 30);

    public static ProgressState Advance(
        ProgressState current,
        IEnumerable<NightOutcome> outcomes)
    {
        ProgressStateRules.Validate(current);
        ArgumentNullException.ThrowIfNull(outcomes);
        NightOutcome[] latestEligible = outcomes
            .Where(outcome => outcome.IsEligible)
            .OrderByDescending(outcome => outcome.ClosedAtUtc)
            .ThenByDescending(outcome => outcome.NightDate)
            .ThenByDescending(outcome => outcome.NightId)
            .Take(RequiredOutcomeCount)
            .ToArray();

        if (latestEligible.Length < RequiredOutcomeCount)
        {
            return current;
        }

        DateOnly newestNightDate = latestEligible[0].NightDate;
        if (current.LastProgressionNightDate is { } lastEvaluated
            && newestNightDate <= lastEvaluated)
        {
            return current;
        }

        ProgressState evaluated = current with
        {
            LastProgressionNightDate = newestNightDate,
        };
        if (current.CurrentStep >= MaximumStep || current.PendingStep is not null)
        {
            return evaluated;
        }

        return latestEligible.Count(outcome => outcome.Qualifies) >= RequiredQualifyingCount
            ? evaluated with
            {
                PendingStep = current.CurrentStep + 1,
                PendingStepUnlockedByNightDate = newestNightDate,
            }
            : evaluated;
    }

    public static ProgressState ConfirmPendingStep(
        ProgressState current,
        int requestedStep,
        IPhoneStepConfirmation confirmation,
        DateTimeOffset observedAtUtc,
        TimeZoneInfo timeZone)
    {
        ProgressStateRules.Validate(current);
        ArgumentNullException.ThrowIfNull(confirmation);
        ArgumentNullException.ThrowIfNull(timeZone);
        if (observedAtUtc == default || observedAtUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException(
                "The confirmation observation must be a nondefault UTC timestamp.",
                nameof(observedAtUtc));
        }

        if (current.PendingStep is not { } pendingStep
            || requestedStep != pendingStep
            || !confirmation.IsComplete)
        {
            throw new InvalidOperationException(
                "The pending iPhone step confirmation is incomplete or does not match.");
        }

        if (current.PendingStepConfirmedAtUtc is not null)
        {
            return current;
        }

        DateTimeOffset localObservation = TimeZoneInfo.ConvertTime(observedAtUtc, timeZone);
        DateOnly effectiveNight = DateOnly.FromDateTime(localObservation.DateTime);
        if (TimeOnly.FromDateTime(localObservation.DateTime) >= ConfirmationCutoff)
        {
            effectiveNight = effectiveNight.AddDays(1);
        }

        if (effectiveNight < current.PendingStepUnlockedByNightDate)
        {
            throw new InvalidOperationException(
                "The confirmation observation predates the pending step unlock.");
        }

        return current with
        {
            PendingStepConfirmedAtUtc = observedAtUtc,
            PendingStepEffectiveNightDate = effectiveNight,
        };
    }

    public static ProgressState ActivatePendingStep(
        ProgressState current,
        DateOnly evaluatedNightDate)
    {
        ProgressStateRules.Validate(current);
        if (evaluatedNightDate == default)
        {
            throw new ArgumentOutOfRangeException(nameof(evaluatedNightDate));
        }

        if (current.PendingStep is not { } pendingStep
            || current.PendingStepConfirmedAtUtc is null
            || current.PendingStepEffectiveNightDate is not { } effectiveNight
            || evaluatedNightDate < effectiveNight)
        {
            return current;
        }

        return current with
        {
            CurrentStep = pendingStep,
            PendingStep = null,
            PendingStepUnlockedByNightDate = null,
            PendingStepConfirmedAtUtc = null,
            PendingStepEffectiveNightDate = null,
        };
    }
}
