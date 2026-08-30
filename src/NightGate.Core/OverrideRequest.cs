namespace NightGate.Core;

public sealed record OverrideRequest(
    OverrideKind Kind,
    EmergencyReason? EmergencyReason);
