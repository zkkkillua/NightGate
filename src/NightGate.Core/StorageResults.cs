namespace NightGate.Core;

public enum StorageMode
{
    Success,
    Degraded,
}

public sealed record StorageResult<T>(
    StorageMode Mode,
    T Value,
    string? DegradationCode = null,
    long Version = 0)
{
    public bool IsDegraded => Mode == StorageMode.Degraded;

    public bool EnforcementEnabled => Mode == StorageMode.Success;
}

public sealed record StorageWriteResult(
    StorageMode Mode,
    string? DegradationCode = null,
    bool IsConflict = false)
{
    public static StorageWriteResult Success { get; } = new(StorageMode.Success);

    public static StorageWriteResult Conflict { get; } = new(
        StorageMode.Success,
        IsConflict: true);

    public bool IsDegraded => Mode == StorageMode.Degraded;

    public bool EnforcementEnabled => Mode == StorageMode.Success && !IsConflict;
}
