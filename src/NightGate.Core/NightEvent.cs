namespace NightGate.Core;

public enum NightEventKind
{
    NightStarted,
    StateObserved,
    BasePhaseAdvanced,
    OverrideRequested,
    OverrideEnded,
    NightClosed,
    HistoryCleared,
    ServiceDegraded,
    DeliberateBypass,
    LateNewEntertainment,
    MissedLock,
    WorkstationLocked,
}

public sealed record NightEvent(
    Guid EventId,
    Guid? NightId,
    DateTimeOffset OccurredAtUtc,
    NightEventKind Kind,
    NightPhase? BasePhase = null,
    OverrideKind? OverrideKind = null);
