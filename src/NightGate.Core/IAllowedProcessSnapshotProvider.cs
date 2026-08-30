using System.Collections.Immutable;

namespace NightGate.Core;

public interface IAllowedProcessSnapshotProvider
{
    ImmutableArray<string> GetSnapshot();

    AllowedProcessSnapshotResult GetSnapshotResult() =>
        AllowedProcessSnapshotResult.Available(GetSnapshot());

    IDisposable? TryAcquireValidationLease(long? expectedGeneration) =>
        expectedGeneration is null
            ? NoOpAllowedProcessSnapshotValidationLease.Instance
            : null;
}

public sealed class NoOpAllowedProcessSnapshotValidationLease : IDisposable
{
    public static NoOpAllowedProcessSnapshotValidationLease Instance { get; } = new();

    private NoOpAllowedProcessSnapshotValidationLease()
    {
    }

    public void Dispose()
    {
    }
}

public sealed record AllowedProcessSnapshotResult
{
    private AllowedProcessSnapshotResult(
        bool isAvailable,
        ImmutableArray<string> identifiers,
        string? degradationCode,
        long? generation)
    {
        if (generation < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(generation));
        }

        IsAvailable = isAvailable;
        Identifiers = identifiers;
        DegradationCode = degradationCode;
        Generation = generation;
    }

    public bool IsAvailable { get; }

    public ImmutableArray<string> Identifiers { get; }

    public string? DegradationCode { get; }

    public long? Generation { get; }

    public static AllowedProcessSnapshotResult Available(
        IEnumerable<string> identifiers,
        long? generation = null)
    {
        ArgumentNullException.ThrowIfNull(identifiers);
        return new(true, [.. identifiers], null, generation);
    }

    public static AllowedProcessSnapshotResult Unavailable(
        string degradationCode,
        long? generation = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(degradationCode);
        return new(false, [], degradationCode, generation);
    }
}
