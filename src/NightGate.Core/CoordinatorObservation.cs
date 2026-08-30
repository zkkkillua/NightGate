namespace NightGate.Core;

public sealed record CoordinatorObservation(
    StorageMode Mode,
    NightState? State,
    NightPhase BasePhase,
    NightPhase EffectivePhase,
    string? DegradationCode = null)
{
    public bool IsDegraded => Mode == StorageMode.Degraded;

    public bool EnforcementEnabled => Mode == StorageMode.Success;
}

public sealed record ScheduledCoordinatorObservation(
    CoordinatorObservation Observation,
    DateTimeOffset EvaluatedAtUtc,
    NightWindow Window);
