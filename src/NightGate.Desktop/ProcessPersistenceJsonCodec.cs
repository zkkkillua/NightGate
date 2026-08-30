using System.Collections.Immutable;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using NightGate.Core;

namespace NightGate.Desktop;

internal static class ProcessPersistenceJsonCodec
{
    private const int SchemaVersion = ProcessPersistenceLimits.CurrentSchemaVersion;
    private const int MaximumStateEntries = 2_048;
    private const int MaximumJournalEntries = 256;
    private const int MaximumPolicyBindings = 64;
    private const int MaximumIdentifierCharacters = 512;
    private const string EncodedDeferredClaimPrefix = "ng2o.";
    private static readonly JsonSerializerOptions Options = CreateOptions();

    internal static bool TrySerializeEnvelope(
        ProcessGateEnvelope envelope,
        out string json)
    {
        json = string.Empty;
        try
        {
            if (!IsValidEnvelope(envelope))
            {
                return false;
            }

            json = JsonSerializer.Serialize(
                new EnvelopeRootDto(SchemaVersion, ToDto(envelope)),
                Options);
            if (!ProcessPersistenceLimits.IsValidPayload(json, SchemaVersion))
            {
                json = string.Empty;
                return false;
            }

            if (!TryDeserializeEnvelope(json, out _))
            {
                json = string.Empty;
                return false;
            }

            return true;
        }
        catch (Exception exception) when (exception is JsonException
            or InvalidDataException
            or ArgumentException
            or OverflowException
            or EncoderFallbackException)
        {
            json = string.Empty;
            return false;
        }
    }

    internal static bool TryDeserializeEnvelope(
        string json,
        out ProcessGateEnvelope? envelope)
    {
        envelope = null;
        try
        {
            if (!ProcessPersistenceLimits.IsValidPayload(json, SchemaVersion))
            {
                return false;
            }

            EnvelopeRootDto? root = JsonSerializer.Deserialize<EnvelopeRootDto>(json, Options);
            if (root is null
                || root.SchemaVersion != SchemaVersion
                || root.Envelope is null
                || !TryFromDto(root.Envelope, out ProcessGateEnvelope? candidate)
                || !IsValidEnvelope(candidate))
            {
                return false;
            }

            envelope = candidate;
            return true;
        }
        catch (Exception exception) when (exception is JsonException
            or InvalidDataException
            or ArgumentException
            or OverflowException)
        {
            return false;
        }
    }

    internal static bool TrySerializeContinuity(
        ProcessSourceContinuityCheckpoint checkpoint,
        out string json)
    {
        json = string.Empty;
        try
        {
            if (!ProcessSourceContinuityReducer.IsValidCheckpoint(checkpoint))
            {
                return false;
            }

            json = JsonSerializer.Serialize(
                new ContinuityRootDto(SchemaVersion, ToDto(checkpoint)),
                Options);
            if (!ProcessPersistenceLimits.IsValidPayload(json, SchemaVersion))
            {
                json = string.Empty;
                return false;
            }

            return true;
        }
        catch (Exception exception) when (exception is JsonException
            or InvalidDataException
            or ArgumentException
            or OverflowException
            or EncoderFallbackException)
        {
            json = string.Empty;
            return false;
        }
    }

    internal static bool TryDeserializeContinuity(
        string json,
        out ProcessSourceContinuityCheckpoint? checkpoint)
    {
        checkpoint = null;
        try
        {
            if (!ProcessPersistenceLimits.IsValidPayload(json, SchemaVersion))
            {
                return false;
            }

            ContinuityRootDto? root = JsonSerializer.Deserialize<ContinuityRootDto>(json, Options);
            if (root is null
                || root.SchemaVersion != SchemaVersion
                || root.Checkpoint is null
                || !TryFromDto(
                    root.Checkpoint,
                    out ProcessSourceContinuityCheckpoint? candidate)
                || !ProcessSourceContinuityReducer.IsValidCheckpoint(candidate))
            {
                return false;
            }

            checkpoint = candidate;
            return true;
        }
        catch (Exception exception) when (exception is JsonException
            or InvalidDataException
            or ArgumentException
            or OverflowException)
        {
            return false;
        }
    }

    private static EnvelopeDto ToDto(ProcessGateEnvelope envelope) => new(
        envelope.Revision,
        ToDto(envelope.ReducerState),
        envelope.ActionJournal
            .OrderBy(pair => pair.Value.Sequence)
            .Select(pair => new JournalEntryDto(
                ToDto(pair.Key),
                ToDto(pair.Value)))
            .ToArray(),
        new(
            envelope.PolicyLedger.HighestRevision,
            envelope.PolicyLedger.HighestEvaluationIdentity,
            envelope.PolicyLedger.PayloadByEvaluationIdentity
                .OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .Select(pair => new PolicyBindingEntryDto(
                    pair.Key,
                    pair.Value.Revision,
                    pair.Value.PayloadFingerprint))
                .ToArray()),
        ToDto(envelope.ObservationContinuity),
        envelope.NextJournalSequence);

    private static StateDto ToDto(ProcessGateState state) => new(
        state.NightDate,
        state.LastEffectiveLogicalTime,
        state.CommittedWake,
        state.IsCommittedWakeLocked,
        state.RuleFingerprint,
        state.RuleStates
            .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
            .Select(pair => new RuleEntryDto(
                pair.Key,
                new(pair.Value.RuleId, pair.Value.CutoffUtc, pair.Value.IsSealed)))
            .ToArray(),
        state.KnownInstances
            .OrderBy(pair => pair.Key.Pid)
            .ThenBy(pair => pair.Key.CreationUtcTicks)
            .Select(pair => new KnownEntryDto(
                ToDto(pair.Key),
                new(ToDto(pair.Value.Identity), ToDto(pair.Value.Parent))))
            .ToArray(),
        state.EligibleInstances
            .OrderBy(pair => pair.Key.Pid)
            .ThenBy(pair => pair.Key.CreationUtcTicks)
            .Select(pair => new EligibleEntryDto(ToDto(pair.Key), pair.Value))
            .ToArray(),
        state.TemporaryInstances
            .OrderBy(pair => pair.Key.Pid)
            .ThenBy(pair => pair.Key.CreationUtcTicks)
            .Select(pair => new TemporaryEntryDto(
                ToDto(pair.Key),
                new(pair.Value.RuleId, ToDto(pair.Value.OverrideIdentity))))
            .ToArray(),
        state.TaintedInstances
            .OrderBy(key => key.Pid)
            .ThenBy(key => key.CreationUtcTicks)
            .Select(ToDto)
            .ToArray(),
        state.TemporaryOverrideIdentity is null
            ? null
            : ToDto(state.TemporaryOverrideIdentity),
        state.CapturedTeamRescueOverride is null
            ? null
            : ToDto(state.CapturedTeamRescueOverride),
        state.OverrideHighWater is null ? null : ToDto(state.OverrideHighWater),
        state.RetiredOverrideIdentities
            .OrderBy(value => value.RequestedAtUtc)
            .ThenBy(value => OverrideKindToken(value.Kind), StringComparer.Ordinal)
            .Select(ToDto)
            .ToArray(),
        state.ObserverContinuityEpoch,
        state.PreOverrideBaselineObservedAtUtc,
        state.CreationTimelineTrusted,
        state.MorningReleased);

    private static KeyDto ToDto(ProcessInstanceKey key) => new(
        key.Pid,
        key.CreationUtcTicks);

    private static IdentityDto ToDto(ObservedProcessIdentity identity) => new(
        ToDto(identity.Key),
        identity.CreationInstantUtc,
        identity.ExecutablePath,
        identity.UserSid,
        identity.SessionId);

    private static ParentDto ToDto(ParentLink parent) => parent.Kind switch
    {
        ParentLinkKind.None => new("none", null),
        ParentLinkKind.Unknown => new("unknown", null),
        ParentLinkKind.Exact when parent.ExactParent is { } exact =>
            new("exact", ToDto(exact)),
        _ => throw new InvalidDataException("Invalid parent link."),
    };

    private static OverrideDto ToDto(ProcessOverrideIdentity value) => new(
        OverrideKindToken(value.Kind),
        value.RequestedAtUtc,
        value.StartsAtUtc,
        value.EndsAtUtc);

    private static ActionKeyDto ToDto(ProcessActionKey key) => new(
        ToDto(key.InstanceKey),
        key.NightDate,
        key.RuleId,
        key.CutoffUtc,
        key.RuleFingerprint);

    private static JournalValueDto ToDto(ProcessActionJournalEntry entry) => new(
        ToDto(entry.Target),
        entry.Sequence,
        entry.CloseCompletion is null ? null : CloseOutcomeToken(entry.CloseCompletion.Value),
        EncodeRecheckClaim(entry.RecheckClaimIdentity, entry.DeferredByOverride),
        entry.TerminationClaimed,
        entry.TerminationCompletion is null
            ? null
            : TerminationOutcomeToken(entry.TerminationCompletion.Value),
        entry.TerminalReason is null ? null : TerminalReasonToken(entry.TerminalReason.Value));

    private static TargetDto ToDto(ProcessExactTarget target) => new(
        ToDto(target.InstanceKey),
        target.CreationInstantUtc,
        target.ExecutablePath,
        target.UserSid,
        target.SessionId,
        target.RuleId,
        target.CutoffUtc,
        target.NightDate,
        target.RuleFingerprint,
        target.OriginalPolicyRevision,
        target.OriginalPolicyIdentity,
        target.OriginalPolicyPayloadFingerprint,
        target.OriginalPolicyEvaluatedAtUtc);

    private static ObservationContinuityDto ToDto(
        ProcessObservationContinuityState value) => new(
        value.IsLost,
        value.TrustSeverPersisted,
        value.LastTrustedEpoch,
        value.LossEpoch,
        value.ClockEpoch,
        value.SampleUtcHighWater,
        value.SampleMonotonicHighWater,
        value.AcknowledgementCheckpoint is null
            ? null
            : new(
                AcknowledgementKindToken(value.AcknowledgementCheckpoint.Kind),
                value.AcknowledgementCheckpoint.ObserverEpoch,
                value.AcknowledgementCheckpoint.TransitionRevision,
                value.AcknowledgementCheckpoint.Delivered));

    private static SourceCheckpointDto ToDto(
        ProcessSourceContinuityCheckpoint checkpoint) => new(
        checkpoint.Version,
        ContinuityPhaseToken(checkpoint.Phase),
        checkpoint.ObserverEpoch,
        checkpoint.HighestAcceptedTransitionRevision,
        checkpoint.LastAcceptedAcknowledgement is null
            ? null
            : new(
                AcknowledgementKindToken(
                    checkpoint.LastAcceptedAcknowledgement.Kind),
                checkpoint.LastAcceptedAcknowledgement.ObserverEpoch,
                checkpoint.LastAcceptedAcknowledgement.TransitionRevision));

    private static bool TryFromDto(
        EnvelopeDto dto,
        out ProcessGateEnvelope? envelope)
    {
        envelope = null;
        if (dto.Revision is < 1 or long.MaxValue
            || dto.ReducerState is null
            || dto.ActionJournal is null
            || dto.ActionJournal.Length > MaximumJournalEntries
            || dto.PolicyLedger is null
            || dto.ObservationContinuity is null
            || dto.NextJournalSequence < 1
            || !TryFromDto(dto.ReducerState, out ProcessGateState? state)
            || !TryFromDto(dto.PolicyLedger, out ProcessPolicyLedger? ledger)
            || !TryFromDto(
                dto.ObservationContinuity,
                out ProcessObservationContinuityState? continuity))
        {
            return false;
        }

        ImmutableDictionary<ProcessActionKey, ProcessActionJournalEntry>.Builder journal =
            ImmutableDictionary.CreateBuilder<ProcessActionKey, ProcessActionJournalEntry>();
        HashSet<long> sequences = [];
        foreach (JournalEntryDto? item in dto.ActionJournal)
        {
            if (item is null
                || !TryFromDto(item.Key, out ProcessActionKey key)
                || !TryFromDto(item.Value, out ProcessActionJournalEntry? value)
                || key != value!.Target.ActionKey
                || !journal.TryAdd(key, value)
                || !sequences.Add(value.Sequence))
            {
                return false;
            }
        }

        envelope = new(
            dto.Revision,
            state!,
            journal.ToImmutable(),
            ledger!,
            continuity!,
            dto.NextJournalSequence);
        return true;
    }

    private static bool TryFromDto(StateDto dto, out ProcessGateState? state)
    {
        state = null;
        if (dto.RuleStates is null
            || dto.KnownInstances is null
            || dto.EligibleInstances is null
            || dto.TemporaryInstances is null
            || dto.TaintedInstances is null
            || dto.RetiredOverrideIdentities is null
            || dto.RuleStates.Length > MaximumStateEntries
            || dto.KnownInstances.Length > MaximumStateEntries
            || dto.EligibleInstances.Length > MaximumStateEntries
            || dto.TemporaryInstances.Length > MaximumStateEntries
            || dto.TaintedInstances.Length > MaximumStateEntries
            || dto.RetiredOverrideIdentities.Length > MaximumStateEntries
            || !IsUtc(dto.LastEffectiveLogicalTime)
            || !IsUtc(dto.CommittedWake)
            || !IsUtc(dto.PreOverrideBaselineObservedAtUtc)
            || !IsOptionalIdentifier(dto.RuleFingerprint)
            || !IsOptionalIdentifier(dto.ObserverContinuityEpoch))
        {
            return false;
        }

        ImmutableDictionary<string, ProcessRuleGateState>.Builder rules =
            ImmutableDictionary.CreateBuilder<string, ProcessRuleGateState>(
                StringComparer.OrdinalIgnoreCase);
        foreach (RuleEntryDto? item in dto.RuleStates)
        {
            if (item?.Value is null
                || !IsIdentifier(item.Key)
                || !IsIdentifier(item.Value.RuleId)
                || !string.Equals(
                    item.Key,
                    item.Value.RuleId,
                    StringComparison.OrdinalIgnoreCase)
                || !IsUtc(item.Value.CutoffUtc)
                || !rules.TryAdd(
                    item.Key,
                    new(
                        item.Value.RuleId,
                        item.Value.CutoffUtc,
                        item.Value.IsSealed)))
            {
                return false;
            }
        }

        ImmutableDictionary<ProcessInstanceKey, ProcessKnownInstance>.Builder known =
            ImmutableDictionary.CreateBuilder<ProcessInstanceKey, ProcessKnownInstance>();
        foreach (KnownEntryDto? item in dto.KnownInstances)
        {
            if (item?.Value is null
                || !TryFromDto(item.Key, out ProcessInstanceKey key)
                || !TryFromDto(item.Value.Identity, out ObservedProcessIdentity? identity)
                || !TryFromDto(item.Value.Parent, out ParentLink parent)
                || identity!.Key != key
                || !known.TryAdd(key, new(identity, parent)))
            {
                return false;
            }
        }

        ImmutableDictionary<ProcessInstanceKey, string>.Builder eligible =
            ImmutableDictionary.CreateBuilder<ProcessInstanceKey, string>();
        foreach (EligibleEntryDto? item in dto.EligibleInstances)
        {
            if (item is null
                || !TryFromDto(item.Key, out ProcessInstanceKey key)
                || !IsIdentifier(item.RuleId)
                || !eligible.TryAdd(key, item.RuleId))
            {
                return false;
            }
        }

        ImmutableDictionary<ProcessInstanceKey, TemporaryProcessGrant>.Builder temporary =
            ImmutableDictionary.CreateBuilder<ProcessInstanceKey, TemporaryProcessGrant>();
        foreach (TemporaryEntryDto? item in dto.TemporaryInstances)
        {
            if (item?.Value is null
                || !TryFromDto(item.Key, out ProcessInstanceKey key)
                || !IsIdentifier(item.Value.RuleId)
                || !TryFromDto(
                    item.Value.OverrideIdentity,
                    out ProcessOverrideIdentity? identity)
                || !temporary.TryAdd(
                    key,
                    new(item.Value.RuleId, identity!)))
            {
                return false;
            }
        }

        ImmutableHashSet<ProcessInstanceKey>.Builder tainted =
            ImmutableHashSet.CreateBuilder<ProcessInstanceKey>();
        foreach (KeyDto? item in dto.TaintedInstances)
        {
            if (!TryFromDto(item, out ProcessInstanceKey key)
                || !tainted.Add(key))
            {
                return false;
            }
        }

        if (!TryOptionalOverride(dto.TemporaryOverrideIdentity, out ProcessOverrideIdentity? current)
            || !TryOptionalOverride(
                dto.CapturedTeamRescueOverride,
                out ProcessOverrideIdentity? captured)
            || !TryOptionalOverride(dto.OverrideHighWater, out ProcessOverrideIdentity? highWater))
        {
            return false;
        }

        ImmutableHashSet<ProcessOverrideIdentity>.Builder retired =
            ImmutableHashSet.CreateBuilder<ProcessOverrideIdentity>();
        foreach (OverrideDto? item in dto.RetiredOverrideIdentities)
        {
            if (!TryFromDto(item, out ProcessOverrideIdentity? identity)
                || !retired.Add(identity!))
            {
                return false;
            }
        }

        state = new(
            dto.NightDate,
            dto.LastEffectiveLogicalTime,
            dto.CommittedWake,
            dto.IsCommittedWakeLocked,
            dto.RuleFingerprint,
            rules.ToImmutable(),
            known.ToImmutable(),
            eligible.ToImmutable(),
            temporary.ToImmutable(),
            tainted.ToImmutable(),
            current,
            captured,
            highWater,
            retired.ToImmutable(),
            dto.ObserverContinuityEpoch,
            dto.PreOverrideBaselineObservedAtUtc,
            dto.CreationTimelineTrusted,
            dto.MorningReleased);
        return true;
    }

    private static bool TryFromDto(KeyDto? dto, out ProcessInstanceKey key)
    {
        key = default;
        if (dto is null
            || dto.Pid <= 0
            || dto.CreationUtcTicks < DateTime.MinValue.Ticks
            || dto.CreationUtcTicks > DateTime.MaxValue.Ticks)
        {
            return false;
        }

        key = new(dto.Pid, dto.CreationUtcTicks);
        return true;
    }

    private static bool TryFromDto(
        IdentityDto? dto,
        out ObservedProcessIdentity? identity)
    {
        identity = null;
        if (dto is null
            || !TryFromDto(dto.Key, out ProcessInstanceKey key)
            || !IsUtc(dto.CreationInstantUtc)
            || dto.CreationInstantUtc.UtcTicks != key.CreationUtcTicks
            || !IsCanonicalExecutablePath(dto.ExecutablePath)
            || !IsSid(dto.UserSid)
            || dto.SessionId < 0)
        {
            return false;
        }

        identity = new(
            key,
            dto.CreationInstantUtc,
            dto.ExecutablePath,
            dto.UserSid,
            dto.SessionId);
        return true;
    }

    private static bool TryFromDto(ParentDto? dto, out ParentLink parent)
    {
        parent = ParentLink.Unknown;
        if (dto is null)
        {
            return false;
        }

        switch (dto.Kind)
        {
            case "none" when dto.ExactParent is null:
                parent = ParentLink.None;
                return true;
            case "unknown" when dto.ExactParent is null:
                parent = ParentLink.Unknown;
                return true;
            case "exact" when TryFromDto(dto.ExactParent, out ProcessInstanceKey exact):
                parent = ParentLink.Exact(exact);
                return true;
            default:
                return false;
        }
    }

    private static bool TryOptionalOverride(
        OverrideDto? dto,
        out ProcessOverrideIdentity? identity)
    {
        if (dto is null)
        {
            identity = null;
            return true;
        }

        return TryFromDto(dto, out identity);
    }

    private static bool TryFromDto(
        OverrideDto? dto,
        out ProcessOverrideIdentity? identity)
    {
        identity = null;
        if (dto is null
            || !TryParseOverrideKind(dto.Kind, out DesktopOverrideKind kind)
            || !IsUtc(dto.RequestedAtUtc)
            || !IsUtc(dto.StartsAtUtc)
            || !IsUtc(dto.EndsAtUtc)
            || dto.RequestedAtUtc > dto.StartsAtUtc
            || dto.StartsAtUtc >= dto.EndsAtUtc)
        {
            return false;
        }

        identity = new(kind, dto.RequestedAtUtc, dto.StartsAtUtc, dto.EndsAtUtc);
        return true;
    }

    private static bool TryFromDto(ActionKeyDto? dto, out ProcessActionKey key)
    {
        key = default;
        if (dto is null
            || !TryFromDto(dto.InstanceKey, out ProcessInstanceKey instanceKey)
            || !IsIdentifier(dto.RuleId)
            || !IsUtc(dto.CutoffUtc)
            || !IsIdentifier(dto.RuleFingerprint))
        {
            return false;
        }

        key = new(
            instanceKey,
            dto.NightDate,
            dto.RuleId,
            dto.CutoffUtc,
            dto.RuleFingerprint);
        return true;
    }

    private static bool TryFromDto(
        JournalValueDto? dto,
        out ProcessActionJournalEntry? entry)
    {
        entry = null;
        if (dto is null
            || !TryFromDto(dto.Target, out ProcessExactTarget? target)
            || dto.Sequence < 1
            || !TryParseOptionalCloseOutcome(dto.CloseCompletion, out ProcessCloseOutcome? close)
            || !TryDecodeRecheckClaim(
                dto.RecheckClaimIdentity,
                out string? recheckClaimIdentity,
                out ProcessOverrideIdentity? embeddedOverride)
            || !TryParseOptionalTerminationOutcome(
                dto.TerminationCompletion,
                out ProcessTerminationOutcome? termination)
            || !TryParseOptionalTerminalReason(
                dto.TerminalReason,
                out ProcessActionTerminalReason? terminalReason)
            || !TryOptionalOverride(
                dto.DeferredByOverride,
                out ProcessOverrideIdentity? propertyOverride))
        {
            return false;
        }

        if (embeddedOverride is not null
            && propertyOverride is not null
            && embeddedOverride != propertyOverride)
        {
            return false;
        }

        if (dto.DeferredByOverrideSpecified
            && recheckClaimIdentity is not null
            && !recheckClaimIdentity.StartsWith(
                ProcessActionJournalEntry.ModernRecheckClaimPrefix,
                StringComparison.Ordinal)
            && !TryMarkModernRecheckClaim(
                recheckClaimIdentity,
                out recheckClaimIdentity))
        {
            return false;
        }

        ProcessOverrideIdentity? deferredByOverride = embeddedOverride ?? propertyOverride;
        if (deferredByOverride is not null
            && (terminalReason != ProcessActionTerminalReason.RecheckCancelled
                || recheckClaimIdentity is null
                || !recheckClaimIdentity.StartsWith(
                    ProcessActionJournalEntry.ModernRecheckClaimPrefix,
                    StringComparison.Ordinal)))
        {
            return false;
        }

        entry = new(
            target!,
            dto.Sequence,
            close,
            recheckClaimIdentity,
            dto.TerminationClaimed,
            termination,
            terminalReason)
        {
            DeferredByOverride = deferredByOverride,
        };
        return true;
    }

    private static string? EncodeRecheckClaim(
        string? recheckClaimIdentity,
        ProcessOverrideIdentity? deferredByOverride)
    {
        if (deferredByOverride is null)
        {
            return recheckClaimIdentity;
        }

        if (recheckClaimIdentity is null
            || !recheckClaimIdentity.StartsWith(
                ProcessActionJournalEntry.ModernRecheckClaimPrefix,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException("Deferred override requires a modern recheck claim.");
        }

        string encodedClaim = Convert.ToBase64String(Encoding.UTF8.GetBytes(recheckClaimIdentity))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
        string encoded = string.Join(
            '.',
            EncodedDeferredClaimPrefix.TrimEnd('.'),
            encodedClaim,
            OverrideKindToken(deferredByOverride.Kind),
            deferredByOverride.RequestedAtUtc.UtcTicks.ToString(CultureInfo.InvariantCulture),
            deferredByOverride.StartsAtUtc.UtcTicks.ToString(CultureInfo.InvariantCulture),
            deferredByOverride.EndsAtUtc.UtcTicks.ToString(CultureInfo.InvariantCulture));
        return IsIdentifier(encoded)
            ? encoded
            : throw new InvalidDataException("Encoded deferred override is too large.");
    }

    private static bool TryDecodeRecheckClaim(
        string? persisted,
        out string? recheckClaimIdentity,
        out ProcessOverrideIdentity? deferredByOverride)
    {
        recheckClaimIdentity = persisted;
        deferredByOverride = null;
        if (!IsOptionalIdentifier(persisted))
        {
            return false;
        }

        if (persisted is null
            || !persisted.StartsWith(EncodedDeferredClaimPrefix, StringComparison.Ordinal))
        {
            return true;
        }

        string[] parts = persisted.Split('.', StringSplitOptions.None);
        if (parts.Length != 6
            || parts[0] != EncodedDeferredClaimPrefix.TrimEnd('.')
            || !TryDecodeBase64Url(parts[1], out string claim)
            || !claim.StartsWith(
                ProcessActionJournalEntry.ModernRecheckClaimPrefix,
                StringComparison.Ordinal)
            || !TryParseOverrideKind(parts[2], out DesktopOverrideKind kind)
            || !long.TryParse(
                parts[3],
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out long requestedTicks)
            || !long.TryParse(
                parts[4],
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out long startsTicks)
            || !long.TryParse(
                parts[5],
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out long endsTicks))
        {
            return false;
        }

        try
        {
            OverrideDto encodedOverride = new(
                OverrideKindToken(kind),
                new DateTimeOffset(requestedTicks, TimeSpan.Zero),
                new DateTimeOffset(startsTicks, TimeSpan.Zero),
                new DateTimeOffset(endsTicks, TimeSpan.Zero));
            if (!TryFromDto(encodedOverride, out deferredByOverride))
            {
                return false;
            }
        }
        catch (ArgumentOutOfRangeException)
        {
            return false;
        }

        recheckClaimIdentity = claim;
        return true;
    }

    private static bool TryMarkModernRecheckClaim(string value, out string marked)
    {
        marked = ProcessActionJournalEntry.ModernRecheckClaimPrefix + value;
        return IsIdentifier(marked);
    }

    private static bool TryDecodeBase64Url(string value, out string decoded)
    {
        decoded = string.Empty;
        try
        {
            string padded = value.Replace('-', '+').Replace('_', '/');
            padded += new string('=', (4 - padded.Length % 4) % 4);
            decoded = Encoding.UTF8.GetString(Convert.FromBase64String(padded));
            string canonical = Convert.ToBase64String(Encoding.UTF8.GetBytes(decoded))
                .TrimEnd('=')
                .Replace('+', '-')
                .Replace('/', '_');
            return string.Equals(value, canonical, StringComparison.Ordinal)
                && IsIdentifier(decoded);
        }
        catch (Exception exception) when (exception is FormatException or DecoderFallbackException)
        {
            decoded = string.Empty;
            return false;
        }
    }

    private static bool TryFromDto(TargetDto? dto, out ProcessExactTarget? target)
    {
        target = null;
        if (dto is null
            || !TryFromDto(dto.InstanceKey, out ProcessInstanceKey key)
            || !IsUtc(dto.CreationInstantUtc)
            || dto.CreationInstantUtc.UtcTicks != key.CreationUtcTicks
            || !IsCanonicalExecutablePath(dto.ExecutablePath)
            || !IsSid(dto.UserSid)
            || dto.SessionId < 0
            || !IsIdentifier(dto.RuleId)
            || !IsUtc(dto.CutoffUtc)
            || !IsIdentifier(dto.RuleFingerprint)
            || dto.OriginalPolicyRevision < 0
            || !IsIdentifier(dto.OriginalPolicyIdentity)
            || !IsIdentifier(dto.OriginalPolicyPayloadFingerprint)
            || !IsUtc(dto.OriginalPolicyEvaluatedAtUtc))
        {
            return false;
        }

        target = new(
            key,
            dto.CreationInstantUtc,
            dto.ExecutablePath,
            dto.UserSid,
            dto.SessionId,
            dto.RuleId,
            dto.CutoffUtc,
            dto.NightDate,
            dto.RuleFingerprint,
            dto.OriginalPolicyRevision,
            dto.OriginalPolicyIdentity,
            dto.OriginalPolicyPayloadFingerprint,
            dto.OriginalPolicyEvaluatedAtUtc);
        return true;
    }

    private static bool TryFromDto(
        PolicyLedgerDto dto,
        out ProcessPolicyLedger? ledger)
    {
        ledger = null;
        if (dto.PayloadByEvaluationIdentity is null
            || dto.PayloadByEvaluationIdentity.Length > MaximumPolicyBindings
            || dto.HighestRevision < -1
            || !IsOptionalIdentifier(dto.HighestEvaluationIdentity))
        {
            return false;
        }

        ImmutableDictionary<string, ProcessPolicyPayloadBinding>.Builder bindings =
            ImmutableDictionary.CreateBuilder<string, ProcessPolicyPayloadBinding>(
                StringComparer.Ordinal);
        foreach (PolicyBindingEntryDto? item in dto.PayloadByEvaluationIdentity)
        {
            if (item is null
                || !IsIdentifier(item.EvaluationIdentity)
                || item.Revision < 0
                || item.Revision > dto.HighestRevision
                || !IsIdentifier(item.PayloadFingerprint)
                || !bindings.TryAdd(
                    item.EvaluationIdentity,
                    new(item.Revision, item.PayloadFingerprint)))
            {
                return false;
            }
        }

        if (dto.HighestRevision == -1)
        {
            if (dto.HighestEvaluationIdentity is not null || bindings.Count != 0)
            {
                return false;
            }
        }
        else if (dto.HighestEvaluationIdentity is null
            || !bindings.TryGetValue(
                dto.HighestEvaluationIdentity,
                out ProcessPolicyPayloadBinding? highest)
            || highest.Revision != dto.HighestRevision)
        {
            return false;
        }

        ledger = new(
            dto.HighestRevision,
            dto.HighestEvaluationIdentity,
            bindings.ToImmutable());
        return true;
    }

    private static bool TryFromDto(
        ObservationContinuityDto dto,
        out ProcessObservationContinuityState? continuity)
    {
        continuity = null;
        if (!IsOptionalIdentifier(dto.LastTrustedEpoch)
            || !IsOptionalIdentifier(dto.LossEpoch)
            || !IsOptionalIdentifier(dto.ClockEpoch)
            || !IsUtc(dto.SampleUtcHighWater)
            || dto.SampleMonotonicHighWater is { } monotonic && monotonic < TimeSpan.Zero)
        {
            return false;
        }

        ProcessObservationAcknowledgementCheckpoint? checkpoint = null;
        if (dto.AcknowledgementCheckpoint is { } item)
        {
            if (!TryParseAcknowledgementKind(
                    item.Kind,
                    out ProcessObservationAcknowledgementKind kind)
                || !IsIdentifier(item.ObserverEpoch)
                || item.TransitionRevision < 1)
            {
                return false;
            }

            checkpoint = new(
                kind,
                item.ObserverEpoch,
                item.TransitionRevision,
                item.Delivered);
        }

        continuity = new(
            dto.IsLost,
            dto.TrustSeverPersisted,
            dto.LastTrustedEpoch,
            dto.LossEpoch,
            dto.ClockEpoch,
            dto.SampleUtcHighWater,
            dto.SampleMonotonicHighWater,
            checkpoint);
        return true;
    }

    private static bool TryFromDto(
        SourceCheckpointDto dto,
        out ProcessSourceContinuityCheckpoint? checkpoint)
    {
        checkpoint = null;
        if (!TryParseContinuityPhase(dto.Phase, out ProcessSourceContinuityPhase phase)
            || !IsIdentifier(dto.ObserverEpoch))
        {
            return false;
        }

        ProcessSourceAcknowledgementTuple? acknowledgement = null;
        if (dto.LastAcceptedAcknowledgement is { } item)
        {
            if (!TryParseAcknowledgementKind(
                    item.Kind,
                    out ProcessObservationAcknowledgementKind kind)
                || !IsIdentifier(item.ObserverEpoch))
            {
                return false;
            }

            acknowledgement = new(kind, item.ObserverEpoch, item.TransitionRevision);
        }

        checkpoint = new(
            dto.Version,
            phase,
            dto.ObserverEpoch,
            dto.HighestAcceptedTransitionRevision,
            acknowledgement);
        return true;
    }

    private static bool IsValidEnvelope(ProcessGateEnvelope? envelope)
    {
        if (envelope is null
            || !ProcessGateCoordinator.IsValidEnvelope(envelope)
            || !ProcessGateReducer.IsValidPersistedState(envelope.ReducerState)
            || envelope.Revision == long.MaxValue
            || envelope.ActionJournal.Count > MaximumJournalEntries
            || envelope.PolicyLedger.PayloadByEvaluationIdentity.Count > MaximumPolicyBindings
            || !IsValidPolicyLedger(envelope.PolicyLedger))
        {
            return false;
        }

        return envelope.ReducerState.KnownInstances.All(pair =>
                pair.Value.Identity.Key == pair.Key
                && IsValidIdentity(pair.Value.Identity))
            && (envelope.ReducerState.TemporaryOverrideIdentity is null
                || IsValidOverride(envelope.ReducerState.TemporaryOverrideIdentity))
            && envelope.ActionJournal.Values.All(entry =>
                entry.DeferredByOverride is null
                || entry.TerminalReason == ProcessActionTerminalReason.RecheckCancelled
                && !entry.TerminationClaimed
                && entry.TerminationCompletion is null
                && entry.RecheckClaimIdentity is not null
                && entry.RecheckClaimIdentity.StartsWith(
                    ProcessActionJournalEntry.ModernRecheckClaimPrefix,
                    StringComparison.Ordinal)
                && IsValidOverride(entry.DeferredByOverride));
    }

    private static bool IsValidPolicyLedger(ProcessPolicyLedger ledger)
    {
        if (ledger.HighestRevision < -1
            || !IsOptionalIdentifier(ledger.HighestEvaluationIdentity)
            || ledger.PayloadByEvaluationIdentity is null)
        {
            return false;
        }

        if (ledger.HighestRevision == -1)
        {
            return ledger.HighestEvaluationIdentity is null
                && ledger.PayloadByEvaluationIdentity.Count == 0;
        }

        return ledger.HighestEvaluationIdentity is { } highestIdentity
            && ledger.PayloadByEvaluationIdentity.TryGetValue(
                highestIdentity,
                out ProcessPolicyPayloadBinding? highest)
            && highest.Revision == ledger.HighestRevision
            && ledger.PayloadByEvaluationIdentity.All(pair =>
                IsIdentifier(pair.Key)
                && pair.Value is not null
                && pair.Value.Revision is >= 0
                && pair.Value.Revision <= ledger.HighestRevision
                && IsIdentifier(pair.Value.PayloadFingerprint));
    }

    private static bool IsValidIdentity(ObservedProcessIdentity identity) =>
        IsUtc(identity.CreationInstantUtc)
        && identity.CreationInstantUtc.UtcTicks == identity.Key.CreationUtcTicks
        && IsCanonicalExecutablePath(identity.ExecutablePath)
        && IsSid(identity.UserSid)
        && identity.SessionId >= 0;

    private static bool IsValidOverride(ProcessOverrideIdentity value) =>
        Enum.IsDefined(value.Kind)
        && IsUtc(value.RequestedAtUtc)
        && IsUtc(value.StartsAtUtc)
        && IsUtc(value.EndsAtUtc)
        && value.RequestedAtUtc <= value.StartsAtUtc
        && value.StartsAtUtc < value.EndsAtUtc;

    private static bool IsCanonicalExecutablePath(string? value) =>
        Win32ExecutablePathCanonicalizer.TryCanonicalize(value, out string canonical)
        && string.Equals(value, canonical, StringComparison.OrdinalIgnoreCase);

    private static bool IsSid(string? value) =>
        IsIdentifier(value)
        && value!.StartsWith("S-", StringComparison.Ordinal);

    private static bool IsIdentifier(string? value) =>
        !string.IsNullOrWhiteSpace(value)
        && value.Length <= MaximumIdentifierCharacters
        && string.Equals(value, value.Trim(), StringComparison.Ordinal)
        && !value.Any(char.IsControl);

    private static bool IsOptionalIdentifier(string? value) =>
        value is null || IsIdentifier(value);

    private static bool IsUtc(DateTimeOffset value) =>
        value != default && value.Offset == TimeSpan.Zero;

    private static bool IsUtc(DateTimeOffset? value) =>
        value is null || IsUtc(value.Value);

    private static JsonSerializerOptions CreateOptions() => new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
        RespectRequiredConstructorParameters = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    private static string OverrideKindToken(DesktopOverrideKind value) => value switch
    {
        DesktopOverrideKind.TeamRescue => "teamRescue",
        DesktopOverrideKind.Emergency => "emergency",
        DesktopOverrideKind.Entertainment => "entertainment",
        _ => throw new InvalidDataException("Invalid override kind."),
    };

    private static bool TryParseOverrideKind(string? value, out DesktopOverrideKind kind)
    {
        switch (value)
        {
            case "teamRescue": kind = DesktopOverrideKind.TeamRescue; return true;
            case "emergency": kind = DesktopOverrideKind.Emergency; return true;
            case "entertainment": kind = DesktopOverrideKind.Entertainment; return true;
            default: kind = default; return false;
        }
    }

    private static string CloseOutcomeToken(ProcessCloseOutcome value) => value switch
    {
        ProcessCloseOutcome.Requested => "requested",
        ProcessCloseOutcome.NoEligibleWindow => "noEligibleWindow",
        ProcessCloseOutcome.TargetExited => "targetExited",
        ProcessCloseOutcome.IdentityMismatch => "identityMismatch",
        ProcessCloseOutcome.Ambiguous => "ambiguous",
        ProcessCloseOutcome.Unavailable => "unavailable",
        _ => throw new InvalidDataException("Invalid close outcome."),
    };

    private static bool TryParseOptionalCloseOutcome(
        string? value,
        out ProcessCloseOutcome? outcome)
    {
        outcome = value switch
        {
            null => null,
            "requested" => ProcessCloseOutcome.Requested,
            "noEligibleWindow" => ProcessCloseOutcome.NoEligibleWindow,
            "targetExited" => ProcessCloseOutcome.TargetExited,
            "identityMismatch" => ProcessCloseOutcome.IdentityMismatch,
            "ambiguous" => ProcessCloseOutcome.Ambiguous,
            "unavailable" => ProcessCloseOutcome.Unavailable,
            _ => null,
        };
        return value is null || outcome is not null;
    }

    private static string TerminationOutcomeToken(ProcessTerminationOutcome value) => value switch
    {
        ProcessTerminationOutcome.Terminated => "terminated",
        ProcessTerminationOutcome.TargetExited => "targetExited",
        ProcessTerminationOutcome.IdentityMismatch => "identityMismatch",
        ProcessTerminationOutcome.Ambiguous => "ambiguous",
        ProcessTerminationOutcome.Unavailable => "unavailable",
        _ => throw new InvalidDataException("Invalid termination outcome."),
    };

    private static bool TryParseOptionalTerminationOutcome(
        string? value,
        out ProcessTerminationOutcome? outcome)
    {
        outcome = value switch
        {
            null => null,
            "terminated" => ProcessTerminationOutcome.Terminated,
            "targetExited" => ProcessTerminationOutcome.TargetExited,
            "identityMismatch" => ProcessTerminationOutcome.IdentityMismatch,
            "ambiguous" => ProcessTerminationOutcome.Ambiguous,
            "unavailable" => ProcessTerminationOutcome.Unavailable,
            _ => null,
        };
        return value is null || outcome is not null;
    }

    private static string TerminalReasonToken(ProcessActionTerminalReason value) => value switch
    {
        ProcessActionTerminalReason.CloseTargetExited => "closeTargetExited",
        ProcessActionTerminalReason.CloseIdentityMismatch => "closeIdentityMismatch",
        ProcessActionTerminalReason.CloseAmbiguous => "closeAmbiguous",
        ProcessActionTerminalReason.CloseUnavailable => "closeUnavailable",
        ProcessActionTerminalReason.RecheckCancelled => "recheckCancelled",
        ProcessActionTerminalReason.TerminationCompleted => "terminationCompleted",
        ProcessActionTerminalReason.TerminationFailedOpen => "terminationFailedOpen",
        ProcessActionTerminalReason.Superseded => "superseded",
        _ => throw new InvalidDataException("Invalid terminal reason."),
    };

    private static bool TryParseOptionalTerminalReason(
        string? value,
        out ProcessActionTerminalReason? reason)
    {
        reason = value switch
        {
            null => null,
            "closeTargetExited" => ProcessActionTerminalReason.CloseTargetExited,
            "closeIdentityMismatch" => ProcessActionTerminalReason.CloseIdentityMismatch,
            "closeAmbiguous" => ProcessActionTerminalReason.CloseAmbiguous,
            "closeUnavailable" => ProcessActionTerminalReason.CloseUnavailable,
            "recheckCancelled" => ProcessActionTerminalReason.RecheckCancelled,
            "terminationCompleted" => ProcessActionTerminalReason.TerminationCompleted,
            "terminationFailedOpen" => ProcessActionTerminalReason.TerminationFailedOpen,
            "superseded" => ProcessActionTerminalReason.Superseded,
            _ => null,
        };
        return value is null || reason is not null;
    }

    private static string AcknowledgementKindToken(
        ProcessObservationAcknowledgementKind value) => value switch
        {
            ProcessObservationAcknowledgementKind.TrustSeverPersisted =>
                "trustSeverPersisted",
            ProcessObservationAcknowledgementKind.AuthoritativeRecoveryPersisted =>
                "authoritativeRecoveryPersisted",
            _ => throw new InvalidDataException("Invalid acknowledgement kind."),
        };

    private static bool TryParseAcknowledgementKind(
        string? value,
        out ProcessObservationAcknowledgementKind kind)
    {
        switch (value)
        {
            case "trustSeverPersisted":
                kind = ProcessObservationAcknowledgementKind.TrustSeverPersisted;
                return true;
            case "authoritativeRecoveryPersisted":
                kind = ProcessObservationAcknowledgementKind.AuthoritativeRecoveryPersisted;
                return true;
            default:
                kind = default;
                return false;
        }
    }

    private static string ContinuityPhaseToken(ProcessSourceContinuityPhase value) => value switch
    {
        ProcessSourceContinuityPhase.Dormant => "dormant",
        ProcessSourceContinuityPhase.FreshLost => "freshLost",
        ProcessSourceContinuityPhase.Lost => "lost",
        ProcessSourceContinuityPhase.Recovering => "recovering",
        ProcessSourceContinuityPhase.RecoveryCandidate => "recoveryCandidate",
        ProcessSourceContinuityPhase.Trusted => "trusted",
        _ => throw new InvalidDataException("Invalid continuity phase."),
    };

    private static bool TryParseContinuityPhase(
        string? value,
        out ProcessSourceContinuityPhase phase)
    {
        switch (value)
        {
            case "dormant": phase = ProcessSourceContinuityPhase.Dormant; return true;
            case "freshLost": phase = ProcessSourceContinuityPhase.FreshLost; return true;
            case "lost": phase = ProcessSourceContinuityPhase.Lost; return true;
            case "recovering": phase = ProcessSourceContinuityPhase.Recovering; return true;
            case "recoveryCandidate":
                phase = ProcessSourceContinuityPhase.RecoveryCandidate;
                return true;
            case "trusted": phase = ProcessSourceContinuityPhase.Trusted; return true;
            default: phase = default; return false;
        }
    }

    private sealed record EnvelopeRootDto(int SchemaVersion, EnvelopeDto Envelope);

    private sealed record ContinuityRootDto(
        int SchemaVersion,
        SourceCheckpointDto Checkpoint);

    private sealed record EnvelopeDto(
        long Revision,
        StateDto ReducerState,
        JournalEntryDto[] ActionJournal,
        PolicyLedgerDto PolicyLedger,
        ObservationContinuityDto ObservationContinuity,
        long NextJournalSequence);

    private sealed record StateDto(
        DateOnly? NightDate,
        DateTimeOffset? LastEffectiveLogicalTime,
        DateTimeOffset? CommittedWake,
        bool IsCommittedWakeLocked,
        string? RuleFingerprint,
        RuleEntryDto[] RuleStates,
        KnownEntryDto[] KnownInstances,
        EligibleEntryDto[] EligibleInstances,
        TemporaryEntryDto[] TemporaryInstances,
        KeyDto[] TaintedInstances,
        OverrideDto? TemporaryOverrideIdentity,
        OverrideDto? CapturedTeamRescueOverride,
        OverrideDto? OverrideHighWater,
        OverrideDto[] RetiredOverrideIdentities,
        string? ObserverContinuityEpoch,
        DateTimeOffset? PreOverrideBaselineObservedAtUtc,
        bool CreationTimelineTrusted,
        bool MorningReleased);

    private sealed record RuleEntryDto(string Key, RuleValueDto Value);

    private sealed record RuleValueDto(
        string RuleId,
        DateTimeOffset CutoffUtc,
        bool IsSealed);

    private sealed record KeyDto(int Pid, long CreationUtcTicks);

    private sealed record KnownEntryDto(KeyDto Key, KnownValueDto Value);

    private sealed record KnownValueDto(IdentityDto Identity, ParentDto Parent);

    private sealed record IdentityDto(
        KeyDto Key,
        DateTimeOffset CreationInstantUtc,
        string ExecutablePath,
        string UserSid,
        int SessionId);

    private sealed record ParentDto(string Kind, KeyDto? ExactParent);

    private sealed record EligibleEntryDto(KeyDto Key, string RuleId);

    private sealed record TemporaryEntryDto(KeyDto Key, TemporaryValueDto Value);

    private sealed record TemporaryValueDto(
        string RuleId,
        OverrideDto OverrideIdentity);

    private sealed record OverrideDto(
        string Kind,
        DateTimeOffset RequestedAtUtc,
        DateTimeOffset StartsAtUtc,
        DateTimeOffset EndsAtUtc);

    private sealed record JournalEntryDto(ActionKeyDto Key, JournalValueDto Value);

    private sealed record ActionKeyDto(
        KeyDto InstanceKey,
        DateOnly NightDate,
        string RuleId,
        DateTimeOffset CutoffUtc,
        string RuleFingerprint);

    private sealed record JournalValueDto(
        TargetDto Target,
        long Sequence,
        string? CloseCompletion,
        string? RecheckClaimIdentity,
        bool TerminationClaimed,
        string? TerminationCompletion,
        string? TerminalReason)
    {
        private OverrideDto? _deferredByOverride;

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public OverrideDto? DeferredByOverride
        {
            get => _deferredByOverride;
            init
            {
                _deferredByOverride = value;
                DeferredByOverrideSpecified = true;
            }
        }

        [JsonIgnore]
        public bool DeferredByOverrideSpecified { get; private set; }
    }

    private sealed record TargetDto(
        KeyDto InstanceKey,
        DateTimeOffset CreationInstantUtc,
        string ExecutablePath,
        string UserSid,
        int SessionId,
        string RuleId,
        DateTimeOffset CutoffUtc,
        DateOnly NightDate,
        string RuleFingerprint,
        long OriginalPolicyRevision,
        string OriginalPolicyIdentity,
        string OriginalPolicyPayloadFingerprint,
        DateTimeOffset OriginalPolicyEvaluatedAtUtc);

    private sealed record PolicyLedgerDto(
        long HighestRevision,
        string? HighestEvaluationIdentity,
        PolicyBindingEntryDto[] PayloadByEvaluationIdentity);

    private sealed record PolicyBindingEntryDto(
        string EvaluationIdentity,
        long Revision,
        string PayloadFingerprint);

    private sealed record ObservationContinuityDto(
        bool IsLost,
        bool TrustSeverPersisted,
        string? LastTrustedEpoch,
        string? LossEpoch,
        string? ClockEpoch,
        DateTimeOffset? SampleUtcHighWater,
        TimeSpan? SampleMonotonicHighWater,
        AcknowledgementCheckpointDto? AcknowledgementCheckpoint);

    private sealed record AcknowledgementCheckpointDto(
        string Kind,
        string ObserverEpoch,
        long TransitionRevision,
        bool Delivered);

    private sealed record SourceCheckpointDto(
        long Version,
        string Phase,
        string ObserverEpoch,
        long HighestAcceptedTransitionRevision,
        SourceAcknowledgementDto? LastAcceptedAcknowledgement);

    private sealed record SourceAcknowledgementDto(
        string Kind,
        string ObserverEpoch,
        long TransitionRevision);
}
