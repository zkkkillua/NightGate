namespace NightGate.Core;

public sealed record NightWindow(
    DateOnly NightDate,
    DateTimeOffset ProtectedStart,
    DateTimeOffset LastStart,
    DateTimeOffset Lock,
    DateTimeOffset LightsOut,
    DateTimeOffset Wake);
