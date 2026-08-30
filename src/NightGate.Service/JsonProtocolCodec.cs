using System.Text.Json;
using NightGate.Protocol;

namespace NightGate.Service;

public enum ProtocolDecodeStatus
{
    Success,
    MessageTooLarge,
    MalformedMessage,
}

public sealed record ProtocolRequestEnvelope(
    int Version,
    string Type,
    string RequestId,
    JsonElement Payload);

public sealed record ProtocolParseResult(
    ProtocolDecodeStatus Status,
    ProtocolRequestEnvelope? Envelope,
    string RequestId = "");

public sealed record ProtocolDecodeResult(
    ProtocolDecodeStatus Status,
    ProtocolRequestEnvelope? Envelope,
    string RequestId = "");

public interface IProtocolEnvelopeParser
{
    ProtocolParseResult Parse(ReadOnlyMemory<byte> utf8Json);
}

public sealed class JsonProtocolCodec(IProtocolEnvelopeParser? parser = null)
{
    public const int MaximumMessageBytes = NightGateProtocol.MaximumBodyBytes;
    private readonly IProtocolEnvelopeParser _parser = parser ?? new SystemTextJsonEnvelopeParser();

    public ProtocolDecodeResult Decode(ReadOnlyMemory<byte> utf8Json)
    {
        if (utf8Json.Length > MaximumMessageBytes)
        {
            return new(ProtocolDecodeStatus.MessageTooLarge, null);
        }

        ProtocolParseResult parsed = _parser.Parse(utf8Json);
        return new(parsed.Status, parsed.Envelope, parsed.RequestId);
    }
}

public sealed class SystemTextJsonEnvelopeParser : IProtocolEnvelopeParser
{
    public ProtocolParseResult Parse(ReadOnlyMemory<byte> utf8Json)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(utf8Json);
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return new(ProtocolDecodeStatus.MalformedMessage, null);
            }

            HashSet<string> seen = new(StringComparer.Ordinal);
            bool hasOnlyUniqueEnvelopeProperties = true;
            foreach (JsonProperty property in root.EnumerateObject())
            {
                if (property.Name is not ("version" or "type" or "requestId" or "payload")
                    || !seen.Add(property.Name))
                {
                    hasOnlyUniqueEnvelopeProperties = false;
                }
            }

            string requestId = seen.Contains("requestId")
                && root.TryGetProperty("requestId", out JsonElement correlationElement)
                && correlationElement.ValueKind == JsonValueKind.String
                && NightGateProtocol.IsValidRequestId(correlationElement.GetString())
                    ? correlationElement.GetString()!
                    : string.Empty;
            if (!hasOnlyUniqueEnvelopeProperties
                || seen.Count != 4
                || !root.TryGetProperty("version", out JsonElement versionElement)
                || versionElement.ValueKind != JsonValueKind.Number
                || !versionElement.TryGetInt32(out int version)
                || !root.TryGetProperty("type", out JsonElement typeElement)
                || typeElement.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(typeElement.GetString())
                || !root.TryGetProperty("requestId", out JsonElement requestIdElement)
                || requestIdElement.ValueKind != JsonValueKind.String
                || !NightGateProtocol.IsValidRequestId(requestIdElement.GetString())
                || !root.TryGetProperty("payload", out JsonElement payloadElement))
            {
                return new(ProtocolDecodeStatus.MalformedMessage, null, requestId);
            }

            ProtocolRequestEnvelope envelope = new(
                version,
                typeElement.GetString()!,
                requestIdElement.GetString()!,
                payloadElement.Clone());
            return new(ProtocolDecodeStatus.Success, envelope, requestId);
        }
        catch (JsonException)
        {
            return new(ProtocolDecodeStatus.MalformedMessage, null);
        }
    }
}
