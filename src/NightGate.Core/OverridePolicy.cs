using System.Collections.Immutable;

namespace NightGate.Core;

public sealed class OverridePolicy(IAllowedProcessSnapshotProvider allowedProcessSnapshotProvider)
{
    private static readonly TimeSpan TeamRescueDuration = TimeSpan.FromMinutes(20);
    private static readonly TimeSpan TeamRescueCooldown = TimeSpan.FromHours(168);
    private static readonly TimeSpan EmergencyDuration = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan EntertainmentCoolingOff = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan EntertainmentDuration = TimeSpan.FromMinutes(20);

    public OverrideDecision Request(
        NightState state,
        ProgressState progress,
        OverrideRequest request,
        DateTimeOffset requestedAtUtc)
    {
        if (request.Kind == OverrideKind.Emergency)
        {
            return RequestEmergency(state, progress, request, requestedAtUtc);
        }

        if (state.ActiveOverride is { } activeOverride
            && requestedAtUtc < activeOverride.EndsAtUtc)
        {
            if (request.Kind == OverrideKind.Entertainment && state.EntertainmentUsed)
            {
                return Rejected(OverrideError.AlreadyUsedTonight, state, progress);
            }

            if (request.Kind == OverrideKind.TeamRescue
                && progress.LastTeamRescueAtUtc is { } lastUse
                && requestedAtUtc < lastUse.Add(TeamRescueCooldown))
            {
                return Rejected(OverrideError.TeamRescueCooldownActive, state, progress);
            }

            return Rejected(OverrideError.OverrideAlreadyActive, state, progress);
        }

        return request.Kind switch
        {
            OverrideKind.TeamRescue => RequestTeamRescue(state, progress, request, requestedAtUtc),
            OverrideKind.Entertainment => RequestEntertainment(state, progress, requestedAtUtc),
            _ => throw new ArgumentOutOfRangeException(nameof(request)),
        };
    }

    public IDisposable? TryAcquireTeamRescueSnapshotValidation(
        long? expectedGeneration) =>
        allowedProcessSnapshotProvider.TryAcquireValidationLease(expectedGeneration);

    public static NightPhase ResolvePhase(
        NightState state,
        NightPhase basePhase,
        DateTimeOffset observedAtUtc)
    {
        ActiveOverride? activeOverride = state.ActiveOverride;
        if (activeOverride is null || observedAtUtc >= activeOverride.EndsAtUtc)
        {
            return basePhase;
        }

        return observedAtUtc < activeOverride.StartsAtUtc
            ? NightPhase.CoolingOff
            : NightPhase.OverrideActive;
    }

    private OverrideDecision RequestTeamRescue(
        NightState state,
        ProgressState progress,
        OverrideRequest request,
        DateTimeOffset requestedAtUtc)
    {
        if (progress.LastTeamRescueAtUtc is { } lastUse && requestedAtUtc < lastUse.Add(TeamRescueCooldown))
        {
            return Rejected(OverrideError.TeamRescueCooldownActive, state, progress);
        }

        AllowedProcessSnapshotResult snapshotResult;
        try
        {
            snapshotResult = allowedProcessSnapshotProvider.GetSnapshotResult();
        }
        catch (Exception)
        {
            return Rejected(OverrideError.TeamRescueUnavailable, state, progress);
        }

        if (snapshotResult is not { IsAvailable: true }
            || snapshotResult.Identifiers.IsDefault)
        {
            return Rejected(OverrideError.TeamRescueUnavailable, state, progress);
        }

        ImmutableArray<string> snapshot = ImmutableArray.CreateRange(snapshotResult.Identifiers);
        ActiveOverride activeOverride = new(
            OverrideKind.TeamRescue,
            requestedAtUtc,
            requestedAtUtc,
            requestedAtUtc.Add(TeamRescueDuration),
            snapshot);

        return Accepted(
            state with
            {
                ActiveOverride = activeOverride,
                TeamRescueUsed = true,
                OverrideReasons = state.OverrideReasons.Increment(OverrideKind.TeamRescue),
            },
            progress with { LastTeamRescueAtUtc = requestedAtUtc },
            snapshotResult.Generation);
    }

    private static OverrideDecision RequestEmergency(
        NightState state,
        ProgressState progress,
        OverrideRequest request,
        DateTimeOffset requestedAtUtc)
    {
        if (request.EmergencyReason is not { } reason
            || reason is not (EmergencyReason.Health
                or EmergencyReason.Safety
                or EmergencyReason.UrgentWork))
        {
            return Rejected(OverrideError.EmergencyReasonRequired, state, progress);
        }

        ActiveOverride activeOverride = new(
            OverrideKind.Emergency,
            requestedAtUtc,
            requestedAtUtc,
            requestedAtUtc.Add(EmergencyDuration),
            []);

        return Accepted(
            state with
            {
                ActiveOverride = activeOverride,
                EmergencyUsed = true,
                OverrideReasons = state.OverrideReasons.Increment(OverrideKind.Emergency, reason),
            },
            progress);
    }

    private static OverrideDecision RequestEntertainment(
        NightState state,
        ProgressState progress,
        DateTimeOffset requestedAtUtc)
    {
        if (state.EntertainmentUsed)
        {
            return Rejected(OverrideError.AlreadyUsedTonight, state, progress);
        }

        DateTimeOffset startsAtUtc = requestedAtUtc.Add(EntertainmentCoolingOff);
        ActiveOverride activeOverride = new(
            OverrideKind.Entertainment,
            requestedAtUtc,
            startsAtUtc,
            startsAtUtc.Add(EntertainmentDuration),
            []);

        return Accepted(
            state with
            {
                ActiveOverride = activeOverride,
                EntertainmentUsed = true,
                OverrideReasons = state.OverrideReasons.Increment(OverrideKind.Entertainment),
            },
            progress);
    }

    private static OverrideDecision Accepted(
        NightState state,
        ProgressState progress,
        long? allowedProcessSnapshotGeneration = null) =>
        new(
            true,
            OverrideError.None,
            state,
            progress,
            allowedProcessSnapshotGeneration);

    private static OverrideDecision Rejected(
        OverrideError error,
        NightState state,
        ProgressState progress) => new(false, error, state, progress);
}
