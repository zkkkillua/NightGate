using System.Collections.Immutable;

namespace NightGate.Core;

public sealed record ActiveOverride(
    OverrideKind Kind,
    DateTimeOffset RequestedAtUtc,
    DateTimeOffset StartsAtUtc,
    DateTimeOffset EndsAtUtc,
    ImmutableArray<string> AllowedProcessIdentifiers);
