namespace NightGate.Core;

public enum OverrideError
{
    None,
    TeamRescueCooldownActive,
    EmergencyReasonRequired,
    AlreadyUsedTonight,
    OverrideAlreadyActive,
    TeamRescueUnavailable,
}

public sealed record OverrideDecision(
    bool Accepted,
    OverrideError Error,
    NightState State,
    ProgressState Progress,
    long? AllowedProcessSnapshotGeneration = null);
