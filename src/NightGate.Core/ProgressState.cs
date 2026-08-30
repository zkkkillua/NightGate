namespace NightGate.Core;

public sealed record ProgressState(
    int CurrentStep,
    DateTimeOffset? LastTeamRescueAtUtc,
    DateOnly? LastProgressionNightDate,
    int? PendingStep = null,
    DateOnly? PendingStepUnlockedByNightDate = null,
    DateTimeOffset? PendingStepConfirmedAtUtc = null,
    DateOnly? PendingStepEffectiveNightDate = null)
{
    public static ProgressState Initial { get; } = new(1, null, null);
}

internal static class ProgressStateRules
{
    public static void Validate(ProgressState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (state.CurrentStep is < 1 or > 4
            || state.LastTeamRescueAtUtc is { } rescue
                && (rescue == default || rescue.Offset != TimeSpan.Zero)
            || state.LastProgressionNightDate is { } progressionNight
                && progressionNight == default
            || state.PendingStepUnlockedByNightDate is { } pendingUnlockNight
                && pendingUnlockNight == default
            || state.PendingStepEffectiveNightDate is { } pendingEffectiveNight
                && pendingEffectiveNight == default)
        {
            throw new InvalidDataException("Progress state is invalid.");
        }

        if (state.PendingStep is null)
        {
            if (state.PendingStepUnlockedByNightDate is not null
                || state.PendingStepConfirmedAtUtc is not null
                || state.PendingStepEffectiveNightDate is not null)
            {
                throw new InvalidDataException("Progress state contains partial pending data.");
            }

            return;
        }

        if (state.CurrentStep >= 4
            || state.PendingStep != state.CurrentStep + 1
            || state.PendingStepUnlockedByNightDate is not { } unlockedBy
            || state.LastProgressionNightDate is not { } lastEvaluated
            || lastEvaluated < unlockedBy
            || (state.PendingStepConfirmedAtUtc is null)
                != (state.PendingStepEffectiveNightDate is null)
            || state.PendingStepConfirmedAtUtc is { } confirmedAt
                && (confirmedAt == default || confirmedAt.Offset != TimeSpan.Zero)
            || state.PendingStepEffectiveNightDate is { } effectiveNight
                && effectiveNight < unlockedBy)
        {
            throw new InvalidDataException("Progress state contains impossible pending data.");
        }
    }
}
