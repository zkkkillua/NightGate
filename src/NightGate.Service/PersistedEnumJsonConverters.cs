using System.Text.Json;
using System.Text.Json.Serialization;
using NightGate.Core;

namespace NightGate.Service;

internal abstract class ExactEnumJsonConverter<T> : JsonConverter<T>
    where T : struct, Enum
{
    public sealed override T Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String
            || !TryParse(reader.GetString(), out T value))
        {
            throw new JsonException($"Invalid persisted {typeof(T).Name} token.");
        }

        return value;
    }

    public sealed override void Write(
        Utf8JsonWriter writer,
        T value,
        JsonSerializerOptions options)
    {
        string? token = GetToken(value);
        if (token is null)
        {
            throw new JsonException($"Invalid persisted {typeof(T).Name} value.");
        }

        writer.WriteStringValue(token);
    }

    protected abstract bool TryParse(string? token, out T value);

    protected abstract string? GetToken(T value);
}

internal sealed class ExactNightPhaseJsonConverter : ExactEnumJsonConverter<NightPhase>
{
    protected override bool TryParse(string? token, out NightPhase value)
    {
        value = token switch
        {
            "Free" => NightPhase.Free,
            "LastStart" => NightPhase.LastStart,
            "Grace" => NightPhase.Grace,
            "LandingLocked" => NightPhase.LandingLocked,
            "Morning" => NightPhase.Morning,
            "CoolingOff" => NightPhase.CoolingOff,
            "OverrideActive" => NightPhase.OverrideActive,
            _ => default,
        };
        return token is "Free" or "LastStart" or "Grace" or "LandingLocked" or
            "Morning" or "CoolingOff" or "OverrideActive";
    }

    protected override string? GetToken(NightPhase value) => value switch
    {
        NightPhase.Free => "Free",
        NightPhase.LastStart => "LastStart",
        NightPhase.Grace => "Grace",
        NightPhase.LandingLocked => "LandingLocked",
        NightPhase.Morning => "Morning",
        NightPhase.CoolingOff => "CoolingOff",
        NightPhase.OverrideActive => "OverrideActive",
        _ => null,
    };
}

internal sealed class ExactOverrideKindJsonConverter : ExactEnumJsonConverter<OverrideKind>
{
    protected override bool TryParse(string? token, out OverrideKind value)
    {
        value = token switch
        {
            "TeamRescue" => OverrideKind.TeamRescue,
            "Emergency" => OverrideKind.Emergency,
            "Entertainment" => OverrideKind.Entertainment,
            _ => default,
        };
        return token is "TeamRescue" or "Emergency" or "Entertainment";
    }

    protected override string? GetToken(OverrideKind value) => value switch
    {
        OverrideKind.TeamRescue => "TeamRescue",
        OverrideKind.Emergency => "Emergency",
        OverrideKind.Entertainment => "Entertainment",
        _ => null,
    };
}

internal sealed class ExactNightEventKindJsonConverter : ExactEnumJsonConverter<NightEventKind>
{
    protected override bool TryParse(string? token, out NightEventKind value)
    {
        value = token switch
        {
            "NightStarted" => NightEventKind.NightStarted,
            "StateObserved" => NightEventKind.StateObserved,
            "BasePhaseAdvanced" => NightEventKind.BasePhaseAdvanced,
            "OverrideRequested" => NightEventKind.OverrideRequested,
            "OverrideEnded" => NightEventKind.OverrideEnded,
            "NightClosed" => NightEventKind.NightClosed,
            "HistoryCleared" => NightEventKind.HistoryCleared,
            "ServiceDegraded" => NightEventKind.ServiceDegraded,
            "DeliberateBypass" => NightEventKind.DeliberateBypass,
            "LateNewEntertainment" => NightEventKind.LateNewEntertainment,
            "MissedLock" => NightEventKind.MissedLock,
            "WorkstationLocked" => NightEventKind.WorkstationLocked,
            _ => default,
        };
        return token is "NightStarted" or "StateObserved" or "BasePhaseAdvanced" or
            "OverrideRequested" or "OverrideEnded" or "NightClosed" or "HistoryCleared" or
            "ServiceDegraded" or "DeliberateBypass" or "LateNewEntertainment" or "MissedLock" or
            "WorkstationLocked";
    }

    protected override string? GetToken(NightEventKind value) => value switch
    {
        NightEventKind.NightStarted => "NightStarted",
        NightEventKind.StateObserved => "StateObserved",
        NightEventKind.BasePhaseAdvanced => "BasePhaseAdvanced",
        NightEventKind.OverrideRequested => "OverrideRequested",
        NightEventKind.OverrideEnded => "OverrideEnded",
        NightEventKind.NightClosed => "NightClosed",
        NightEventKind.HistoryCleared => "HistoryCleared",
        NightEventKind.ServiceDegraded => "ServiceDegraded",
        NightEventKind.DeliberateBypass => "DeliberateBypass",
        NightEventKind.LateNewEntertainment => "LateNewEntertainment",
        NightEventKind.MissedLock => "MissedLock",
        NightEventKind.WorkstationLocked => "WorkstationLocked",
        _ => null,
    };
}
