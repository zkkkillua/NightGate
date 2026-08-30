using System.Text;
using System.Text.Json;

namespace NightGate.Core;

public enum ProcessPersistenceSlot
{
    ProcessGateEnvelope,
    ProcessSourceContinuity,
}

public static class ProcessPersistenceLimits
{
    public const int CurrentSchemaVersion = 1;
    public const int MaximumPayloadBytes = 60_000;

    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    public static string GetSlotToken(ProcessPersistenceSlot slot) => slot switch
    {
        ProcessPersistenceSlot.ProcessGateEnvelope => "processGateEnvelope",
        ProcessPersistenceSlot.ProcessSourceContinuity => "processSourceContinuity",
        _ => throw new ArgumentOutOfRangeException(nameof(slot)),
    };

    public static bool TryParseSlotToken(
        string? token,
        out ProcessPersistenceSlot slot)
    {
        switch (token)
        {
            case "processGateEnvelope":
                slot = ProcessPersistenceSlot.ProcessGateEnvelope;
                return true;
            case "processSourceContinuity":
                slot = ProcessPersistenceSlot.ProcessSourceContinuity;
                return true;
            default:
                slot = default;
                return false;
        }
    }

    public static bool IsValidPayload(string? payloadJson, int schemaVersion)
    {
        if (schemaVersion != CurrentSchemaVersion || payloadJson is null)
        {
            return false;
        }

        try
        {
            if (StrictUtf8.GetByteCount(payloadJson) > MaximumPayloadBytes)
            {
                return false;
            }

            using JsonDocument document = JsonDocument.Parse(payloadJson);
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !HasUniquePropertiesRecursively(root)
                || !root.TryGetProperty("schemaVersion", out JsonElement version)
                || version.ValueKind != JsonValueKind.Number
                || !version.TryGetInt32(out int embeddedVersion)
                || embeddedVersion != schemaVersion)
            {
                return false;
            }

            if (JsonSerializer.SerializeToUtf8Bytes(root).Length
                > MaximumPayloadBytes)
            {
                return false;
            }

            return true;
        }
        catch (JsonException)
        {
            return false;
        }
        catch (EncoderFallbackException)
        {
            return false;
        }
    }

    private static bool HasUniquePropertiesRecursively(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            HashSet<string> names = new(StringComparer.Ordinal);
            foreach (JsonProperty property in element.EnumerateObject())
            {
                if (!names.Add(property.Name)
                    || !HasUniquePropertiesRecursively(property.Value))
                {
                    return false;
                }
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement item in element.EnumerateArray())
            {
                if (!HasUniquePropertiesRecursively(item))
                {
                    return false;
                }
            }
        }

        return true;
    }
}

public sealed record ProcessPersistenceRecord(
    ProcessPersistenceSlot Slot,
    int SchemaVersion,
    long Version,
    string PayloadJson);

public enum ProcessPersistenceLoadStatus
{
    Found,
    Missing,
    Unavailable,
    Corrupt,
}

public sealed record ProcessPersistenceLoadResult(
    ProcessPersistenceLoadStatus Status,
    ProcessPersistenceRecord? Record);

public enum ProcessPersistenceSaveStatus
{
    Saved,
    Conflict,
    Unavailable,
    Corrupt,
}

public sealed record ProcessPersistenceSaveResult(
    ProcessPersistenceSaveStatus Status,
    ProcessPersistenceRecord? Record);

public interface IProcessPersistenceRepository
{
    ValueTask<ProcessPersistenceLoadResult> LoadProcessPersistenceAsync(
        ProcessPersistenceSlot slot,
        CancellationToken cancellationToken = default);

    ValueTask<ProcessPersistenceSaveResult> CompareExchangeProcessPersistenceAsync(
        ProcessPersistenceSlot slot,
        long? expectedVersion,
        ProcessPersistenceRecord replacement,
        CancellationToken cancellationToken = default);
}
