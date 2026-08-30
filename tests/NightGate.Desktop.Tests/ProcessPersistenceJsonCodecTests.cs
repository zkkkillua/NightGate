using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace NightGate.Desktop.Tests;

public sealed class ProcessPersistenceJsonCodecTests
{
    private static readonly DateTimeOffset Now = new(
        2026,
        7,
        7,
        0,
        10,
        0,
        TimeSpan.Zero);

    [Fact]
    public void Envelope_RoundTripsEveryFieldAndRestoresComparers()
    {
        ProcessGateEnvelope original = CreateEnvelope();

        Assert.True(ProcessPersistenceJsonCodec.TrySerializeEnvelope(
            original,
            out string json));
        Assert.True(ProcessPersistenceJsonCodec.TryDeserializeEnvelope(
            json,
            out ProcessGateEnvelope? restored));

        Assert.NotNull(restored);
        AssertEnvelopeEquivalent(original, restored);
        Assert.Same(
            StringComparer.OrdinalIgnoreCase,
            restored.ReducerState.RuleStates.KeyComparer);
        Assert.True(restored.ReducerState.RuleStates.ContainsKey("gAmE"));
        Assert.Same(
            StringComparer.Ordinal,
            restored.PolicyLedger.PayloadByEvaluationIdentity.KeyComparer);
    }

    [Fact]
    public void Envelope_DeferredOverride_RoundTrips()
    {
        ProcessGateEnvelope original = CreateEnvelope();
        KeyValuePair<ProcessActionKey, ProcessActionJournalEntry> journal =
            Assert.Single(original.ActionJournal);
        ProcessActionJournalEntry deferred = journal.Value with
        {
            RecheckClaimIdentity = "ng2:0123456789abcdef0123456789abcdef",
            TerminalReason = ProcessActionTerminalReason.RecheckCancelled,
            DeferredByOverride = original.ReducerState.TemporaryOverrideIdentity,
        };
        original = original with
        {
            ActionJournal = original.ActionJournal.SetItem(journal.Key, deferred),
        };

        Assert.True(ProcessPersistenceJsonCodec.TrySerializeEnvelope(original, out string json));
        Assert.DoesNotContain("deferredByOverride", json, StringComparison.Ordinal);
        Assert.Contains("ng2o.", json, StringComparison.Ordinal);
        Assert.True(ProcessPersistenceJsonCodec.TryDeserializeEnvelope(
            json,
            out ProcessGateEnvelope? restored));

        Assert.Equal(
            deferred.DeferredByOverride,
            Assert.Single(restored!.ActionJournal.Values).DeferredByOverride);

        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement legacyJournalValue = document.RootElement
            .GetProperty("envelope")
            .GetProperty("actionJournal")[0]
            .GetProperty("value");
        JsonSerializerOptions legacyOptions = new(JsonSerializerDefaults.Web)
        {
            PropertyNameCaseInsensitive = false,
            RespectRequiredConstructorParameters = true,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        };
        LegacyJournalValueDto? legacy = JsonSerializer.Deserialize<LegacyJournalValueDto>(
            legacyJournalValue.GetRawText(),
            legacyOptions);
        Assert.NotNull(legacy);
        Assert.StartsWith("ng2o.", legacy.RecheckClaimIdentity, StringComparison.Ordinal);

        JsonNode rollbackRoundTrip = JsonNode.Parse(json)!;
        rollbackRoundTrip["envelope"]!["actionJournal"]![0]!["value"] =
            JsonNode.Parse(JsonSerializer.Serialize(legacy, legacyOptions));
        Assert.True(ProcessPersistenceJsonCodec.TryDeserializeEnvelope(
            rollbackRoundTrip.ToJsonString(),
            out ProcessGateEnvelope? restoredAfterRollback));
        Assert.Equal(
            deferred.DeferredByOverride,
            Assert.Single(restoredAfterRollback!.ActionJournal.Values).DeferredByOverride);
    }

    [Fact]
    public void Envelope_LegacyJournalWithoutDeferredOverride_RemainsReadable()
    {
        ProcessGateEnvelope original = CreateEnvelope();
        KeyValuePair<ProcessActionKey, ProcessActionJournalEntry> journal =
            Assert.Single(original.ActionJournal);
        original = original with
        {
            ActionJournal = original.ActionJournal.SetItem(
                journal.Key,
                journal.Value with
                {
                    RecheckClaimIdentity = "0123456789abcdef0123456789abcdef",
                    TerminalReason = ProcessActionTerminalReason.RecheckCancelled,
                }),
        };
        Assert.True(ProcessPersistenceJsonCodec.TrySerializeEnvelope(original, out string legacy));
        Assert.DoesNotContain("deferredByOverride", legacy, StringComparison.Ordinal);

        Assert.True(ProcessPersistenceJsonCodec.TryDeserializeEnvelope(
            legacy,
            out ProcessGateEnvelope? restored));
        ProcessActionJournalEntry restoredEntry = Assert.Single(restored!.ActionJournal.Values);
        Assert.Null(restoredEntry.DeferredByOverride);
        Assert.Equal(
            "0123456789abcdef0123456789abcdef",
            restoredEntry.RecheckClaimIdentity);
    }

    [Fact]
    public void Envelope_InterimDeferredProperty_RemainsReadableAndNormalizesClaimMarker()
    {
        ProcessGateEnvelope original = CreateEnvelope();
        KeyValuePair<ProcessActionKey, ProcessActionJournalEntry> journal =
            Assert.Single(original.ActionJournal);
        ProcessOverrideIdentity deferred = original.ReducerState.TemporaryOverrideIdentity!;
        original = original with
        {
            ActionJournal = original.ActionJournal.SetItem(
                journal.Key,
                journal.Value with
                {
                    RecheckClaimIdentity = "0123456789abcdef0123456789abcdef",
                    TerminalReason = ProcessActionTerminalReason.RecheckCancelled,
                }),
        };
        Assert.True(ProcessPersistenceJsonCodec.TrySerializeEnvelope(original, out string json));
        JsonNode root = JsonNode.Parse(json)!;
        JsonObject value = root["envelope"]!["actionJournal"]![0]!["value"]!.AsObject();
        value["deferredByOverride"] = JsonSerializer.SerializeToNode(new
        {
            kind = "teamRescue",
            requestedAtUtc = deferred.RequestedAtUtc,
            startsAtUtc = deferred.StartsAtUtc,
            endsAtUtc = deferred.EndsAtUtc,
        });

        Assert.True(ProcessPersistenceJsonCodec.TryDeserializeEnvelope(
            root.ToJsonString(),
            out ProcessGateEnvelope? restored));
        ProcessActionJournalEntry restoredEntry = Assert.Single(restored!.ActionJournal.Values);
        Assert.Equal(deferred, restoredEntry.DeferredByOverride);
        Assert.Equal(
            "ng2:0123456789abcdef0123456789abcdef",
            restoredEntry.RecheckClaimIdentity);
    }

    [Fact]
    public void Envelope_InterimExplicitNull_MarksCancellationAsModern()
    {
        ProcessGateEnvelope original = CreateEnvelope();
        KeyValuePair<ProcessActionKey, ProcessActionJournalEntry> journal =
            Assert.Single(original.ActionJournal);
        original = original with
        {
            ActionJournal = original.ActionJournal.SetItem(
                journal.Key,
                journal.Value with
                {
                    RecheckClaimIdentity = "0123456789abcdef0123456789abcdef",
                    TerminalReason = ProcessActionTerminalReason.RecheckCancelled,
                }),
        };
        Assert.True(ProcessPersistenceJsonCodec.TrySerializeEnvelope(original, out string json));
        JsonNode root = JsonNode.Parse(json)!;
        JsonObject value = root["envelope"]!["actionJournal"]![0]!["value"]!.AsObject();
        value["deferredByOverride"] = null;

        Assert.True(ProcessPersistenceJsonCodec.TryDeserializeEnvelope(
            root.ToJsonString(),
            out ProcessGateEnvelope? restored));
        ProcessActionJournalEntry restoredEntry = Assert.Single(restored!.ActionJournal.Values);
        Assert.Null(restoredEntry.DeferredByOverride);
        Assert.False(restoredEntry.IsLegacyRecheckCancellation);
        Assert.Equal(
            "ng2:0123456789abcdef0123456789abcdef",
            restoredEntry.RecheckClaimIdentity);
    }

    [Fact]
    public void Envelope_MalformedEmbeddedDeferredClaim_IsRejected()
    {
        ProcessGateEnvelope original = CreateEnvelope();
        KeyValuePair<ProcessActionKey, ProcessActionJournalEntry> journal =
            Assert.Single(original.ActionJournal);
        original = original with
        {
            ActionJournal = original.ActionJournal.SetItem(
                journal.Key,
                journal.Value with
                {
                    RecheckClaimIdentity = "0123456789abcdef0123456789abcdef",
                    TerminalReason = ProcessActionTerminalReason.RecheckCancelled,
                }),
        };
        Assert.True(ProcessPersistenceJsonCodec.TrySerializeEnvelope(original, out string json));
        string malformed = json.Replace(
            "0123456789abcdef0123456789abcdef",
            "ng2o.invalid",
            StringComparison.Ordinal);
        Assert.NotEqual(json, malformed);

        Assert.False(ProcessPersistenceJsonCodec.TryDeserializeEnvelope(malformed, out _));
    }

    [Fact]
    public void Envelope_DeferredOverrideWithoutRecheckCancellation_IsRejected()
    {
        ProcessGateEnvelope original = CreateEnvelope();
        KeyValuePair<ProcessActionKey, ProcessActionJournalEntry> journal =
            Assert.Single(original.ActionJournal);
        ProcessGateEnvelope invalid = original with
        {
            ActionJournal = original.ActionJournal.SetItem(
                journal.Key,
                journal.Value with
                {
                    DeferredByOverride = original.ReducerState.TemporaryOverrideIdentity,
                }),
        };

        Assert.False(ProcessPersistenceJsonCodec.TrySerializeEnvelope(invalid, out string json));
        Assert.Empty(json);
    }

    [Fact]
    public void Continuity_RoundTripsEveryField()
    {
        ProcessSourceContinuityCheckpoint original = new(
            5,
            ProcessSourceContinuityPhase.Recovering,
            "new-epoch",
            4,
            new(
                ProcessObservationAcknowledgementKind.TrustSeverPersisted,
                "old-epoch",
                4));

        Assert.True(ProcessPersistenceJsonCodec.TrySerializeContinuity(
            original,
            out string json));
        Assert.True(ProcessPersistenceJsonCodec.TryDeserializeContinuity(
            json,
            out ProcessSourceContinuityCheckpoint? restored));

        Assert.Equal(original, restored);
    }

    [Theory]
    [InlineData("{\"schemaVersion\":1,\"envelope\":{},\"extra\":true}")]
    [InlineData("{\"schemaVersion\":1,\"schemaVersion\":1,\"envelope\":{}}")]
    [InlineData("{\"schemaVersion\":1}")]
    [InlineData("{\"schemaVersion\":2,\"envelope\":{}}")]
    [InlineData("{\"schemaVersion\":1,\"envelope\":null}")]
    [InlineData("{\"schemaVersion\":1,\"envelope\":{\"$type\":\"System.Object\"}}")]
    public void Envelope_InvalidJsonShape_IsRejected(string json)
    {
        Assert.False(ProcessPersistenceJsonCodec.TryDeserializeEnvelope(
            json,
            out ProcessGateEnvelope? envelope));
        Assert.Null(envelope);
    }

    [Fact]
    public void Envelope_DuplicateLogicalRuleKey_IsRejectedCaseInsensitively()
    {
        ProcessGateEnvelope original = CreateEnvelope();
        Assert.True(ProcessPersistenceJsonCodec.TrySerializeEnvelope(
            original,
            out string json));
        string duplicateEntry =
            "{\"key\":\"game\",\"value\":{\"ruleId\":\"game\",\"cutoffUtc\":\"2026-07-07T00:05:00+00:00\",\"isSealed\":true}}";
        string corrupted = json.Replace(
            "\"ruleStates\":[",
            $"\"ruleStates\":[{duplicateEntry},",
            StringComparison.Ordinal);

        Assert.False(ProcessPersistenceJsonCodec.TryDeserializeEnvelope(
            corrupted,
            out _));
    }

    [Fact]
    public void Envelope_SemanticallyMismatchedKnownInstanceKey_IsRejected()
    {
        Assert.True(ProcessPersistenceJsonCodec.TrySerializeEnvelope(
            CreateEnvelope(),
            out string json));
        string corrupted = json.Replace(
            "\"knownInstances\":[{\"key\":{\"pid\":123,",
            "\"knownInstances\":[{\"key\":{\"pid\":124,",
            StringComparison.Ordinal);
        Assert.NotEqual(json, corrupted);

        Assert.False(ProcessPersistenceJsonCodec.TryDeserializeEnvelope(
            corrupted,
            out _));
    }

    [Fact]
    public void Envelope_SemanticallyInvalidNestedOverride_IsRejectedBeforeSerialization()
    {
        ProcessGateEnvelope original = CreateEnvelope();
        ProcessOverrideIdentity invalid = new(
            DesktopOverrideKind.Emergency,
            Now,
            Now.AddMinutes(30),
            Now.AddMinutes(20));
        ProcessGateEnvelope corrupted = original with
        {
            ReducerState = original.ReducerState with { OverrideHighWater = invalid },
        };

        Assert.False(ProcessPersistenceJsonCodec.TrySerializeEnvelope(
            corrupted,
            out string json));
        Assert.Empty(json);
    }

    [Theory]
    [InlineData("{\"schemaVersion\":1,\"checkpoint\":{},\"unknown\":0}")]
    [InlineData("{\"schemaVersion\":1,\"checkpoint\":{\"version\":1,\"phase\":\"trusted\",\"observerEpoch\":\"epoch\",\"highestAcceptedTransitionRevision\":0,\"lastAcceptedAcknowledgement\":null}}")]
    [InlineData("{\"schemaVersion\":1,\"checkpoint\":{\"version\":1,\"phase\":\"TRUSTED\",\"observerEpoch\":\"epoch\",\"highestAcceptedTransitionRevision\":1,\"lastAcceptedAcknowledgement\":{\"kind\":\"authoritativeRecoveryPersisted\",\"observerEpoch\":\"epoch\",\"transitionRevision\":1}}}")]
    public void Continuity_InvalidOrSemanticallyDamagedJson_IsRejected(string json)
    {
        Assert.False(ProcessPersistenceJsonCodec.TryDeserializeContinuity(
            json,
            out ProcessSourceContinuityCheckpoint? checkpoint));
        Assert.Null(checkpoint);
    }

    private static ProcessGateEnvelope CreateEnvelope()
    {
        ProcessInstanceKey key = new(123, Now.AddMinutes(-10).UtcTicks);
        ObservedProcessIdentity identity = new(
            key,
            Now.AddMinutes(-10),
            @"C:\Games\Game.exe",
            "S-1-5-21-1000",
            2);
        ProcessOverrideIdentity activeOverride = new(
            DesktopOverrideKind.TeamRescue,
            Now,
            Now,
            Now.AddMinutes(20));
        ProcessOverrideIdentity retiredOverride = new(
            DesktopOverrideKind.Entertainment,
            Now.AddHours(-1),
            Now.AddMinutes(-50),
            Now.AddMinutes(-30));
        ProcessRuleGateState rule = new("Game", Now.AddMinutes(-5), true);
        ProcessGateState state = new(
            new DateOnly(2026, 7, 6),
            Now,
            Now.AddHours(8),
            true,
            "rule-fingerprint",
            ImmutableDictionary<string, ProcessRuleGateState>.Empty
                .WithComparers(StringComparer.OrdinalIgnoreCase)
                .Add("Game", rule),
            ImmutableDictionary<ProcessInstanceKey, ProcessKnownInstance>.Empty
                .Add(key, new(identity, ParentLink.None)),
            ImmutableDictionary<ProcessInstanceKey, string>.Empty.Add(key, "Game"),
            ImmutableDictionary<ProcessInstanceKey, TemporaryProcessGrant>.Empty.Add(
                key,
                new("Game", activeOverride)),
            ImmutableHashSet<ProcessInstanceKey>.Empty.Add(key),
            activeOverride,
            activeOverride,
            activeOverride,
            ImmutableHashSet<ProcessOverrideIdentity>.Empty.Add(retiredOverride),
            "observer-epoch",
            Now.AddMinutes(-1),
            true,
            false);
        ProcessExactTarget target = new(
            key,
            identity.CreationInstantUtc,
            identity.ExecutablePath,
            identity.UserSid,
            identity.SessionId,
            "Game",
            rule.CutoffUtc,
            state.NightDate!.Value,
            state.RuleFingerprint!,
            5,
            "evaluation-5",
            "payload-5",
            Now.AddMinutes(-2));
        ProcessActionJournalEntry journalEntry = new(
            target,
            1,
            ProcessCloseOutcome.Requested,
            null,
            false,
            null,
            null);
        ProcessPolicyLedger ledger = new(
            5,
            "evaluation-5",
            ImmutableDictionary<string, ProcessPolicyPayloadBinding>.Empty
                .WithComparers(StringComparer.Ordinal)
                .Add("evaluation-5", new(5, "payload-5")));
        ProcessObservationContinuityState continuity = new(
            false,
            false,
            "trusted-epoch",
            null,
            "clock-epoch",
            Now,
            TimeSpan.FromHours(10),
            new(
                ProcessObservationAcknowledgementKind.AuthoritativeRecoveryPersisted,
                "trusted-epoch",
                6,
                true));
        return new(
            7,
            state,
            ImmutableDictionary<ProcessActionKey, ProcessActionJournalEntry>.Empty.Add(
                target.ActionKey,
                journalEntry),
            ledger,
            continuity,
            2);
    }

    private static void AssertEnvelopeEquivalent(
        ProcessGateEnvelope expected,
        ProcessGateEnvelope actual)
    {
        Assert.Equal(expected.Revision, actual.Revision);
        Assert.Equal(expected.ReducerState.NightDate, actual.ReducerState.NightDate);
        Assert.Equal(expected.ReducerState.LastEffectiveLogicalTime, actual.ReducerState.LastEffectiveLogicalTime);
        Assert.Equal(expected.ReducerState.CommittedWake, actual.ReducerState.CommittedWake);
        Assert.Equal(expected.ReducerState.IsCommittedWakeLocked, actual.ReducerState.IsCommittedWakeLocked);
        Assert.Equal(expected.ReducerState.RuleFingerprint, actual.ReducerState.RuleFingerprint);
        Assert.Equal(expected.ReducerState.RuleStates.Count, actual.ReducerState.RuleStates.Count);
        Assert.Equal(expected.ReducerState.KnownInstances.Count, actual.ReducerState.KnownInstances.Count);
        Assert.Equal(expected.ReducerState.EligibleInstances.Count, actual.ReducerState.EligibleInstances.Count);
        Assert.Equal(expected.ReducerState.TemporaryInstances.Count, actual.ReducerState.TemporaryInstances.Count);
        Assert.True(expected.ReducerState.TaintedInstances.SetEquals(actual.ReducerState.TaintedInstances));
        Assert.Equal(expected.ReducerState.TemporaryOverrideIdentity, actual.ReducerState.TemporaryOverrideIdentity);
        Assert.Equal(expected.ReducerState.CapturedTeamRescueOverride, actual.ReducerState.CapturedTeamRescueOverride);
        Assert.Equal(expected.ReducerState.OverrideHighWater, actual.ReducerState.OverrideHighWater);
        Assert.True(expected.ReducerState.RetiredOverrideIdentities.SetEquals(actual.ReducerState.RetiredOverrideIdentities));
        Assert.Equal(expected.ReducerState.ObserverContinuityEpoch, actual.ReducerState.ObserverContinuityEpoch);
        Assert.Equal(expected.ReducerState.PreOverrideBaselineObservedAtUtc, actual.ReducerState.PreOverrideBaselineObservedAtUtc);
        Assert.Equal(expected.ReducerState.CreationTimelineTrusted, actual.ReducerState.CreationTimelineTrusted);
        Assert.Equal(expected.ReducerState.MorningReleased, actual.ReducerState.MorningReleased);
        Assert.Equal(expected.ActionJournal.Single().Key, actual.ActionJournal.Single().Key);
        Assert.Equal(expected.ActionJournal.Single().Value, actual.ActionJournal.Single().Value);
        Assert.Equal(expected.PolicyLedger.HighestRevision, actual.PolicyLedger.HighestRevision);
        Assert.Equal(expected.PolicyLedger.HighestEvaluationIdentity, actual.PolicyLedger.HighestEvaluationIdentity);
        Assert.Equal(
            expected.PolicyLedger.PayloadByEvaluationIdentity.Single(),
            actual.PolicyLedger.PayloadByEvaluationIdentity.Single());
        Assert.Equal(expected.ObservationContinuity, actual.ObservationContinuity);
        Assert.Equal(expected.NextJournalSequence, actual.NextJournalSequence);
    }

    private sealed record LegacyJournalValueDto(
        JsonElement Target,
        long Sequence,
        string? CloseCompletion,
        string? RecheckClaimIdentity,
        bool TerminationClaimed,
        string? TerminationCompletion,
        string? TerminalReason);
}
