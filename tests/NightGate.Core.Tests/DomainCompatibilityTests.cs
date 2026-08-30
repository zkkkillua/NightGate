using System.Text.Json;
using System.Text.Json.Serialization;
using NightGate.Core;

namespace NightGate.Core.Tests;

public sealed class DomainCompatibilityTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    [Fact]
    public void StoredV1ProgressJsonDefaultsAllPendingFields()
    {
        const string json =
            "{\"currentStep\":2,\"lastTeamRescueAtUtc\":null,\"lastProgressionNightDate\":\"2026-07-06\"}";

        ProgressState state = JsonSerializer.Deserialize<ProgressState>(json, JsonOptions)!;

        Assert.Equal(2, state.CurrentStep);
        Assert.Null(state.PendingStep);
        Assert.Null(state.PendingStepUnlockedByNightDate);
        Assert.Null(state.PendingStepConfirmedAtUtc);
        Assert.Null(state.PendingStepEffectiveNightDate);
    }

    [Fact]
    public void StoredV1NightStateJsonDefaultsReportFacts()
    {
        const string json =
            """
            {
              "nightId":"aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa",
              "nightDate":"2026-07-06",
              "lastObservedUtc":"2026-07-07T00:40:00+00:00",
              "highestBasePhaseReached":3,
              "activeOverride":null,
              "emergencyUsed":false,
              "teamRescueUsed":false,
              "entertainmentUsed":false,
              "deliberateBypass":false,
              "lateNewEntertainment":false,
              "missedLock":false,
              "isClosed":false,
              "lastObservedUptime":null,
              "lastObservedBootSessionId":null
            }
            """;

        NightState state = JsonSerializer.Deserialize<NightState>(json, JsonOptions)!;

        Assert.Equal(OverrideReasonSummary.Empty, state.OverrideReasons);
        Assert.Null(state.FirstLockObservedAtUtc);
        Assert.Null(state.ScheduledLockAtUtc);
        Assert.False(state.ProtectionGapObserved);
        Assert.Null(state.ScheduleTimeZoneSerialized);
    }

    [Fact]
    public void StoredV1NightOutcomeJsonRemainsReadableButCannotProveQualification()
    {
        const string json =
            """
            {
              "nightId":"bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb",
              "nightDate":"2026-07-06",
              "closedAtUtc":"2026-07-07T09:00:00+00:00",
              "emergencyUsed":false,
              "teamRescueUsed":false,
              "entertainmentUsed":false,
              "deliberateBypass":false,
              "lateNewEntertainment":false,
              "missedLock":false
            }
            """;

        NightOutcome outcome = JsonSerializer.Deserialize<NightOutcome>(json, JsonOptions)!;

        Assert.Equal(OverrideReasonSummary.Empty, outcome.OverrideReasons);
        Assert.Null(outcome.FirstLockObservedAtUtc);
        Assert.Null(outcome.ScheduledLockAtUtc);
        Assert.False(outcome.ProtectionGapObserved);
        Assert.Null(outcome.ScheduleTimeZoneSerialized);
        Assert.True(outcome.IsEligible);
        Assert.False(outcome.Qualifies);
    }

    [Fact]
    public void StoredV2NightOutcomeDefaultsProtectionGapAndCannotProveQualification()
    {
        const string json =
            """
            {
              "nightId":"cccccccc-cccc-cccc-cccc-cccccccccccc",
              "nightDate":"2026-07-06",
              "closedAtUtc":"2026-07-07T09:00:00+00:00",
              "emergencyUsed":false,
              "teamRescueUsed":false,
              "entertainmentUsed":false,
              "deliberateBypass":false,
              "lateNewEntertainment":false,
              "missedLock":false,
              "overrideReasons":{
                "teamRescueCount":0,
                "entertainmentCount":0,
                "emergencyHealthCount":0,
                "emergencySafetyCount":0,
                "emergencyUrgentWorkCount":0,
                "emergencyOtherCount":0
              },
              "firstLockObservedAtUtc":"2026-07-07T00:40:00+00:00"
            }
            """;

        NightOutcome outcome = JsonSerializer.Deserialize<NightOutcome>(json, JsonOptions)!;

        Assert.False(outcome.ProtectionGapObserved);
        Assert.Null(outcome.ScheduleTimeZoneSerialized);
        Assert.True(outcome.IsEligible);
        Assert.False(outcome.Qualifies);
    }
}
