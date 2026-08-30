using System.Buffers;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;

namespace NightGate.Desktop;

public interface IDesktopPolicyClient
{
    ValueTask<DesktopPolicyResult> GetPolicyAsync(
        CancellationToken cancellationToken = default);

    ValueTask<DesktopRecordEventResult> RecordEventAsync(
        PrivacySafeEventKind kind,
        CancellationToken cancellationToken = default);
}

public sealed class DesktopPolicyWitnessSource : IProcessGatePolicySource
{
    private const string UnavailableCode = "policy-witness-unavailable";
    private readonly IDesktopPolicyClient _client;

    public DesktopPolicyWitnessSource(IDesktopPolicyClient client)
    {
        ArgumentNullException.ThrowIfNull(client);
        _client = client;
    }

    public async ValueTask<ProcessGatePolicySourceResult> ReadAsync(
        CancellationToken cancellationToken = default)
    {
        try
        {
            DesktopPolicyResult result = await _client
                .GetPolicyAsync(cancellationToken)
                .ConfigureAwait(false);
            if (!TryCreateWitness(result, out ValidatedProcessPolicy? witness))
            {
                return Unavailable(result.DegradationCode ?? UnavailableCode);
            }

            return new(ProcessGateSourceStatus.Available, witness, null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return Unavailable(UnavailableCode);
        }
    }

    internal static bool TryCreateWitness(
        DesktopPolicyResult? result,
        out ValidatedProcessPolicy? witness)
    {
        witness = null;
        if (result is not
            {
                CanEnforce: true,
                IsDegraded: false,
                Status:
                {
                    EnforcementEnabled: true,
                    IsDegraded: false,
                    Policy:
                    {
                        EnforcementEnabled: true,
                        IsDegraded: false,
                    } snapshot,
                },
            }
            || result.ExecutablePolicy != snapshot
            || !NightGateDesktopClient.IsUsablePolicy(snapshot))
        {
            return false;
        }

        long revision = snapshot.EvaluatedAt.UtcTicks;
        if (revision < 0
            || !TryFingerprint(snapshot, out string fingerprint))
        {
            return false;
        }

        string identity = string.Create(
            CultureInfo.InvariantCulture,
            $"{revision:X16}:{fingerprint}");
        witness = new(revision, identity, fingerprint, result, snapshot);
        return true;
    }

    private static bool TryFingerprint(
        DesktopPolicySnapshotDto snapshot,
        out string fingerprint)
    {
        fingerprint = string.Empty;
        try
        {
            ArrayBufferWriter<byte> buffer = new();
            using (Utf8JsonWriter writer = new(buffer, new JsonWriterOptions
            {
                Indented = false,
                SkipValidation = false,
            }))
            {
                writer.WriteStartObject();
                WriteInstant(writer, "evaluatedAt", snapshot.EvaluatedAt);
                writer.WriteNumber("phase", (int)snapshot.Phase);
                WriteWindow(writer, snapshot.Window);
                WriteAppRules(writer, snapshot.AppRules);
                WriteSiteRules(writer, snapshot.SiteRules);
                writer.WriteBoolean("enforcementEnabled", snapshot.EnforcementEnabled);
                writer.WriteBoolean("isDegraded", snapshot.IsDegraded);
                WriteOverride(writer, snapshot.ActiveOverride);
                writer.WriteEndObject();
                writer.Flush();
            }

            fingerprint = Convert.ToHexString(SHA256.HashData(buffer.WrittenSpan));
            return fingerprint.Length == 64;
        }
        catch (Exception exception) when (exception is ArgumentException
            or InvalidOperationException
            or JsonException
            or NotSupportedException)
        {
            return false;
        }
    }

    private static void WriteWindow(
        Utf8JsonWriter writer,
        DesktopNightWindowDto window)
    {
        writer.WriteStartObject("window");
        writer.WriteNumber("nightDayNumber", window.NightDate.DayNumber);
        WriteInstant(writer, "protectedStart", window.ProtectedStart);
        WriteInstant(writer, "lastStart", window.LastStart);
        WriteInstant(writer, "lock", window.Lock);
        WriteInstant(writer, "lightsOut", window.LightsOut);
        WriteInstant(writer, "wake", window.Wake);
        writer.WriteEndObject();
    }

    private static void WriteAppRules(
        Utf8JsonWriter writer,
        IReadOnlyList<DesktopAppRuleDto> rules)
    {
        writer.WriteStartArray("appRules");
        foreach (DesktopAppRuleDto rule in rules)
        {
            writer.WriteStartObject();
            writer.WriteString("id", rule.Id);
            if (rule.RootExecutablePath is null)
            {
                writer.WriteNull("rootExecutablePath");
            }
            else
            {
                writer.WriteString("rootExecutablePath", rule.RootExecutablePath);
            }

            writer.WriteStartArray("helperExecutablePaths");
            foreach (string helper in rule.HelperExecutablePaths)
            {
                writer.WriteStringValue(helper);
            }
            writer.WriteEndArray();
            if (rule.Category is { } category)
            {
                writer.WriteNumber("category", (int)category);
            }
            else
            {
                writer.WriteNull("category");
            }
            writer.WriteNumber("sessionMinutes", rule.SessionMinutes);
            writer.WriteBoolean("isConfigured", rule.IsConfigured);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
    }

    private static void WriteSiteRules(
        Utf8JsonWriter writer,
        IReadOnlyList<DesktopSiteRuleDto> rules)
    {
        writer.WriteStartArray("siteRules");
        foreach (DesktopSiteRuleDto rule in rules)
        {
            writer.WriteStartObject();
            writer.WriteString("domain", rule.Domain);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
    }

    private static void WriteOverride(
        Utf8JsonWriter writer,
        DesktopActiveOverrideDto? activeOverride)
    {
        if (activeOverride is null)
        {
            writer.WriteNull("activeOverride");
            return;
        }

        writer.WriteStartObject("activeOverride");
        writer.WriteNumber("kind", (int)activeOverride.Kind);
        WriteInstant(writer, "requestedAtUtc", activeOverride.RequestedAtUtc);
        WriteInstant(writer, "startsAtUtc", activeOverride.StartsAtUtc);
        WriteInstant(writer, "endsAtUtc", activeOverride.EndsAtUtc);
        writer.WriteStartArray("allowedProcessIdentifiers");
        foreach (string identifier in activeOverride.AllowedProcessIdentifiers)
        {
            writer.WriteStringValue(identifier);
        }
        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    private static void WriteInstant(
        Utf8JsonWriter writer,
        string propertyName,
        DateTimeOffset value)
    {
        writer.WriteStartObject(propertyName);
        writer.WriteNumber("utcTicks", value.UtcTicks);
        writer.WriteNumber("offsetTicks", value.Offset.Ticks);
        writer.WriteEndObject();
    }

    private static ProcessGatePolicySourceResult Unavailable(string code) =>
        new(
            ProcessGateSourceStatus.Unavailable,
            null,
            string.IsNullOrWhiteSpace(code) ? UnavailableCode : code);
}
