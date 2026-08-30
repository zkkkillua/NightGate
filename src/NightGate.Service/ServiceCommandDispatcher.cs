using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using NightGate.Core;
using NightGate.Protocol;

namespace NightGate.Service;

public interface IProtocolCommandHandler
{
    ValueTask<ProtocolCommandResult> ExecuteAsync(
        ServiceCommand command,
        CancellationToken cancellationToken = default);
}

public sealed record ProtocolCommandResult(StorageMode Mode, JsonElement Payload)
{
    public static ProtocolCommandResult Success<T>(T payload) => new(
        StorageMode.Success,
        JsonSerializer.SerializeToElement(payload, ProtocolJson.Options));

    public static ProtocolCommandResult Degraded<T>(T payload) => new(
        StorageMode.Degraded,
        JsonSerializer.SerializeToElement(payload, ProtocolJson.Options));
}

public sealed record ProtocolDispatchResult(
    ReadOnlyMemory<byte> ResponseUtf8,
    bool CommandExecuted,
    bool IsDegraded = false);

public sealed class ServiceCommandDispatcher(
    JsonProtocolCodec codec,
    IProtocolCommandHandler handler,
    IClock? clock = null)
{
    private const int ProtocolVersion = NightGateProtocol.Version;
    private const long MaximumJavaScriptSafeInteger = 9_007_199_254_740_991;

    public async ValueTask<ProtocolDispatchResult> DispatchAsync(
        ReadOnlyMemory<byte> requestUtf8,
        CancellationToken cancellationToken = default)
    {
        ProtocolDecodeResult decoded = codec.Decode(requestUtf8);
        if (decoded.Status == ProtocolDecodeStatus.MessageTooLarge)
        {
            return Error(string.Empty, "messageTooLarge");
        }

        if (decoded.Status != ProtocolDecodeStatus.Success || decoded.Envelope is null)
        {
            return Error(decoded.RequestId, "malformedMessage");
        }

        ProtocolRequestEnvelope envelope = decoded.Envelope;
        if (envelope.Version != ProtocolVersion)
        {
            return Error(envelope.RequestId, "unsupportedVersion");
        }

        if (!IsAllowedType(envelope.Type))
        {
            return Error(envelope.RequestId, "unknownType");
        }

        if (!TryCreateCommand(envelope.Type, envelope.Payload, out ServiceCommand? command))
        {
            return Error(envelope.RequestId, "malformedPayload");
        }

        try
        {
            ProtocolCommandResult result = await handler
                .ExecuteAsync(command!, cancellationToken)
                .ConfigureAwait(false);
            JsonElement payload = JsonSerializer.SerializeToElement(
                new
                {
                    status = result.Mode == StorageMode.Success ? "success" : "degraded",
                    data = result.Payload,
                },
                ProtocolJson.Options);
            return new(
                Serialize(new(ProtocolVersion, $"{envelope.Type}Result", envelope.RequestId, payload)),
                true,
                result.Mode == StorageMode.Degraded);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return Error(envelope.RequestId, "commandFailed", true);
        }
    }

    private static bool IsAllowedType(string type) => type is
        "getPolicy" or
        "getDesktopPolicy" or
        "endDesktopSession" or
        "getStatus" or
        "getUserState" or
        "requestOverride" or
        "recordEvent" or
        "confirmIPhoneStep" or
        "completeOnboardingStep" or
        "saveNightSelfReport" or
        "saveRuleSettings" or
        "claimDueNotice" or
        "recordBrowserEvent" or
        "recordChromeHealth" or
        "listLegacyTaskMigrations" or
        "findLegacyTaskMigrationRecoveryCandidate" or
        "prepareLegacyTaskMigration" or
        "completeLegacyTaskMigration" or
        "recoverLegacyTaskMigrationDisabled" or
        "clearHistory" or
        "loadProcessPersistence" or
        "compareExchangeProcessPersistence";

    private bool TryCreateCommand(
        string type,
        JsonElement payload,
        out ServiceCommand? command)
    {
        command = null;
        if (payload.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        try
        {
            command = type switch
            {
                "getPolicy" when IsEmptyObject(payload) => new GetPolicyCommand(),
                "getDesktopPolicy" => CreateGetDesktopPolicyCommand(payload),
                "endDesktopSession" => CreateEndDesktopSessionCommand(payload),
                "getStatus" when IsEmptyObject(payload) => new GetStatusCommand(),
                "getUserState" when IsEmptyObject(payload) => new GetUserStateCommand(),
                "claimDueNotice" when IsEmptyObject(payload) => new ClaimDueNoticeCommand(),
                "clearHistory" when IsEmptyObject(payload) => new ClearHistoryCommand(),
                "listLegacyTaskMigrations" =>
                    CreateListLegacyTaskMigrationsCommand(payload),
                "findLegacyTaskMigrationRecoveryCandidate" =>
                    CreateFindLegacyTaskMigrationRecoveryCandidateCommand(payload),
                "requestOverride" => CreateOverrideCommand(payload),
                "recordEvent" => CreateRecordEventCommand(payload),
                "confirmIPhoneStep" => CreateConfirmIPhoneStepCommand(payload),
                "completeOnboardingStep" => CreateCompleteOnboardingStepCommand(payload),
                "saveNightSelfReport" => CreateSaveNightSelfReportCommand(payload),
                "saveRuleSettings" => CreateSaveRuleSettingsCommand(payload),
                "recordBrowserEvent" => CreateRecordBrowserEventCommand(payload),
                "recordChromeHealth" => CreateRecordChromeHealthCommand(payload),
                "prepareLegacyTaskMigration" =>
                    CreatePrepareLegacyTaskMigrationCommand(payload),
                "completeLegacyTaskMigration" =>
                    CreateCompleteLegacyTaskMigrationCommand(payload),
                "recoverLegacyTaskMigrationDisabled" =>
                    CreateRecoverLegacyTaskMigrationDisabledCommand(payload),
                "loadProcessPersistence" => CreateLoadProcessPersistenceCommand(payload),
                "compareExchangeProcessPersistence" =>
                    CreateCompareExchangeProcessPersistenceCommand(payload),
                _ => null,
            };
            return command is not null;
        }
        catch (Exception exception) when (exception is JsonException or ArgumentException)
        {
            return false;
        }
    }

    private static bool IsEmptyObject(JsonElement payload) =>
        !payload.EnumerateObject().Any();

    private static GetDesktopPolicyCommand? CreateGetDesktopPolicyCommand(
        JsonElement payload) =>
        TryReadSessionId(payload, out string sessionId)
            ? new(sessionId)
            : null;

    private static EndDesktopSessionCommand? CreateEndDesktopSessionCommand(
        JsonElement payload) =>
        TryReadSessionId(payload, out string sessionId)
            ? new(sessionId)
            : null;

    private static bool TryReadSessionId(JsonElement payload, out string sessionId)
    {
        sessionId = string.Empty;
        if (!HasOnlyProperties(payload, "sessionId")
            || payload.EnumerateObject().Count() != 1
            || !payload.TryGetProperty("sessionId", out JsonElement sessionIdElement)
            || sessionIdElement.ValueKind != JsonValueKind.String
            || sessionIdElement.GetString() is not { } value
            || !DesktopSessionLease.IsValidSessionId(value))
        {
            return false;
        }

        sessionId = value;
        return true;
    }

    private static RequestOverrideCommand? CreateOverrideCommand(JsonElement payload)
    {
        if (!HasOnlyProperties(payload, "kind", "emergencyReason")
            || !payload.TryGetProperty("kind", out JsonElement kindElement)
            || !TryParseOverrideKind(kindElement, out OverrideKind kind))
        {
            return null;
        }

        EmergencyReason? emergencyReason = null;
        if (payload.TryGetProperty("emergencyReason", out JsonElement reasonElement))
        {
            if (!TryParseEmergencyReason(reasonElement, out EmergencyReason reason))
            {
                return null;
            }

            emergencyReason = reason;
        }

        return new(new(kind, emergencyReason));
    }

    private static RecordEventCommand? CreateRecordEventCommand(JsonElement payload)
    {
        if (!HasOnlyProperties(payload, "kind", "basePhase", "overrideKind")
            || !payload.TryGetProperty("kind", out JsonElement kindElement)
            || !TryParseNightEventKind(kindElement, out NightEventKind kind))
        {
            return null;
        }

        NightPhase? basePhase = null;
        if (payload.TryGetProperty("basePhase", out JsonElement phaseElement))
        {
            if (!TryParseNightPhase(phaseElement, out NightPhase phase))
            {
                return null;
            }

            basePhase = phase;
        }

        OverrideKind? overrideKind = null;
        if (payload.TryGetProperty("overrideKind", out JsonElement overrideElement))
        {
            if (!TryParseOverrideKind(overrideElement, out OverrideKind parsedOverrideKind))
            {
                return null;
            }

            overrideKind = parsedOverrideKind;
        }

        return new(kind, basePhase, overrideKind);
    }

    private static ConfirmIPhoneStepCommand? CreateConfirmIPhoneStepCommand(
        JsonElement payload)
    {
        if (!HasOnlyProperties(payload, "step", "checklist")
            || payload.EnumerateObject().Count() != 2
            || !payload.TryGetProperty("step", out JsonElement stepElement)
            || stepElement.ValueKind != JsonValueKind.Number
            || !stepElement.TryGetInt32(out int step)
            || step is < 2 or > 4
            || !payload.TryGetProperty("checklist", out JsonElement checklist)
            || checklist.ValueKind != JsonValueKind.Object
            || !HasOnlyProperties(
                checklist,
                "healthSleepScheduleConfigured",
                "sleepFocusConfigured",
                "downtimeConfigured",
                "blockAtDowntimeEnabled",
                "entertainmentCategoriesRestricted",
                "requiredAppsAllowed",
                "safariNotAllowlisted",
                "distinctRecoverableScreenTimePasscodeAcknowledged",
                "oldAlarmsChecked",
                "phonePlacementPlanned")
            || checklist.EnumerateObject().Count() != 10
            || !TryReadRequiredBoolean(
                checklist,
                "healthSleepScheduleConfigured",
                out bool healthSleepScheduleConfigured)
            || !TryReadRequiredBoolean(
                checklist,
                "sleepFocusConfigured",
                out bool sleepFocusConfigured)
            || !TryReadRequiredBoolean(
                checklist,
                "downtimeConfigured",
                out bool downtimeConfigured)
            || !TryReadRequiredBoolean(
                checklist,
                "blockAtDowntimeEnabled",
                out bool blockAtDowntimeEnabled)
            || !TryReadRequiredBoolean(
                checklist,
                "entertainmentCategoriesRestricted",
                out bool entertainmentCategoriesRestricted)
            || !TryReadRequiredBoolean(
                checklist,
                "requiredAppsAllowed",
                out bool requiredAppsAllowed)
            || !TryReadRequiredBoolean(
                checklist,
                "safariNotAllowlisted",
                out bool safariNotAllowlisted)
            || !TryReadRequiredBoolean(
                checklist,
                "distinctRecoverableScreenTimePasscodeAcknowledged",
                out bool distinctRecoverableScreenTimePasscodeAcknowledged)
            || !TryReadRequiredBoolean(
                checklist,
                "oldAlarmsChecked",
                out bool oldAlarmsChecked)
            || !TryReadRequiredBoolean(
                checklist,
                "phonePlacementPlanned",
                out bool phonePlacementPlanned))
        {
            return null;
        }

        return new(
            step,
            new(
                healthSleepScheduleConfigured,
                sleepFocusConfigured,
                downtimeConfigured,
                blockAtDowntimeEnabled,
                requiredAppsAllowed,
                safariNotAllowlisted,
                distinctRecoverableScreenTimePasscodeAcknowledged,
                oldAlarmsChecked,
                phonePlacementPlanned,
                entertainmentCategoriesRestricted));
    }

    private static bool TryReadRequiredBoolean(
        JsonElement payload,
        string name,
        out bool value)
    {
        value = false;
        if (!payload.TryGetProperty(name, out JsonElement element)
            || element.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            return false;
        }

        value = element.GetBoolean();
        return true;
    }

    private static CompleteOnboardingStepCommand? CreateCompleteOnboardingStepCommand(
        JsonElement payload)
    {
        if (!HasOnlyProperties(
                payload,
                "step",
                "chromeVerified",
                "incognitoProtected",
                "incognitoWarningAcknowledged",
                "iPhoneConfirmedThroughStep",
                "chromeDegradedAcknowledged")
            || payload.EnumerateObject().Count() != 6
            || !payload.TryGetProperty("step", out JsonElement stepElement)
            || stepElement.ValueKind != JsonValueKind.Number
            || !stepElement.TryGetInt32(out int step)
            || step is < 1 or > 5
            || !TryReadRequiredBoolean(payload, "chromeVerified", out bool chromeVerified)
            || !TryReadRequiredBoolean(payload, "incognitoProtected", out bool incognitoProtected)
            || !TryReadRequiredBoolean(
                payload,
                "incognitoWarningAcknowledged",
                out bool incognitoWarningAcknowledged)
            || !payload.TryGetProperty(
                "iPhoneConfirmedThroughStep",
                out JsonElement iPhoneStepElement)
            || iPhoneStepElement.ValueKind != JsonValueKind.Number
            || !iPhoneStepElement.TryGetInt32(out int iPhoneConfirmedThroughStep)
            || iPhoneConfirmedThroughStep is < 0 or > 4
            || !TryReadRequiredBoolean(
                payload,
                "chromeDegradedAcknowledged",
                out bool chromeDegradedAcknowledged))
        {
            return null;
        }

        return new(
            step,
            chromeVerified,
            incognitoProtected,
            incognitoWarningAcknowledged,
            iPhoneConfirmedThroughStep,
            chromeDegradedAcknowledged);
    }

    private static SaveNightSelfReportCommand? CreateSaveNightSelfReportCommand(
        JsonElement payload)
    {
        if (!HasOnlyProperties(
                payload,
                "nightDate",
                "phoneOutOfReach",
                "wakeWithinWindow")
            || payload.EnumerateObject().Count() != 3
            || !payload.TryGetProperty("nightDate", out JsonElement nightDateElement)
            || nightDateElement.ValueKind != JsonValueKind.String
            || !DateOnly.TryParseExact(
                nightDateElement.GetString(),
                "yyyy-MM-dd",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out DateOnly nightDate)
            || nightDate == default
            || !TryReadRequiredNullableBoolean(
                payload,
                "phoneOutOfReach",
                out bool? phoneOutOfReach)
            || !TryReadRequiredNullableBoolean(
                payload,
                "wakeWithinWindow",
                out bool? wakeWithinWindow))
        {
            return null;
        }

        return new(nightDate, phoneOutOfReach, wakeWithinWindow);
    }

    private static RecordChromeHealthCommand? CreateRecordChromeHealthCommand(
        JsonElement payload)
    {
        bool hasProtectionReady = payload.TryGetProperty(
            "protectionReady",
            out JsonElement protectionReadyElement);
        bool protectionReady = false;
        if (!HasOnlyProperties(
                payload,
                "extensionId",
                "extensionVersion",
                "profileTokenSha256",
                "policyRevision",
                "incognitoAllowed",
                "protectionReady")
            || payload.EnumerateObject().Count() != (hasProtectionReady ? 6 : 5)
            || !payload.TryGetProperty("extensionId", out JsonElement extensionIdElement)
            || extensionIdElement.ValueKind != JsonValueKind.String
            || extensionIdElement.GetString() is not { } extensionId
            || !payload.TryGetProperty(
                "extensionVersion",
                out JsonElement extensionVersionElement)
            || extensionVersionElement.ValueKind != JsonValueKind.String
            || extensionVersionElement.GetString() is not { } extensionVersion
            || !payload.TryGetProperty(
                "profileTokenSha256",
                out JsonElement profileHashElement)
            || profileHashElement.ValueKind != JsonValueKind.String
            || profileHashElement.GetString() is not { } profileTokenSha256
            || !payload.TryGetProperty("policyRevision", out JsonElement revisionElement)
            || revisionElement.ValueKind != JsonValueKind.Number
            || !revisionElement.TryGetInt64(out long policyRevision)
            || policyRevision is < 0 or > MaximumJavaScriptSafeInteger
            || !TryReadRequiredBoolean(payload, "incognitoAllowed", out bool incognitoAllowed)
            || (hasProtectionReady
                && protectionReadyElement.ValueKind is not (
                    JsonValueKind.True or JsonValueKind.False)))
        {
            return null;
        }
        if (hasProtectionReady)
        {
            protectionReady = protectionReadyElement.GetBoolean();
        }

        _ = new ChromeProtectionHealth(
            extensionId,
            extensionVersion,
            profileTokenSha256,
            policyRevision,
            incognitoAllowed,
            DateTimeOffset.UnixEpoch,
            protectionReady);
        return new(
            extensionId,
            extensionVersion,
            profileTokenSha256,
            policyRevision,
            incognitoAllowed,
            protectionReady);
    }

    private static PrepareLegacyTaskMigrationCommand?
        CreatePrepareLegacyTaskMigrationCommand(JsonElement payload)
    {
        if (!HasOnlyProperties(
                payload,
                "taskPath",
                "actionFingerprint",
                "originalEnabled")
            || payload.EnumerateObject().Count() != 3
            || !payload.TryGetProperty("taskPath", out JsonElement taskPathElement)
            || taskPathElement.ValueKind != JsonValueKind.String
            || taskPathElement.GetString() is not { } taskPath
            || !payload.TryGetProperty(
                "actionFingerprint",
                out JsonElement fingerprintElement)
            || fingerprintElement.ValueKind != JsonValueKind.String
            || fingerprintElement.GetString() is not { } actionFingerprint
            || !TryReadRequiredBoolean(payload, "originalEnabled", out bool originalEnabled))
        {
            return null;
        }

        _ = new LegacyTaskMigrationRecord(
            "validation",
            taskPath,
            actionFingerprint,
            originalEnabled,
            LegacyTaskMigrationStatus.Prepared,
            DateTimeOffset.UnixEpoch);
        return new(taskPath, actionFingerprint, originalEnabled);
    }

    private static ListLegacyTaskMigrationsCommand?
        CreateListLegacyTaskMigrationsCommand(JsonElement payload)
    {
        if (IsEmptyObject(payload))
        {
            return new();
        }

        if (!HasOnlyProperties(payload, "cursor")
            || payload.EnumerateObject().Count() != 1
            || !payload.TryGetProperty("cursor", out JsonElement cursorElement)
            || cursorElement.ValueKind != JsonValueKind.String
            || cursorElement.GetString() is not { } cursor
            || string.IsNullOrWhiteSpace(cursor)
            || cursor.Length > LegacyTaskMigrationRecord.MaximumMigrationIdLength
            || !string.Equals(cursor, cursor.Trim(), StringComparison.Ordinal))
        {
            return null;
        }

        return new(cursor);
    }

    private static CompleteLegacyTaskMigrationCommand?
        CreateCompleteLegacyTaskMigrationCommand(JsonElement payload)
    {
        if (!HasOnlyProperties(payload, "migrationId", "status")
            || payload.EnumerateObject().Count() != 2
            || !payload.TryGetProperty("migrationId", out JsonElement migrationIdElement)
            || migrationIdElement.ValueKind != JsonValueKind.String
            || migrationIdElement.GetString() is not { } migrationId
            || string.IsNullOrWhiteSpace(migrationId)
            || migrationId.Length > LegacyTaskMigrationRecord.MaximumMigrationIdLength
            || !string.Equals(migrationId, migrationId.Trim(), StringComparison.Ordinal)
            || !payload.TryGetProperty("status", out JsonElement statusElement)
            || statusElement.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        LegacyTaskMigrationStatus status = statusElement.GetString() switch
        {
            "disabled" => LegacyTaskMigrationStatus.Disabled,
            "restorePrepared" => LegacyTaskMigrationStatus.RestorePrepared,
            "restored" => LegacyTaskMigrationStatus.Restored,
            "failed" => LegacyTaskMigrationStatus.Failed,
            _ => (LegacyTaskMigrationStatus)(-1),
        };
        return Enum.IsDefined(status) && status != LegacyTaskMigrationStatus.Prepared
            ? new(migrationId, status)
            : null;
    }

    private static FindLegacyTaskMigrationRecoveryCandidateCommand?
        CreateFindLegacyTaskMigrationRecoveryCandidateCommand(JsonElement payload)
    {
        if (!HasOnlyProperties(payload, "taskPath")
            || payload.EnumerateObject().Count() != 1
            || !payload.TryGetProperty("taskPath", out JsonElement taskPathElement)
            || taskPathElement.ValueKind != JsonValueKind.String
            || taskPathElement.GetString() is not { } taskPath
            || string.IsNullOrWhiteSpace(taskPath)
            || taskPath.Length > LegacyTaskMigrationRecord.MaximumTaskPathLength
            || !string.Equals(taskPath, taskPath.Trim(), StringComparison.Ordinal)
            || !taskPath.StartsWith('\\')
            || taskPath.Contains('\0'))
        {
            return null;
        }

        return new(taskPath);
    }

    private static RecoverLegacyTaskMigrationDisabledCommand?
        CreateRecoverLegacyTaskMigrationDisabledCommand(JsonElement payload)
    {
        if (!HasOnlyProperties(
                payload,
                "migrationId",
                "taskPath",
                "actionFingerprint",
                "originalEnabled",
                "recoveryToken")
            || payload.EnumerateObject().Count() != 5
            || !payload.TryGetProperty("migrationId", out JsonElement idElement)
            || idElement.ValueKind != JsonValueKind.String
            || idElement.GetString() is not { } migrationId
            || string.IsNullOrWhiteSpace(migrationId)
            || migrationId.Length > LegacyTaskMigrationRecord.MaximumMigrationIdLength
            || !string.Equals(migrationId, migrationId.Trim(), StringComparison.Ordinal)
            || !payload.TryGetProperty("taskPath", out JsonElement taskPathElement)
            || taskPathElement.ValueKind != JsonValueKind.String
            || taskPathElement.GetString() is not { } taskPath
            || !payload.TryGetProperty(
                "actionFingerprint",
                out JsonElement fingerprintElement)
            || fingerprintElement.ValueKind != JsonValueKind.String
            || fingerprintElement.GetString() is not { } actionFingerprint
            || !TryReadRequiredBoolean(payload, "originalEnabled", out bool originalEnabled)
            || !payload.TryGetProperty("recoveryToken", out JsonElement tokenElement)
            || tokenElement.ValueKind != JsonValueKind.String
            || tokenElement.GetString() is not { } recoveryToken
            || recoveryToken.Length != LegacyTaskMigrationRecord.RecoveryTokenLength
            || recoveryToken.Any(character => character is not (>= '0' and <= '9')
                and not (>= 'a' and <= 'f')))
        {
            return null;
        }

        _ = new LegacyTaskMigrationRecord(
            migrationId,
            taskPath,
            actionFingerprint,
            originalEnabled,
            LegacyTaskMigrationStatus.Prepared,
            DateTimeOffset.UnixEpoch);
        return new(
            migrationId,
            taskPath,
            actionFingerprint,
            originalEnabled,
            recoveryToken);
    }

    private static SaveRuleSettingsCommand? CreateSaveRuleSettingsCommand(JsonElement payload)
    {
        if (!HasOnlyProperties(payload, "appRules", "siteRules")
            || payload.EnumerateObject().Count() != 2
            || !payload.TryGetProperty("appRules", out JsonElement appRulesElement)
            || appRulesElement.ValueKind != JsonValueKind.Array
            || !payload.TryGetProperty("siteRules", out JsonElement siteRulesElement)
            || siteRulesElement.ValueKind != JsonValueKind.Array
            || appRulesElement.GetArrayLength() > RuleSettingsState.MaximumRulesPerSet
            || siteRulesElement.GetArrayLength() > RuleSettingsState.MaximumRulesPerSet)
        {
            return null;
        }

        var appRules = ImmutableArray.CreateBuilder<AppRule>();
        foreach (JsonElement appElement in appRulesElement.EnumerateArray())
        {
            AppRule? appRule = CreateAppRule(appElement);
            if (appRule is null)
            {
                return null;
            }

            appRules.Add(appRule);
        }

        var siteDomains = new SortedSet<string>(StringComparer.Ordinal);
        foreach (JsonElement siteElement in siteRulesElement.EnumerateArray())
        {
            if (siteElement.ValueKind != JsonValueKind.Object
                || !HasOnlyProperties(siteElement, "domain")
                || siteElement.EnumerateObject().Count() != 1
                || !siteElement.TryGetProperty("domain", out JsonElement domainElement)
                || domainElement.ValueKind != JsonValueKind.String
                || !SupportedEntertainmentSiteCatalog.IsSupported(domainElement.GetString()))
            {
                return null;
            }

            siteDomains.Add(domainElement.GetString()!);
        }

        ImmutableArray<AppRule> canonicalApps = appRules.ToImmutable();
        ImmutableArray<SiteRule> canonicalSites = siteDomains
            .Select(domain => new SiteRule(domain))
            .ToImmutableArray();
        _ = new RuleSettingsState(canonicalApps, canonicalSites);
        return new(canonicalApps, canonicalSites);
    }

    private static AppRule? CreateAppRule(JsonElement payload)
    {
        if (payload.ValueKind != JsonValueKind.Object
            || !HasOnlyProperties(
                payload,
                "id",
                "rootExecutablePath",
                "helperExecutablePaths",
                "category",
                "sessionMinutes")
            || payload.EnumerateObject().Count() != 5
            || !payload.TryGetProperty("id", out JsonElement idElement)
            || idElement.ValueKind != JsonValueKind.String
            || !payload.TryGetProperty(
                "rootExecutablePath",
                out JsonElement rootExecutablePathElement)
            || rootExecutablePathElement.ValueKind != JsonValueKind.String
            || !payload.TryGetProperty(
                "helperExecutablePaths",
                out JsonElement helperExecutablePathsElement)
            || helperExecutablePathsElement.ValueKind != JsonValueKind.Array
            || helperExecutablePathsElement.GetArrayLength()
                > AppRule.MaximumHelperExecutablePaths
            || !payload.TryGetProperty("category", out JsonElement categoryElement)
            || categoryElement.ValueKind != JsonValueKind.String
            || !TryParseAppRuleCategory(categoryElement.GetString(), out AppRuleCategory category)
            || !payload.TryGetProperty("sessionMinutes", out JsonElement sessionMinutesElement)
            || sessionMinutesElement.ValueKind != JsonValueKind.Number
            || !sessionMinutesElement.TryGetInt32(out int sessionMinutes))
        {
            return null;
        }

        var helperExecutablePaths = ImmutableArray.CreateBuilder<string>();
        foreach (JsonElement helperElement in helperExecutablePathsElement.EnumerateArray())
        {
            if (helperElement.ValueKind != JsonValueKind.String)
            {
                return null;
            }

            helperExecutablePaths.Add(helperElement.GetString()!);
        }

        return new(
            idElement.GetString()!,
            rootExecutablePathElement.GetString()!,
            helperExecutablePaths,
            category,
            sessionMinutes);
    }

    private static bool TryParseAppRuleCategory(string? token, out AppRuleCategory category)
    {
        switch (token)
        {
            case "game": category = AppRuleCategory.Game; return true;
            case "voice": category = AppRuleCategory.Voice; return true;
            default: category = default; return false;
        }
    }

    private static bool TryReadRequiredNullableBoolean(
        JsonElement payload,
        string name,
        out bool? value)
    {
        value = null;
        if (!payload.TryGetProperty(name, out JsonElement element))
        {
            return false;
        }

        if (element.ValueKind == JsonValueKind.Null)
        {
            return true;
        }

        if (element.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            return false;
        }

        value = element.GetBoolean();
        return true;
    }

    private RecordBrowserEventCommand? CreateRecordBrowserEventCommand(JsonElement payload)
    {
        if (!HasOnlyProperties(payload, "timestamp", "eventType", "category")
            || payload.EnumerateObject().Count() != 3
            || !payload.TryGetProperty("timestamp", out JsonElement timestampElement)
            || timestampElement.ValueKind != JsonValueKind.String
            || !DateTimeOffset.TryParseExact(
                timestampElement.GetString(),
                "yyyy-MM-dd'T'HH:mm:ss.fff'Z'",
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out DateTimeOffset timestampUtc)
            || !payload.TryGetProperty("eventType", out JsonElement eventTypeElement)
            || !TryParseBrowserEventType(eventTypeElement, out BrowserEventType eventType)
            || !payload.TryGetProperty("category", out JsonElement categoryElement)
            || !TryParseBrowserSiteCategory(categoryElement, out BrowserSiteCategory category))
        {
            return null;
        }

        DateTimeOffset now = (clock?.UtcNow ?? DateTimeOffset.UtcNow).ToUniversalTime();
        if (timestampUtc < now - BrowserEventLimits.MaximumAge
            || timestampUtc > now + BrowserEventLimits.MaximumFutureSkew)
        {
            return null;
        }

        return new(new(timestampUtc, eventType, category));
    }

    private static LoadProcessPersistenceCommand? CreateLoadProcessPersistenceCommand(
        JsonElement payload)
    {
        if (!HasOnlyProperties(payload, "slot")
            || payload.EnumerateObject().Count() != 1
            || !payload.TryGetProperty("slot", out JsonElement slotElement)
            || !TryParseProcessPersistenceSlot(slotElement, out ProcessPersistenceSlot slot))
        {
            return null;
        }

        return new(slot);
    }

    private static CompareExchangeProcessPersistenceCommand?
        CreateCompareExchangeProcessPersistenceCommand(JsonElement payload)
    {
        if (!HasOnlyProperties(
                payload,
                "slot",
                "expectedVersion",
                "schemaVersion",
                "replacementVersion",
                "payload")
            || payload.EnumerateObject().Count() != 5
            || !payload.TryGetProperty("slot", out JsonElement slotElement)
            || !TryParseProcessPersistenceSlot(slotElement, out ProcessPersistenceSlot slot)
            || !payload.TryGetProperty("expectedVersion", out JsonElement expectedElement)
            || !TryParseExpectedProcessPersistenceVersion(
                expectedElement,
                out long? expectedVersion)
            || !payload.TryGetProperty("schemaVersion", out JsonElement schemaElement)
            || schemaElement.ValueKind != JsonValueKind.Number
            || !schemaElement.TryGetInt32(out int schemaVersion)
            || schemaVersion != ProcessPersistenceLimits.CurrentSchemaVersion
            || !payload.TryGetProperty(
                "replacementVersion",
                out JsonElement replacementVersionElement)
            || replacementVersionElement.ValueKind != JsonValueKind.Number
            || !replacementVersionElement.TryGetInt64(out long replacementVersion)
            || replacementVersion != (expectedVersion ?? 0) + 1
            || replacementVersion is < 1 or long.MaxValue
            || !payload.TryGetProperty("payload", out JsonElement persistedPayload)
            || persistedPayload.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        string payloadJson = persistedPayload.GetRawText();
        if (!ProcessPersistenceLimits.IsValidPayload(payloadJson, schemaVersion))
        {
            return null;
        }

        return new(
            slot,
            expectedVersion,
            new(slot, schemaVersion, replacementVersion, payloadJson));
    }

    private static bool TryParseProcessPersistenceSlot(
        JsonElement element,
        out ProcessPersistenceSlot slot)
    {
        if (element.ValueKind == JsonValueKind.String
            && ProcessPersistenceLimits.TryParseSlotToken(element.GetString(), out slot))
        {
            return true;
        }

        slot = default;
        return false;
    }

    private static bool TryParseExpectedProcessPersistenceVersion(
        JsonElement element,
        out long? version)
    {
        if (element.ValueKind == JsonValueKind.Null)
        {
            version = null;
            return true;
        }

        if (element.ValueKind == JsonValueKind.Number
            && element.TryGetInt64(out long parsed)
            && parsed is >= 1 and < long.MaxValue)
        {
            version = parsed;
            return true;
        }

        version = null;
        return false;
    }

    private static bool HasOnlyProperties(JsonElement payload, params string[] allowedNames)
    {
        HashSet<string> seen = new(StringComparer.Ordinal);
        foreach (JsonProperty property in payload.EnumerateObject())
        {
            if (!allowedNames.Contains(property.Name, StringComparer.Ordinal)
                || !seen.Add(property.Name))
            {
                return false;
            }
        }

        return true;
    }

    private static bool TryParseOverrideKind(JsonElement element, out OverrideKind value)
    {
        value = default;
        if (element.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        switch (element.GetString())
        {
            case "teamRescue": value = OverrideKind.TeamRescue; return true;
            case "emergency": value = OverrideKind.Emergency; return true;
            case "entertainment": value = OverrideKind.Entertainment; return true;
            default: return false;
        }
    }

    private static bool TryParseEmergencyReason(JsonElement element, out EmergencyReason value)
    {
        value = default;
        if (element.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        switch (element.GetString())
        {
            case "health": value = EmergencyReason.Health; return true;
            case "safety": value = EmergencyReason.Safety; return true;
            case "urgentWork": value = EmergencyReason.UrgentWork; return true;
            default: return false;
        }
    }

    private static bool TryParseNightEventKind(JsonElement element, out NightEventKind value)
    {
        value = default;
        if (element.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        switch (element.GetString())
        {
            case "nightStarted": value = NightEventKind.NightStarted; return true;
            case "stateObserved": value = NightEventKind.StateObserved; return true;
            case "basePhaseAdvanced": value = NightEventKind.BasePhaseAdvanced; return true;
            case "overrideRequested": value = NightEventKind.OverrideRequested; return true;
            case "overrideEnded": value = NightEventKind.OverrideEnded; return true;
            case "nightClosed": value = NightEventKind.NightClosed; return true;
            case "historyCleared": value = NightEventKind.HistoryCleared; return true;
            case "serviceDegraded": value = NightEventKind.ServiceDegraded; return true;
            case "deliberateBypass": value = NightEventKind.DeliberateBypass; return true;
            case "lateNewEntertainment": value = NightEventKind.LateNewEntertainment; return true;
            case "missedLock": value = NightEventKind.MissedLock; return true;
            case "workstationLocked": value = NightEventKind.WorkstationLocked; return true;
            default: return false;
        }
    }

    private static bool TryParseNightPhase(JsonElement element, out NightPhase value)
    {
        value = default;
        if (element.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        switch (element.GetString())
        {
            case "free": value = NightPhase.Free; return true;
            case "lastStart": value = NightPhase.LastStart; return true;
            case "grace": value = NightPhase.Grace; return true;
            case "landingLocked": value = NightPhase.LandingLocked; return true;
            case "morning": value = NightPhase.Morning; return true;
            default: return false;
        }
    }

    private static bool TryParseBrowserEventType(
        JsonElement element,
        out BrowserEventType value)
    {
        value = default;
        if (element.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        switch (element.GetString())
        {
            case "mediaPlaying": value = BrowserEventType.MediaPlaying; return true;
            case "mediaPaused": value = BrowserEventType.MediaPaused; return true;
            case "mediaEnded": value = BrowserEventType.MediaEnded; return true;
            case "navigationBlocked": value = BrowserEventType.NavigationBlocked; return true;
            default: return false;
        }
    }

    private static bool TryParseBrowserSiteCategory(
        JsonElement element,
        out BrowserSiteCategory value)
    {
        value = default;
        if (element.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        switch (element.GetString())
        {
            case "gaming": value = BrowserSiteCategory.Gaming; return true;
            case "video": value = BrowserSiteCategory.Video; return true;
            case "social": value = BrowserSiteCategory.Social; return true;
            case "other": value = BrowserSiteCategory.Other; return true;
            default: return false;
        }
    }

    private static ProtocolDispatchResult Error(
        string requestId,
        string code,
        bool commandExecuted = false)
    {
        JsonElement payload = JsonSerializer.SerializeToElement(
            new ProtocolErrorPayload(code),
            ProtocolJson.Options);
        return new(
            Serialize(new(ProtocolVersion, "error", requestId, payload)),
            commandExecuted,
            commandExecuted);
    }

    private static ReadOnlyMemory<byte> Serialize(ProtocolResponseEnvelope response) =>
        JsonSerializer.SerializeToUtf8Bytes(response, ProtocolJson.Options);

    private sealed record ProtocolErrorPayload(string Code);

    private sealed record ProtocolResponseEnvelope(
        int Version,
        string Type,
        string RequestId,
        JsonElement Payload);
}

internal static class ProtocolJson
{
    public static JsonSerializerOptions Options { get; } = CreateOptions();

    private static JsonSerializerOptions CreateOptions()
    {
        JsonSerializerOptions options = new(JsonSerializerDefaults.Web)
        {
            PropertyNameCaseInsensitive = false,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        };
        options.Converters.Add(new JsonStringEnumConverter(
            JsonNamingPolicy.CamelCase,
            allowIntegerValues: false));
        return options;
    }
}
