using System.IO;
using System.Text.Json;
using NightGate.Core;
using NightGate.Protocol;

namespace NightGate.Desktop;

internal sealed class PipeProcessGateEnvelopeStore : IProcessGateEnvelopeStore
{
    private readonly ProcessPersistencePipeClient _client;

    internal PipeProcessGateEnvelopeStore(
        INightGatePipeTransport transport,
        IProtocolRequestIdSource? requestIdSource = null)
    {
        _client = new(transport, requestIdSource ?? new GuidProtocolRequestIdSource());
    }

    public async ValueTask<ProcessGateEnvelopeLoadResult> LoadAsync(
        CancellationToken cancellationToken = default)
    {
        ProcessPersistenceLoadResult result = await _client.LoadAsync(
                ProcessPersistenceSlot.ProcessGateEnvelope,
                cancellationToken)
            .ConfigureAwait(false);
        return result.Status switch
        {
            ProcessPersistenceLoadStatus.Missing =>
                new(ProcessGateStoreLoadStatus.NotFound, null),
            ProcessPersistenceLoadStatus.Unavailable =>
                new(ProcessGateStoreLoadStatus.Unavailable, null),
            ProcessPersistenceLoadStatus.Corrupt =>
                new(ProcessGateStoreLoadStatus.Corrupt, null),
            ProcessPersistenceLoadStatus.Found => DecodeFound(result.Record),
            _ => new(ProcessGateStoreLoadStatus.Corrupt, null),
        };
    }

    public async ValueTask<ProcessGateEnvelopeSaveResult> CompareExchangeAsync(
        long expectedRevision,
        ProcessGateEnvelope replacement,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(replacement);
        if (expectedRevision < 0
            || expectedRevision == long.MaxValue
            || replacement.Revision != expectedRevision + 1)
        {
            return new(ProcessGateStoreSaveStatus.Corrupt, null);
        }

        if (!ProcessPersistenceJsonCodec.TrySerializeEnvelope(
                replacement,
                out string payloadJson))
        {
            return new(ProcessGateStoreSaveStatus.Unavailable, null);
        }

        ProcessPersistenceRecord proposed = new(
            ProcessPersistenceSlot.ProcessGateEnvelope,
            ProcessPersistenceLimits.CurrentSchemaVersion,
            replacement.Revision,
            payloadJson);
        ProcessPersistenceSaveResult result = await _client.CompareExchangeAsync(
                proposed.Slot,
                expectedRevision == 0 ? null : expectedRevision,
                proposed,
                cancellationToken)
            .ConfigureAwait(false);
        return result.Status switch
        {
            ProcessPersistenceSaveStatus.Unavailable =>
                new(ProcessGateStoreSaveStatus.Unavailable, null),
            ProcessPersistenceSaveStatus.Corrupt =>
                new(ProcessGateStoreSaveStatus.Corrupt, null),
            ProcessPersistenceSaveStatus.Conflict => DecodeConflict(result.Record),
            ProcessPersistenceSaveStatus.Saved => DecodeSaved(
                result.Record,
                replacement,
                payloadJson),
            _ => new(ProcessGateStoreSaveStatus.Corrupt, null),
        };
    }

    private static ProcessGateEnvelopeLoadResult DecodeFound(
        ProcessPersistenceRecord? record)
    {
        if (!TryDecode(record, out ProcessGateEnvelope? envelope))
        {
            return new(ProcessGateStoreLoadStatus.Corrupt, null);
        }

        return new(ProcessGateStoreLoadStatus.Found, envelope);
    }

    private static ProcessGateEnvelopeSaveResult DecodeConflict(
        ProcessPersistenceRecord? record)
    {
        if (record is null)
        {
            return new(ProcessGateStoreSaveStatus.Conflict, null);
        }

        return TryDecode(record, out ProcessGateEnvelope? envelope)
            ? new(ProcessGateStoreSaveStatus.Conflict, envelope)
            : new(ProcessGateStoreSaveStatus.Corrupt, null);
    }

    private static ProcessGateEnvelopeSaveResult DecodeSaved(
        ProcessPersistenceRecord? record,
        ProcessGateEnvelope proposed,
        string canonicalProposedJson)
    {
        if (!TryDecode(record, out ProcessGateEnvelope? accepted)
            || accepted!.Revision != proposed.Revision
            || !ProcessPersistenceJsonCodec.TrySerializeEnvelope(
                accepted,
                out string canonicalAcceptedJson)
            || !string.Equals(
                canonicalAcceptedJson,
                canonicalProposedJson,
                StringComparison.Ordinal))
        {
            return new(ProcessGateStoreSaveStatus.Corrupt, null);
        }

        return new(ProcessGateStoreSaveStatus.Saved, accepted);
    }

    private static bool TryDecode(
        ProcessPersistenceRecord? record,
        out ProcessGateEnvelope? envelope)
    {
        envelope = null;
        return record is
            {
                Slot: ProcessPersistenceSlot.ProcessGateEnvelope,
                SchemaVersion: ProcessPersistenceLimits.CurrentSchemaVersion,
                Version: >= 1 and < long.MaxValue,
            }
            && ProcessPersistenceJsonCodec.TryDeserializeEnvelope(
                record.PayloadJson,
                out envelope)
            && envelope!.Revision == record.Version;
    }
}

internal sealed class PipeProcessSourceContinuityStore : IProcessSourceContinuityStore
{
    private readonly ProcessPersistencePipeClient _client;

    internal PipeProcessSourceContinuityStore(
        INightGatePipeTransport transport,
        IProtocolRequestIdSource? requestIdSource = null)
    {
        _client = new(transport, requestIdSource ?? new GuidProtocolRequestIdSource());
    }

    public async ValueTask<ProcessSourceContinuityStoreLoadResult> LoadAsync(
        CancellationToken cancellationToken = default)
    {
        ProcessPersistenceLoadResult result = await _client.LoadAsync(
                ProcessPersistenceSlot.ProcessSourceContinuity,
                cancellationToken)
            .ConfigureAwait(false);
        return result.Status switch
        {
            ProcessPersistenceLoadStatus.Missing => new(
                ProcessSourceContinuityStoreLoadStatus.Missing,
                null),
            ProcessPersistenceLoadStatus.Unavailable => new(
                ProcessSourceContinuityStoreLoadStatus.Unavailable,
                null),
            ProcessPersistenceLoadStatus.Corrupt => new(
                ProcessSourceContinuityStoreLoadStatus.Corrupt,
                null),
            ProcessPersistenceLoadStatus.Found => DecodeFound(result.Record),
            _ => new(ProcessSourceContinuityStoreLoadStatus.Corrupt, null),
        };
    }

    public async ValueTask<ProcessSourceContinuityStoreSaveResult> CompareExchangeAsync(
        long? expectedVersion,
        ProcessSourceContinuityCheckpoint replacement,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(replacement);
        if (expectedVersion is < 1
            || expectedVersion == long.MaxValue
            || replacement.Version != (expectedVersion ?? 0) + 1)
        {
            return new(ProcessSourceContinuityStoreSaveStatus.Corrupt, null);
        }

        if (!ProcessPersistenceJsonCodec.TrySerializeContinuity(
                replacement,
                out string payloadJson))
        {
            return new(ProcessSourceContinuityStoreSaveStatus.Unavailable, null);
        }

        ProcessPersistenceRecord proposed = new(
            ProcessPersistenceSlot.ProcessSourceContinuity,
            ProcessPersistenceLimits.CurrentSchemaVersion,
            replacement.Version,
            payloadJson);
        ProcessPersistenceSaveResult result = await _client.CompareExchangeAsync(
                proposed.Slot,
                expectedVersion,
                proposed,
                cancellationToken)
            .ConfigureAwait(false);
        return result.Status switch
        {
            ProcessPersistenceSaveStatus.Unavailable => new(
                ProcessSourceContinuityStoreSaveStatus.Unavailable,
                null),
            ProcessPersistenceSaveStatus.Corrupt => new(
                ProcessSourceContinuityStoreSaveStatus.Corrupt,
                null),
            ProcessPersistenceSaveStatus.Conflict => DecodeConflict(result.Record),
            ProcessPersistenceSaveStatus.Saved => DecodeSaved(
                result.Record,
                replacement,
                payloadJson),
            _ => new(ProcessSourceContinuityStoreSaveStatus.Corrupt, null),
        };
    }

    private static ProcessSourceContinuityStoreLoadResult DecodeFound(
        ProcessPersistenceRecord? record) =>
        TryDecode(record, out ProcessSourceContinuityCheckpoint? checkpoint)
            ? new(ProcessSourceContinuityStoreLoadStatus.Found, checkpoint)
            : new(ProcessSourceContinuityStoreLoadStatus.Corrupt, null);

    private static ProcessSourceContinuityStoreSaveResult DecodeConflict(
        ProcessPersistenceRecord? record)
    {
        if (record is null)
        {
            return new(ProcessSourceContinuityStoreSaveStatus.Conflict, null);
        }

        return TryDecode(record, out ProcessSourceContinuityCheckpoint? checkpoint)
            ? new(ProcessSourceContinuityStoreSaveStatus.Conflict, checkpoint)
            : new(ProcessSourceContinuityStoreSaveStatus.Corrupt, null);
    }

    private static ProcessSourceContinuityStoreSaveResult DecodeSaved(
        ProcessPersistenceRecord? record,
        ProcessSourceContinuityCheckpoint proposed,
        string canonicalProposedJson)
    {
        if (!TryDecode(record, out ProcessSourceContinuityCheckpoint? accepted)
            || accepted!.Version != proposed.Version
            || !ProcessPersistenceJsonCodec.TrySerializeContinuity(
                accepted,
                out string canonicalAcceptedJson)
            || !string.Equals(
                canonicalAcceptedJson,
                canonicalProposedJson,
                StringComparison.Ordinal))
        {
            return new(ProcessSourceContinuityStoreSaveStatus.Corrupt, null);
        }

        return new(ProcessSourceContinuityStoreSaveStatus.Saved, accepted);
    }

    private static bool TryDecode(
        ProcessPersistenceRecord? record,
        out ProcessSourceContinuityCheckpoint? checkpoint)
    {
        checkpoint = null;
        return record is
            {
                Slot: ProcessPersistenceSlot.ProcessSourceContinuity,
                SchemaVersion: ProcessPersistenceLimits.CurrentSchemaVersion,
                Version: >= 1 and < long.MaxValue,
            }
            && ProcessPersistenceJsonCodec.TryDeserializeContinuity(
                record.PayloadJson,
                out checkpoint)
            && checkpoint!.Version == record.Version;
    }
}

internal sealed class ProcessPersistencePipeClient
{
    private readonly INightGatePipeTransport _transport;
    private readonly IProtocolRequestIdSource _requestIdSource;

    internal ProcessPersistencePipeClient(
        INightGatePipeTransport transport,
        IProtocolRequestIdSource requestIdSource)
    {
        ArgumentNullException.ThrowIfNull(transport);
        ArgumentNullException.ThrowIfNull(requestIdSource);
        _transport = transport;
        _requestIdSource = requestIdSource;
    }

    internal async ValueTask<ProcessPersistenceLoadResult> LoadAsync(
        ProcessPersistenceSlot slot,
        CancellationToken cancellationToken)
    {
        try
        {
            string requestId = NextRequestId();
            ReadOnlyMemory<byte> request = JsonSerializer.SerializeToUtf8Bytes(new
            {
                version = NightGateProtocol.Version,
                type = "loadProcessPersistence",
                requestId,
                payload = new { slot = ProcessPersistenceLimits.GetSlotToken(slot) },
            });
            EnsureFrameSize(request);
            ReadOnlyMemory<byte> response = await _transport
                .ExchangeAsync(request, cancellationToken)
                .ConfigureAwait(false);
            return DecodeLoadResponse(response, slot, requestId);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return new(ProcessPersistenceLoadStatus.Unavailable, null);
        }
    }

    internal async ValueTask<ProcessPersistenceSaveResult> CompareExchangeAsync(
        ProcessPersistenceSlot slot,
        long? expectedVersion,
        ProcessPersistenceRecord replacement,
        CancellationToken cancellationToken)
    {
        try
        {
            if (!IsValidReplacement(slot, expectedVersion, replacement))
            {
                return new(ProcessPersistenceSaveStatus.Corrupt, null);
            }

            using JsonDocument payloadDocument = JsonDocument.Parse(replacement.PayloadJson);
            string requestId = NextRequestId();
            ReadOnlyMemory<byte> request = JsonSerializer.SerializeToUtf8Bytes(new
            {
                version = NightGateProtocol.Version,
                type = "compareExchangeProcessPersistence",
                requestId,
                payload = new
                {
                    slot = ProcessPersistenceLimits.GetSlotToken(slot),
                    expectedVersion,
                    schemaVersion = replacement.SchemaVersion,
                    replacementVersion = replacement.Version,
                    payload = payloadDocument.RootElement,
                },
            });
            EnsureFrameSize(request);
            ReadOnlyMemory<byte> response = await _transport
                .ExchangeAsync(request, cancellationToken)
                .ConfigureAwait(false);
            return DecodeSaveResponse(response, slot, requestId);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return new(ProcessPersistenceSaveStatus.Unavailable, null);
        }
    }

    private string NextRequestId()
    {
        string requestId = _requestIdSource.NextRequestId();
        if (!NightGateProtocol.IsValidRequestId(requestId))
        {
            throw new InvalidDataException("Request ID source returned an invalid identifier.");
        }

        return requestId;
    }

    private static ProcessPersistenceLoadResult DecodeLoadResponse(
        ReadOnlyMemory<byte> response,
        ProcessPersistenceSlot expectedSlot,
        string requestId)
    {
        DecodedResponse decoded = DecodeResponse(
            response,
            "loadProcessPersistenceResult",
            requestId,
            expectedSlot);
        return decoded.Status switch
        {
            "found" when decoded.WrapperStatus == "success" && decoded.Record is not null =>
                new(ProcessPersistenceLoadStatus.Found, decoded.Record),
            "missing" when decoded.WrapperStatus == "success" && decoded.Record is null =>
                new(ProcessPersistenceLoadStatus.Missing, null),
            "unavailable" when decoded.WrapperStatus == "degraded" && decoded.Record is null =>
                new(ProcessPersistenceLoadStatus.Unavailable, null),
            "corrupt" when decoded.WrapperStatus == "degraded" && decoded.Record is null =>
                new(ProcessPersistenceLoadStatus.Corrupt, null),
            _ => new(ProcessPersistenceLoadStatus.Unavailable, null),
        };
    }

    private static ProcessPersistenceSaveResult DecodeSaveResponse(
        ReadOnlyMemory<byte> response,
        ProcessPersistenceSlot expectedSlot,
        string requestId)
    {
        DecodedResponse decoded = DecodeResponse(
            response,
            "compareExchangeProcessPersistenceResult",
            requestId,
            expectedSlot);
        return decoded.Status switch
        {
            "saved" when decoded.WrapperStatus == "success" && decoded.Record is not null =>
                new(ProcessPersistenceSaveStatus.Saved, decoded.Record),
            "conflict" when decoded.WrapperStatus == "success" =>
                new(ProcessPersistenceSaveStatus.Conflict, decoded.Record),
            "unavailable" when decoded.WrapperStatus == "degraded" && decoded.Record is null =>
                new(ProcessPersistenceSaveStatus.Unavailable, null),
            "corrupt" when decoded.WrapperStatus == "degraded" && decoded.Record is null =>
                new(ProcessPersistenceSaveStatus.Corrupt, null),
            _ => new(ProcessPersistenceSaveStatus.Unavailable, null),
        };
    }

    private static DecodedResponse DecodeResponse(
        ReadOnlyMemory<byte> response,
        string expectedType,
        string expectedRequestId,
        ProcessPersistenceSlot expectedSlot)
    {
        EnsureFrameSize(response);
        using JsonDocument document = JsonDocument.Parse(response);
        JsonElement root = document.RootElement;
        EnsureExactObject(root, "version", "type", "requestId", "payload");
        if (root.GetProperty("version").ValueKind != JsonValueKind.Number
            || !root.GetProperty("version").TryGetInt32(out int version)
            || version != NightGateProtocol.Version
            || root.GetProperty("type").ValueKind != JsonValueKind.String
            || root.GetProperty("type").GetString() != expectedType
            || root.GetProperty("requestId").ValueKind != JsonValueKind.String
            || root.GetProperty("requestId").GetString() != expectedRequestId)
        {
            throw new InvalidDataException("Persistence response envelope does not match request.");
        }

        JsonElement wrapper = root.GetProperty("payload");
        EnsureExactObject(wrapper, "status", "data");
        if (wrapper.GetProperty("status").ValueKind != JsonValueKind.String
            || wrapper.GetProperty("status").GetString() is not ("success" or "degraded"))
        {
            throw new InvalidDataException("Persistence response wrapper is invalid.");
        }

        JsonElement data = wrapper.GetProperty("data");
        EnsureExactObject(data, "status", "record");
        if (data.GetProperty("status").ValueKind != JsonValueKind.String)
        {
            throw new InvalidDataException("Persistence response status is invalid.");
        }

        JsonElement recordElement = data.GetProperty("record");
        ProcessPersistenceRecord? record = null;
        if (recordElement.ValueKind != JsonValueKind.Null)
        {
            if (!TryReadRecord(recordElement, expectedSlot, out record))
            {
                return new(
                    wrapper.GetProperty("status").GetString()!,
                    "corrupt",
                    null);
            }
        }

        return new(
            wrapper.GetProperty("status").GetString()!,
            data.GetProperty("status").GetString()!,
            record);
    }

    private static bool TryReadRecord(
        JsonElement element,
        ProcessPersistenceSlot expectedSlot,
        out ProcessPersistenceRecord? record)
    {
        record = null;
        try
        {
            EnsureExactObject(element, "slot", "schemaVersion", "version", "payload");
            if (element.GetProperty("slot").ValueKind != JsonValueKind.String
                || !ProcessPersistenceLimits.TryParseSlotToken(
                    element.GetProperty("slot").GetString(),
                    out ProcessPersistenceSlot slot)
                || slot != expectedSlot
                || element.GetProperty("schemaVersion").ValueKind != JsonValueKind.Number
                || !element.GetProperty("schemaVersion").TryGetInt32(out int schemaVersion)
                || schemaVersion != ProcessPersistenceLimits.CurrentSchemaVersion
                || element.GetProperty("version").ValueKind != JsonValueKind.Number
                || !element.GetProperty("version").TryGetInt64(out long version)
                || version is < 1 or long.MaxValue
                || element.GetProperty("payload").ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            string payloadJson = element.GetProperty("payload").GetRawText();
            if (!ProcessPersistenceLimits.IsValidPayload(payloadJson, schemaVersion))
            {
                return false;
            }

            record = new(slot, schemaVersion, version, payloadJson);
            return true;
        }
        catch (Exception exception) when (exception is InvalidOperationException
            or KeyNotFoundException
            or JsonException)
        {
            return false;
        }
    }

    private static bool IsValidReplacement(
        ProcessPersistenceSlot slot,
        long? expectedVersion,
        ProcessPersistenceRecord replacement) =>
        Enum.IsDefined(slot)
        && replacement.Slot == slot
        && replacement.SchemaVersion == ProcessPersistenceLimits.CurrentSchemaVersion
        && expectedVersion is null or >= 1
        && expectedVersion != long.MaxValue
        && replacement.Version == (expectedVersion ?? 0) + 1
        && ProcessPersistenceLimits.IsValidPayload(
            replacement.PayloadJson,
            replacement.SchemaVersion);

    private static void EnsureFrameSize(ReadOnlyMemory<byte> frame)
    {
        if (frame.Length > NightGateProtocol.MaximumBodyBytes)
        {
            throw new InvalidDataException("Persistence frame exceeds protocol limit.");
        }
    }

    private static void EnsureExactObject(
        JsonElement element,
        params string[] expectedNames)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException("Expected a JSON object.");
        }

        HashSet<string> names = new(StringComparer.Ordinal);
        foreach (JsonProperty property in element.EnumerateObject())
        {
            if (!expectedNames.Contains(property.Name, StringComparer.Ordinal)
                || !names.Add(property.Name))
            {
                throw new InvalidDataException("JSON object has unknown or duplicate members.");
            }

            EnsureUniquePropertiesRecursively(property.Value);
        }

        if (names.Count != expectedNames.Length)
        {
            throw new InvalidDataException("JSON object is missing a member.");
        }
    }

    private static void EnsureUniquePropertiesRecursively(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            HashSet<string> names = new(StringComparer.Ordinal);
            foreach (JsonProperty property in element.EnumerateObject())
            {
                if (!names.Add(property.Name))
                {
                    throw new InvalidDataException("JSON object has duplicate members.");
                }

                EnsureUniquePropertiesRecursively(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement item in element.EnumerateArray())
            {
                EnsureUniquePropertiesRecursively(item);
            }
        }
    }

    private sealed record DecodedResponse(
        string WrapperStatus,
        string Status,
        ProcessPersistenceRecord? Record);
}
