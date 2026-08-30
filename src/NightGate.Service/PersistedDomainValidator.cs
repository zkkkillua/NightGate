using NightGate.Core;
using System.Globalization;
using System.Text.Json;

namespace NightGate.Service;

internal static class PersistedDomainValidator
{
    private static readonly TimeSpan TeamRescueDuration = TimeSpan.FromMinutes(20);
    private static readonly TimeSpan EmergencyDuration = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan EntertainmentCoolingOff = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan EntertainmentDuration = TimeSpan.FromMinutes(20);

    public static T Validate<T>(T value)
        where T : class
    {
        switch (value)
        {
            case NightState state:
                ValidateState(state);
                break;
            case ProgressState progress:
                ValidateProgress(progress);
                break;
            case NightOutcome outcome:
                ValidateOutcome(outcome);
                break;
            case NightEvent nightEvent:
                ValidateEvent(nightEvent);
                break;
            case OnboardingState onboarding:
                ValidateOnboarding(onboarding);
                break;
            case ChromeProtectionHealth chromeProtectionHealth:
                ValidateChromeProtectionHealth(chromeProtectionHealth);
                break;
            case RuleSettingsState ruleSettings:
                ValidateRuleSettings(ruleSettings);
                break;
            case NightSelfReport selfReport:
                ValidateSelfReport(selfReport);
                break;
            case NoticeClaim noticeClaim:
                ValidateNoticeClaim(noticeClaim);
                break;
            case LegacyTaskMigrationRecord migration:
                ValidateMigration(migration);
                break;
            default:
                throw new InvalidDataException($"Unsupported persisted domain type {typeof(T).Name}.");
        }

        return value;
    }

    public static void ValidateSerializedShape<T>(string json)
        where T : class
    {
        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement root = document.RootElement;
        Type persistedType = typeof(T);
        if (persistedType == typeof(NightState))
        {
            ValidateNightStateShape(root);
            return;
        }

        if (persistedType == typeof(ProgressState))
        {
            _ = ValidateVersionedObjectShape(
                root,
                ["currentStep", "lastTeamRescueAtUtc", "lastProgressionNightDate"],
                [
                    [
                        "pendingStep",
                        "pendingStepUnlockedByNightDate",
                        "pendingStepConfirmedAtUtc",
                        "pendingStepEffectiveNightDate",
                    ],
                ]);
            return;
        }

        if (persistedType == typeof(NightOutcome))
        {
            ValidateNightOutcomeShape(root);
            return;
        }

        if (persistedType == typeof(NightEvent))
        {
            _ = ValidateObjectShape(
                root,
                ["eventId", "nightId", "occurredAtUtc", "kind", "basePhase", "overrideKind"]);
            return;
        }

        if (persistedType == typeof(OnboardingState))
        {
            _ = ValidateVersionedObjectShape(
                root,
                [
                    "wizardVersion",
                    "completedStep",
                    "chromeVerified",
                    "incognitoProtected",
                    "incognitoWarningAcknowledged",
                    "iPhoneConfirmedThroughStep",
                    "completedAtUtc",
                ],
                [
                    ["chromeDegradedAcknowledged"],
                ]);
            return;
        }

        if (persistedType == typeof(ChromeProtectionHealth))
        {
            _ = ValidateVersionedObjectShape(
                root,
                [
                    "extensionId",
                    "extensionVersion",
                    "profileTokenSha256",
                    "policyRevision",
                    "incognitoAllowed",
                    "observedAtUtc",
                ],
                [
                    ["protectionReady"],
                ]);
            return;
        }

        if (persistedType == typeof(RuleSettingsState))
        {
            ValidateRuleSettingsShape(root);
            return;
        }

        if (persistedType == typeof(NightSelfReport))
        {
            _ = ValidateObjectShape(
                root,
                ["nightDate", "phoneOutOfReach", "wakeWithinWindow", "updatedAtUtc"]);
            return;
        }

        if (persistedType == typeof(NoticeClaim))
        {
            _ = ValidateObjectShape(root, ["nightDate", "kind", "claimedAtUtc"]);
            return;
        }

        if (persistedType == typeof(LegacyTaskMigrationRecord))
        {
            _ = ValidateVersionedObjectShape(
                root,
                [
                    "migrationId",
                    "taskPath",
                    "actionFingerprint",
                    "originalEnabled",
                    "status",
                    "preparedAtUtc",
                    "completedAtUtc",
                ],
                [
                    ["disabledStateVerified"],
                ]);
            return;
        }

        throw new InvalidDataException($"Unsupported persisted domain type {persistedType.Name}.");
    }

    private static void ValidateNightStateShape(JsonElement root)
    {
        Dictionary<string, JsonElement> properties = ValidateVersionedObjectShape(
            root,
            [
                "nightId",
                "nightDate",
                "lastObservedUtc",
                "highestBasePhaseReached",
                "activeOverride",
                "emergencyUsed",
                "teamRescueUsed",
                "entertainmentUsed",
                "deliberateBypass",
                "lateNewEntertainment",
                "missedLock",
                "isClosed",
            ],
            [
                ["lastObservedUptime"],
                ["lastObservedBootSessionId"],
                ["overrideReasons", "firstLockObservedAtUtc"],
                ["scheduledLockAtUtc"],
                ["protectionGapObserved"],
                ["scheduleTimeZoneSerialized"],
            ]);
        if (properties["activeOverride"].ValueKind != JsonValueKind.Null)
        {
            ValidateActiveOverrideShape(properties["activeOverride"]);
        }

        if (properties.TryGetValue("overrideReasons", out JsonElement reasons))
        {
            ValidateOverrideReasonsShape(reasons);
        }

        if (properties.ContainsKey("protectionGapObserved"))
        {
            _ = ReadBoolean(properties, "protectionGapObserved");
        }
    }

    private static void ValidateNightOutcomeShape(JsonElement root)
    {
        Dictionary<string, JsonElement> properties = ValidateVersionedObjectShape(
            root,
            [
                "nightId",
                "nightDate",
                "closedAtUtc",
                "emergencyUsed",
                "teamRescueUsed",
                "entertainmentUsed",
                "deliberateBypass",
                "lateNewEntertainment",
                "missedLock",
                "isWorkNight",
                "isEligible",
                "qualifies",
            ],
            [
                ["overrideReasons", "firstLockObservedAtUtc"],
                ["scheduledLockAtUtc"],
                ["protectionGapObserved"],
                ["scheduleTimeZoneSerialized"],
            ]);
        if (properties.TryGetValue("overrideReasons", out JsonElement reasons))
        {
            ValidateOverrideReasonsShape(reasons);
        }

        ValidateNightOutcomeComputedFacts(properties);
    }

    private static void ValidateRuleSettingsShape(JsonElement root)
    {
        Dictionary<string, JsonElement> properties = ValidateObjectShape(
            root,
            [
                "activeAppRules",
                "activeSiteRules",
                "pendingAppRules",
                "pendingSiteRules",
                "pendingEffectiveNightDate",
                "pendingSavedAtUtc",
            ]);
        ValidateAppRuleArray(properties["activeAppRules"], allowNull: false);
        ValidateSiteRuleArray(properties["activeSiteRules"], allowNull: false);
        ValidateAppRuleArray(properties["pendingAppRules"], allowNull: true);
        ValidateSiteRuleArray(properties["pendingSiteRules"], allowNull: true);
    }

    private static void ValidateAppRuleArray(JsonElement value, bool allowNull)
    {
        if (allowNull && value.ValueKind == JsonValueKind.Null)
        {
            return;
        }

        if (value.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException("Persisted app rules must be an array or allowed null.");
        }

        foreach (JsonElement item in value.EnumerateArray())
        {
            Dictionary<string, JsonElement> properties = ValidateObjectShape(
                item,
                [
                    "id",
                    "rootExecutablePath",
                    "helperExecutablePaths",
                    "category",
                    "sessionMinutes",
                    "isConfigured",
                ]);
            if (properties["helperExecutablePaths"].ValueKind != JsonValueKind.Array)
            {
                throw new InvalidDataException(
                    "Persisted helper executable paths must be an array.");
            }

            if (properties["isConfigured"].ValueKind != JsonValueKind.True)
            {
                throw new InvalidDataException(
                    "Persisted configured app rules must declare isConfigured as boolean true.");
            }
        }
    }

    private static void ValidateSiteRuleArray(JsonElement value, bool allowNull)
    {
        if (allowNull && value.ValueKind == JsonValueKind.Null)
        {
            return;
        }

        if (value.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException("Persisted site rules must be an array or allowed null.");
        }

        foreach (JsonElement item in value.EnumerateArray())
        {
            _ = ValidateObjectShape(item, ["domain"]);
        }
    }

    private static void ValidateActiveOverrideShape(JsonElement value) =>
        _ = ValidateObjectShape(
            value,
            ["kind", "requestedAtUtc", "startsAtUtc", "endsAtUtc", "allowedProcessIdentifiers"]);

    private static void ValidateOverrideReasonsShape(JsonElement value) =>
        _ = ValidateObjectShape(
            value,
            [
                "teamRescueCount",
                "entertainmentCount",
                "emergencyHealthCount",
                "emergencySafetyCount",
                "emergencyUrgentWorkCount",
                "emergencyOtherCount",
            ]);

    private static void ValidateNightOutcomeComputedFacts(
        IReadOnlyDictionary<string, JsonElement> properties)
    {
        if (properties["nightDate"].ValueKind != JsonValueKind.String
            || !DateOnly.TryParseExact(
                properties["nightDate"].GetString(),
                "yyyy-MM-dd",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out DateOnly nightDate))
        {
            throw new InvalidDataException("Persisted NightOutcome has an invalid night date.");
        }

        bool isWorkNight = nightDate.DayOfWeek is >= DayOfWeek.Sunday and <= DayOfWeek.Thursday;
        bool isEligible = isWorkNight && !ReadBoolean(properties, "emergencyUsed");
        bool qualifies = isEligible
            && !ReadBoolean(properties, "teamRescueUsed")
            && !ReadBoolean(properties, "entertainmentUsed")
            && !ReadBoolean(properties, "deliberateBypass")
            && !ReadBoolean(properties, "lateNewEntertainment")
            && !ReadBoolean(properties, "missedLock")
            && (!properties.ContainsKey("protectionGapObserved")
                || !ReadBoolean(properties, "protectionGapObserved"));
        if (properties.ContainsKey("scheduledLockAtUtc"))
        {
            DateTimeOffset? firstLockObservedAtUtc = ReadNullableTimestamp(
                properties,
                "firstLockObservedAtUtc");
            DateTimeOffset? scheduledLockAtUtc = ReadNullableTimestamp(
                properties,
                "scheduledLockAtUtc");
            qualifies = qualifies
                && firstLockObservedAtUtc is not null
                && scheduledLockAtUtc is not null
                && firstLockObservedAtUtc <= scheduledLockAtUtc;
        }
        if (ReadBoolean(properties, "isWorkNight") != isWorkNight
            || ReadBoolean(properties, "isEligible") != isEligible
            || ReadBoolean(properties, "qualifies") != qualifies)
        {
            throw new InvalidDataException(
                "Persisted NightOutcome computed facts do not match its domain fields.");
        }
    }

    private static DateTimeOffset? ReadNullableTimestamp(
        IReadOnlyDictionary<string, JsonElement> properties,
        string propertyName)
    {
        JsonElement value = properties[propertyName];
        if (value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (value.ValueKind != JsonValueKind.String
            || !value.TryGetDateTimeOffset(out DateTimeOffset timestamp))
        {
            throw new InvalidDataException(
                $"Persisted property '{propertyName}' must be a timestamp or null.");
        }

        return timestamp;
    }

    private static bool ReadBoolean(
        IReadOnlyDictionary<string, JsonElement> properties,
        string propertyName)
    {
        JsonElement value = properties[propertyName];
        if (value.ValueKind is not JsonValueKind.True and not JsonValueKind.False)
        {
            throw new InvalidDataException(
                $"Persisted property '{propertyName}' must be a JSON boolean.");
        }

        return value.GetBoolean();
    }

    private static Dictionary<string, JsonElement> ValidateVersionedObjectShape(
        JsonElement value,
        IReadOnlyCollection<string> baseProperties,
        IReadOnlyList<IReadOnlyCollection<string>> appendedVersionGroups)
    {
        HashSet<string> allowed = new(baseProperties, StringComparer.Ordinal);
        foreach (IReadOnlyCollection<string> group in appendedVersionGroups)
        {
            allowed.UnionWith(group);
        }

        Dictionary<string, JsonElement> observed = ReadObjectProperties(value, allowed);
        if (baseProperties.Any(property => !observed.ContainsKey(property)))
        {
            throw new InvalidDataException("Persisted domain JSON is missing a base property.");
        }

        bool previousVersionPresent = true;
        foreach (IReadOnlyCollection<string> group in appendedVersionGroups)
        {
            int presentCount = group.Count(observed.ContainsKey);
            bool completeGroup = presentCount == group.Count;
            if ((presentCount != 0 && !completeGroup)
                || (completeGroup && !previousVersionPresent))
            {
                throw new InvalidDataException(
                    "Persisted domain JSON mixes fields from incompatible serialized versions.");
            }

            previousVersionPresent = completeGroup;
        }

        return observed;
    }

    private static Dictionary<string, JsonElement> ValidateObjectShape(
        JsonElement value,
        IReadOnlyCollection<string> requiredProperties)
    {
        HashSet<string> allowed = new(requiredProperties, StringComparer.Ordinal);
        Dictionary<string, JsonElement> observed = ReadObjectProperties(value, allowed);
        if (requiredProperties.Any(property => !observed.ContainsKey(property)))
        {
            throw new InvalidDataException("Persisted domain JSON is missing a required property.");
        }

        return observed;
    }

    private static Dictionary<string, JsonElement> ReadObjectProperties(
        JsonElement value,
        IReadOnlySet<string> allowedProperties)
    {
        if (value.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException("Persisted domain JSON must be an object.");
        }

        Dictionary<string, JsonElement> observed = new(StringComparer.Ordinal);
        foreach (JsonProperty property in value.EnumerateObject())
        {
            if (!allowedProperties.Contains(property.Name)
                || !observed.TryAdd(property.Name, property.Value))
            {
                throw new InvalidDataException(
                    $"Persisted domain JSON contains unknown, noncanonical, or duplicate property '{property.Name}'.");
            }
        }

        return observed;
    }

    private static void ValidateState(NightState state)
    {
        Require(state.NightId != Guid.Empty, "NightState requires a nonempty night ID.");
        Require(state.NightDate != default, "NightState requires a night date.");
        ValidateUtc(state.LastObservedUtc, "NightState last-observed time");
        Require(
            state.LastObservedUptime is null || state.LastObservedUptime >= TimeSpan.Zero,
            "NightState last-observed uptime cannot be negative.");
        Require(
            state.LastObservedBootSessionId is null
                || state.LastObservedBootSessionId != Guid.Empty,
            "NightState boot session ID cannot be empty.");
        Require(
            state.LastObservedBootSessionId is null || state.LastObservedUptime is not null,
            "NightState boot session ID requires an uptime anchor.");
        Require(IsBasePhase(state.HighestBasePhaseReached), "NightState contains an invalid base phase.");
        Require(!state.IsClosed || state.ActiveOverride is null, "A closed NightState cannot retain an override.");
        ValidateOverrideReasons(state.OverrideReasons, "NightState");
        if (state.FirstLockObservedAtUtc is { } firstLockObservedAtUtc)
        {
            ValidateUtc(firstLockObservedAtUtc, "NightState first-lock observation");
            Require(
                firstLockObservedAtUtc <= state.LastObservedUtc,
                "NightState first-lock observation cannot follow its last observation.");
        }

        if (state.ScheduledLockAtUtc is { } scheduledLockAtUtc)
        {
            ValidateUtc(scheduledLockAtUtc, "NightState scheduled-lock deadline");
        }

        if (state.ScheduleTimeZoneSerialized is { } scheduleTimeZoneSerialized)
        {
            ValidateScheduleTimeZone(scheduleTimeZoneSerialized, "NightState");
        }

        if (state.ActiveOverride is { } activeOverride)
        {
            ValidateOverride(activeOverride);
            Require(
                activeOverride.RequestedAtUtc <= state.LastObservedUtc
                    && state.LastObservedUtc < activeOverride.EndsAtUtc,
                "NightState observation time is inconsistent with its active override.");
            bool usageRecorded = activeOverride.Kind switch
            {
                OverrideKind.TeamRescue => state.TeamRescueUsed,
                OverrideKind.Emergency => state.EmergencyUsed,
                OverrideKind.Entertainment => state.EntertainmentUsed,
                _ => false,
            };
            Require(usageRecorded, "An active override requires its persisted usage flag.");
        }
    }

    private static void ValidateOverride(ActiveOverride activeOverride)
    {
        Require(Enum.IsDefined(activeOverride.Kind), "ActiveOverride contains an invalid kind.");
        ValidateUtc(activeOverride.RequestedAtUtc, "ActiveOverride request time");
        ValidateUtc(activeOverride.StartsAtUtc, "ActiveOverride start time");
        ValidateUtc(activeOverride.EndsAtUtc, "ActiveOverride end time");
        Require(
            activeOverride.RequestedAtUtc <= activeOverride.StartsAtUtc
                && activeOverride.StartsAtUtc < activeOverride.EndsAtUtc,
            "ActiveOverride times are out of order.");
        Require(
            !activeOverride.AllowedProcessIdentifiers.IsDefault,
            "ActiveOverride requires an initialized process snapshot.");

        HashSet<string> identifiers = new(StringComparer.OrdinalIgnoreCase);
        foreach (string identifier in activeOverride.AllowedProcessIdentifiers)
        {
            Require(
                !string.IsNullOrWhiteSpace(identifier)
                    && string.Equals(identifier, identifier.Trim(), StringComparison.Ordinal)
                    && identifiers.Add(identifier),
                "ActiveOverride contains an invalid process identifier.");
        }

        switch (activeOverride.Kind)
        {
            case OverrideKind.TeamRescue:
                Require(
                    activeOverride.StartsAtUtc == activeOverride.RequestedAtUtc
                        && activeOverride.EndsAtUtc - activeOverride.StartsAtUtc == TeamRescueDuration,
                    "Team rescue override timing is invalid.");
                break;
            case OverrideKind.Emergency:
                Require(
                    activeOverride.StartsAtUtc == activeOverride.RequestedAtUtc
                        && activeOverride.EndsAtUtc - activeOverride.StartsAtUtc == EmergencyDuration
                        && activeOverride.AllowedProcessIdentifiers.IsEmpty,
                    "Emergency override timing or process snapshot is invalid.");
                break;
            case OverrideKind.Entertainment:
                Require(
                    activeOverride.StartsAtUtc - activeOverride.RequestedAtUtc == EntertainmentCoolingOff
                        && activeOverride.EndsAtUtc - activeOverride.StartsAtUtc == EntertainmentDuration
                        && activeOverride.AllowedProcessIdentifiers.IsEmpty,
                    "Entertainment override timing or process snapshot is invalid.");
                break;
            default:
                throw new InvalidDataException("ActiveOverride contains an invalid kind.");
        }
    }

    private static void ValidateProgress(ProgressState progress)
    {
        Require(progress.CurrentStep is >= 1 and <= 4, "ProgressState step is outside 1 through 4.");
        if (progress.LastTeamRescueAtUtc is { } lastTeamRescueAtUtc)
        {
            ValidateUtc(lastTeamRescueAtUtc, "ProgressState team-rescue time");
        }

        if (progress.LastProgressionNightDate is { } lastProgressionNightDate)
        {
            Require(lastProgressionNightDate != default, "ProgressState progression date is invalid.");
        }

        if (progress.PendingStepUnlockedByNightDate is { } pendingUnlockNight)
        {
            Require(pendingUnlockNight != default, "ProgressState pending unlock date is invalid.");
        }

        if (progress.PendingStepEffectiveNightDate is { } pendingEffectiveNight)
        {
            Require(pendingEffectiveNight != default, "ProgressState pending effective date is invalid.");
        }

        if (progress.PendingStep is null)
        {
            Require(
                progress.PendingStepUnlockedByNightDate is null
                    && progress.PendingStepConfirmedAtUtc is null
                    && progress.PendingStepEffectiveNightDate is null,
                "ProgressState contains partial pending data.");
            return;
        }

        Require(
            progress.CurrentStep < 4
                && progress.PendingStep == progress.CurrentStep + 1
                && progress.PendingStepUnlockedByNightDate is { } unlockedBy
                && progress.LastProgressionNightDate is { } lastEvaluated
                && lastEvaluated >= unlockedBy,
            "ProgressState contains an impossible pending step.");
        Require(
            (progress.PendingStepConfirmedAtUtc is null)
                == (progress.PendingStepEffectiveNightDate is null),
            "ProgressState pending confirmation fields must be paired.");
        if (progress.PendingStepConfirmedAtUtc is { } confirmedAt)
        {
            ValidateUtc(confirmedAt, "ProgressState pending confirmation time");
        }

        if (progress.PendingStepEffectiveNightDate is { } effectiveNight)
        {
            Require(
                effectiveNight >= progress.PendingStepUnlockedByNightDate!.Value,
                "ProgressState pending effective date predates its unlock.");
        }
    }

    private static void ValidateOutcome(NightOutcome outcome)
    {
        Require(outcome.NightId != Guid.Empty, "NightOutcome requires a nonempty night ID.");
        Require(outcome.NightDate != default, "NightOutcome requires a night date.");
        ValidateUtc(outcome.ClosedAtUtc, "NightOutcome close time");
        ValidateOverrideReasons(outcome.OverrideReasons, "NightOutcome");
        if (outcome.FirstLockObservedAtUtc is { } firstLockObservedAtUtc)
        {
            ValidateUtc(firstLockObservedAtUtc, "NightOutcome first-lock observation");
            Require(
                firstLockObservedAtUtc <= outcome.ClosedAtUtc,
                "NightOutcome first-lock observation cannot follow its close time.");
        }
        if (outcome.ScheduledLockAtUtc is { } scheduledLockAtUtc)
        {
            ValidateUtc(scheduledLockAtUtc, "NightOutcome scheduled-lock deadline");
            Require(
                scheduledLockAtUtc <= outcome.ClosedAtUtc,
                "NightOutcome scheduled-lock deadline cannot follow its close time.");
        }
        if (outcome.ScheduleTimeZoneSerialized is { } scheduleTimeZoneSerialized)
        {
            ValidateScheduleTimeZone(scheduleTimeZoneSerialized, "NightOutcome");
        }
    }

    private static void ValidateScheduleTimeZone(string serialized, string owner)
    {
        try
        {
            _ = NightScheduleTimeZone.Restore(serialized);
        }
        catch (Exception exception) when (exception is ArgumentException
            or InvalidTimeZoneException
            or System.Runtime.Serialization.SerializationException)
        {
            throw new InvalidDataException(
                $"{owner} contains an invalid persisted schedule time zone.",
                exception);
        }
    }

    private static void ValidateOverrideReasons(
        OverrideReasonSummary? reasons,
        string owner)
    {
        Require(reasons is not null, $"{owner} override reasons cannot be null.");
        int[] counts =
        [
            reasons!.TeamRescueCount,
            reasons.EntertainmentCount,
            reasons.EmergencyHealthCount,
            reasons.EmergencySafetyCount,
            reasons.EmergencyUrgentWorkCount,
            reasons.EmergencyOtherCount,
        ];
        Require(
            counts.All(count => count is >= 0 and <= OverrideReasonSummary.MaximumCount),
            $"{owner} override-reason count is outside its supported range.");
    }

    private static void ValidateEvent(NightEvent nightEvent)
    {
        Require(nightEvent.EventId != Guid.Empty, "NightEvent requires a nonempty event ID.");
        Require(
            nightEvent.NightId is null || nightEvent.NightId.Value != Guid.Empty,
            "NightEvent contains an empty night ID.");
        ValidateUtc(nightEvent.OccurredAtUtc, "NightEvent occurrence time");
        Require(Enum.IsDefined(nightEvent.Kind), "NightEvent contains an invalid kind.");
        Require(
            nightEvent.BasePhase is null || IsBasePhase(nightEvent.BasePhase.Value),
            "NightEvent contains an invalid base phase.");
        Require(
            nightEvent.OverrideKind is null || Enum.IsDefined(nightEvent.OverrideKind.Value),
            "NightEvent contains an invalid override kind.");
    }

    private static void ValidateOnboarding(OnboardingState state)
    {
        _ = new OnboardingState(
            state.CompletedStep,
            state.ChromeVerified,
            state.IncognitoProtected,
            state.IncognitoWarningAcknowledged,
            state.IPhoneConfirmedThroughStep,
            state.CompletedAtUtc,
            state.WizardVersion,
            state.ChromeDegradedAcknowledged);
    }

    private static void ValidateChromeProtectionHealth(ChromeProtectionHealth health)
    {
        _ = new ChromeProtectionHealth(
            health.ExtensionId,
            health.ExtensionVersion,
            health.ProfileTokenSha256,
            health.PolicyRevision,
            health.IncognitoAllowed,
            health.ObservedAtUtc,
            health.ProtectionReady);
    }

    private static void ValidateRuleSettings(RuleSettingsState state)
    {
        _ = new RuleSettingsState(
            state.ActiveAppRules,
            state.ActiveSiteRules,
            state.PendingAppRules,
            state.PendingSiteRules,
            state.PendingEffectiveNightDate,
            state.PendingSavedAtUtc);
    }

    private static void ValidateSelfReport(NightSelfReport report)
    {
        _ = new NightSelfReport(
            report.NightDate,
            report.PhoneOutOfReach,
            report.WakeWithinWindow,
            report.UpdatedAtUtc);
    }

    private static void ValidateNoticeClaim(NoticeClaim claim)
    {
        _ = new NoticeClaim(claim.NightDate, claim.Kind, claim.ClaimedAtUtc);
    }

    private static void ValidateMigration(LegacyTaskMigrationRecord migration)
    {
        _ = new LegacyTaskMigrationRecord(
            migration.MigrationId,
            migration.TaskPath,
            migration.ActionFingerprint,
            migration.OriginalEnabled,
            migration.Status,
            migration.PreparedAtUtc,
            migration.CompletedAtUtc,
            migration.DisabledStateVerified);
    }

    private static bool IsBasePhase(NightPhase phase) => phase is
        NightPhase.Free or
        NightPhase.LastStart or
        NightPhase.Grace or
        NightPhase.LandingLocked or
        NightPhase.Morning;

    private static void ValidateUtc(DateTimeOffset value, string fieldName) => Require(
        value != default && value.Offset == TimeSpan.Zero,
        $"{fieldName} must be a nondefault UTC timestamp.");

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidDataException(message);
        }
    }
}
