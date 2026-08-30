using System.Text;
using System.Text.Json;
using NightGate.Desktop;

namespace NightGate.Desktop.Tests;

public sealed class UserExperienceClientTests
{
    [Fact]
    public async Task GetUserState_WritesExactRequestAndReturnsValidatedFacts()
    {
        RecordingTransport transport = new(UserStateResponse("state-1"));
        NightGateDesktopClient client = new(
            transport,
            new UserExperienceRequestIds("state-1"));

        DesktopUserStateResult result = await client.GetUserStateAsync();

        Assert.True(result.Available);
        Assert.Null(result.Error);
        Assert.Equal(1, result.State!.Progress.CurrentStep);
        Assert.Equal(new DateOnly(2026, 7, 14), result.State.CurrentNightDate);
        Assert.Null(result.State.SelfReport);
        Assert.True(result.State.ChromeProtection.IsHealthy);
        Assert.True(result.State.ChromeProtection.IncognitoProtected);
        Assert.Equal("1.0.0", result.State.ChromeProtection.ExtensionVersion);
        Assert.True(result.State.Onboarding.ChromeDegradedAcknowledged);
        using JsonDocument request = JsonDocument.Parse(transport.Requests.Single());
        Assert.Equal("getUserState", request.RootElement.GetProperty("type").GetString());
        Assert.Equal("state-1", request.RootElement.GetProperty("requestId").GetString());
        Assert.Empty(request.RootElement.GetProperty("payload").EnumerateObject());
    }

    [Fact]
    public async Task GetUserState_AcceptsProtectionDegradedStatus()
    {
        string response = UserStateResponse("not-ready")
            .Replace("\"status\":\"healthy\",\"isHealthy\":true", "\"status\":\"protectionDegraded\",\"isHealthy\":false", StringComparison.Ordinal);
        NightGateDesktopClient client = new(
            new RecordingTransport(response),
            new UserExperienceRequestIds("not-ready"));

        DesktopUserStateResult result = await client.GetUserStateAsync();

        Assert.True(result.Available);
        Assert.Equal("protectionDegraded", result.State!.ChromeProtection.Status);
        Assert.False(result.State.ChromeProtection.IsHealthy);
    }

    [Theory]
    [InlineData("unknownField")]
    [InlineData("invalidStep")]
    [InlineData("mismatchedSelfReport")]
    public async Task GetUserState_RejectsUnknownOrInconsistentFacts(string mutation)
    {
        string response = mutation switch
        {
            "unknownField" => UserStateResponse("strict")
                .Replace("\"currentNightDate\"", "\"surprise\":true,\"currentNightDate\"", StringComparison.Ordinal),
            "invalidStep" => UserStateResponse("strict")
                .Replace("\"currentStep\":1", "\"currentStep\":9", StringComparison.Ordinal),
            "mismatchedSelfReport" => UserStateResponse("strict")
                .Replace("\"selfReport\":null", "\"selfReport\":{\"nightDate\":\"2026-07-13\",\"phoneOutOfReach\":true,\"wakeWithinWindow\":null,\"updatedAtUtc\":\"2026-07-14T16:00:00Z\"}", StringComparison.Ordinal),
            _ => throw new ArgumentOutOfRangeException(nameof(mutation)),
        };
        NightGateDesktopClient client = new(
            new RecordingTransport(response),
            new UserExperienceRequestIds("strict"));

        DesktopUserStateResult result = await client.GetUserStateAsync();

        Assert.False(result.Available);
        Assert.Equal(
            mutation == "unknownField" ? "service-unavailable" : "service-degraded",
            result.Error);
        Assert.Null(result.State);
    }

    [Fact]
    public async Task GetUserState_AcceptsLegacyOnboardingWithoutDegradedAcknowledgement()
    {
        string response = UserStateResponse("legacy-state").Replace(
            ",\"chromeDegradedAcknowledged\":true",
            string.Empty,
            StringComparison.Ordinal);
        NightGateDesktopClient client = new(
            new RecordingTransport(response),
            new UserExperienceRequestIds("legacy-state"));

        DesktopUserStateResult result = await client.GetUserStateAsync();

        Assert.True(result.Available);
        Assert.False(result.State!.Onboarding.ChromeDegradedAcknowledged);
    }

    [Fact]
    public async Task GetUserState_RejectsCompletedChromeStepWithoutHealthyOrDegradedInvariant()
    {
        string response = UserStateResponse("invalid-chrome")
            .Replace("\"completedStep\":0", "\"completedStep\":3", StringComparison.Ordinal)
            .Replace(
                "\"chromeDegradedAcknowledged\":true",
                "\"chromeDegradedAcknowledged\":false",
                StringComparison.Ordinal);
        NightGateDesktopClient client = new(
            new RecordingTransport(response),
            new UserExperienceRequestIds("invalid-chrome"));

        DesktopUserStateResult result = await client.GetUserStateAsync();

        Assert.False(result.Available);
        Assert.Equal("service-degraded", result.Error);
    }

    [Fact]
    public async Task CompleteOnboardingStep_SendsOnlyPersistableFactsAndValidatesAcceptedState()
    {
        RecordingTransport transport = new(OnboardingResponse("onboard-1"));
        NightGateDesktopClient client = new(
            transport,
            new UserExperienceRequestIds("onboard-1"));
        DesktopOnboardingStepRequest request = new(
            Step: 3,
            ChromeVerified: false,
            IncognitoProtected: false,
            IncognitoWarningAcknowledged: false,
            IPhoneConfirmedThroughStep: 0,
            ChromeDegradedAcknowledged: true);

        DesktopOnboardingMutationResult result = await client
            .CompleteOnboardingStepAsync(request);

        Assert.True(result.Accepted);
        Assert.Equal(3, result.Onboarding!.CompletedStep);
        Assert.True(result.Onboarding.ChromeDegradedAcknowledged);
        using JsonDocument sent = JsonDocument.Parse(transport.Requests.Single());
        JsonElement payload = sent.RootElement.GetProperty("payload");
        Assert.Equal(6, payload.EnumerateObject().Count());
        Assert.Equal(3, payload.GetProperty("step").GetInt32());
        Assert.False(payload.GetProperty("chromeVerified").GetBoolean());
        Assert.False(payload.GetProperty("incognitoProtected").GetBoolean());
        Assert.False(payload.GetProperty("incognitoWarningAcknowledged").GetBoolean());
        Assert.Equal(0, payload.GetProperty("iPhoneConfirmedThroughStep").GetInt32());
        Assert.True(payload.GetProperty("chromeDegradedAcknowledged").GetBoolean());
    }

    [Fact]
    public async Task SaveNightSelfReport_UsesLogicalNightAndReturnsServiceStampedReport()
    {
        RecordingTransport transport = new(SelfReportResponse("report-1"));
        NightGateDesktopClient client = new(
            transport,
            new UserExperienceRequestIds("report-1"));

        DesktopSelfReportMutationResult result = await client.SaveNightSelfReportAsync(
            new DateOnly(2026, 7, 14),
            phoneOutOfReach: true,
            wakeWithinWindow: null);

        Assert.True(result.Saved);
        Assert.Equal(new DateOnly(2026, 7, 14), result.SelfReport!.NightDate);
        using JsonDocument sent = JsonDocument.Parse(transport.Requests.Single());
        JsonElement payload = sent.RootElement.GetProperty("payload");
        Assert.Equal(3, payload.EnumerateObject().Count());
        Assert.Equal("2026-07-14", payload.GetProperty("nightDate").GetString());
        Assert.True(payload.GetProperty("phoneOutOfReach").GetBoolean());
        Assert.Equal(JsonValueKind.Null, payload.GetProperty("wakeWithinWindow").ValueKind);
    }

    [Fact]
    public async Task SaveRuleSettings_OmitsComputedFieldsAndReturnsEffectiveTiming()
    {
        RecordingTransport transport = new(RuleSettingsResponse("rules-1"));
        NightGateDesktopClient client = new(
            transport,
            new UserExperienceRequestIds("rules-1"));
        DesktopAppRuleDraft game = new(
            "game-main",
            @"C:\Games\Main\game.exe",
            [@"C:\Games\Main\helper.exe"],
            DesktopAppRuleCategory.Game,
            45);

        DesktopRuleSettingsMutationResult result = await client.SaveRuleSettingsAsync(
            [game],
            ["youtube.com"]);

        Assert.True(result.Saved);
        Assert.True(result.AppliesImmediately);
        Assert.True(result.AppliesTonight);
        Assert.Null(result.EffectiveNight);
        Assert.Single(result.Rules!.ActiveAppRules);
        using JsonDocument sent = JsonDocument.Parse(transport.Requests.Single());
        JsonElement app = sent.RootElement.GetProperty("payload")
            .GetProperty("appRules")[0];
        Assert.Equal(5, app.EnumerateObject().Count());
        Assert.False(app.TryGetProperty("isConfigured", out _));
        Assert.Equal("game", app.GetProperty("category").GetString());
        Assert.Equal(45, app.GetProperty("sessionMinutes").GetInt32());
    }

    [Fact]
    public async Task SaveRuleSettings_DecodesTonightStagingAsPendingInsteadOfActive()
    {
        RecordingTransport transport = new(StagedRuleSettingsResponse("rules-staged"));
        NightGateDesktopClient client = new(
            transport,
            new UserExperienceRequestIds("rules-staged"));
        DesktopAppRuleDraft game = new(
            "game-main",
            @"C:\Games\Main\game.exe",
            [@"C:\Games\Main\helper.exe"],
            DesktopAppRuleCategory.Game,
            45);

        DesktopRuleSettingsMutationResult result = await client.SaveRuleSettingsAsync(
            [game],
            ["youtube.com"]);

        Assert.True(result.Saved);
        Assert.False(result.AppliesImmediately);
        Assert.True(result.AppliesTonight);
        Assert.Equal(new DateOnly(2026, 7, 14), result.EffectiveNight);
        Assert.Single(result.Rules!.PendingAppRules!);
        Assert.Empty(result.Rules.ActiveAppRules);
    }

    [Fact]
    public async Task ClaimDueNotice_UsesServiceClaimAndDecodesOnlyKnownNoticeKinds()
    {
        RecordingTransport transport = new(NoticeResponse("notice-1"));
        NightGateDesktopClient client = new(
            transport,
            new UserExperienceRequestIds("notice-1"));

        DesktopNoticeClaimResult result = await client.ClaimDueNoticeAsync();

        Assert.True(result.Claimed);
        Assert.Equal(DesktopNightNoticeKind.Grace10, result.Kind);
        Assert.Equal(new DateOnly(2026, 7, 14), result.NightDate);
        using JsonDocument sent = JsonDocument.Parse(transport.Requests.Single());
        Assert.Equal("claimDueNotice", sent.RootElement.GetProperty("type").GetString());
        Assert.Empty(sent.RootElement.GetProperty("payload").EnumerateObject());
    }

    [Fact]
    public async Task ConfirmIPhoneProgression_SendsAllTenChecklistFacts()
    {
        RecordingTransport transport = new(IPhoneProgressionResponse("iphone-1"));
        NightGateDesktopClient client = new(
            transport,
            new UserExperienceRequestIds("iphone-1"));
        DesktopIPhoneChecklist checklist = DesktopIPhoneChecklist.AllConfirmed;

        DesktopIPhoneProgressionResult result = await client
            .ConfirmIPhoneProgressionAsync(2, checklist);

        Assert.True(result.Accepted);
        Assert.Equal(2, result.PendingStep);
        Assert.Equal(new DateOnly(2026, 7, 15), result.EffectiveNightDate);
        using JsonDocument sent = JsonDocument.Parse(transport.Requests.Single());
        JsonElement payload = sent.RootElement.GetProperty("payload");
        Assert.Equal(2, payload.GetProperty("step").GetInt32());
        JsonElement sentChecklist = payload.GetProperty("checklist");
        Assert.Equal(10, sentChecklist.EnumerateObject().Count());
        Assert.True(sentChecklist.GetProperty("entertainmentCategoriesRestricted").GetBoolean());
        Assert.All(sentChecklist.EnumerateObject(), property => Assert.True(property.Value.GetBoolean()));
    }

    [Fact]
    public async Task ClearHistory_UsesDedicatedCommandAndDoesNotClaimOperationalStateWasCleared()
    {
        RecordingTransport transport = new(ClearHistoryResponse("clear-1"));
        NightGateDesktopClient client = new(
            transport,
            new UserExperienceRequestIds("clear-1"));

        DesktopClearHistoryResult result = await client.ClearHistoryAsync();

        Assert.True(result.Cleared);
        Assert.Null(result.Error);
        using JsonDocument sent = JsonDocument.Parse(transport.Requests.Single());
        Assert.Equal("clearHistory", sent.RootElement.GetProperty("type").GetString());
        Assert.Empty(sent.RootElement.GetProperty("payload").EnumerateObject());
    }

    private static string UserStateResponse(string requestId) => """
        {"version":1,"type":"getUserStateResult","requestId":"$REQUEST$","payload":{"status":"success","data":{"progress":{"currentStep":1,"lastTeamRescueAtUtc":null,"lastProgressionNightDate":null,"pendingStep":null,"pendingStepUnlockedByNightDate":null,"pendingStepConfirmedAtUtc":null,"pendingStepEffectiveNightDate":null},"onboarding":{"wizardVersion":1,"completedStep":0,"chromeVerified":false,"incognitoProtected":false,"incognitoWarningAcknowledged":false,"iPhoneConfirmedThroughStep":0,"completedAtUtc":null,"chromeDegradedAcknowledged":true},"rules":{"activeAppRules":[],"activeSiteRules":[],"pendingAppRules":null,"pendingSiteRules":null,"pendingEffectiveNightDate":null,"pendingSavedAtUtc":null},"weeklyReport":{"periodStart":"2026-07-09","periodEnd":"2026-07-15","observedWorkNights":0,"eligibleWorkNights":0,"qualifyingWorkNights":0,"lockObservations":0,"medianLockTime":null,"medianLockChangeMinutes":null,"overrideReasons":{"teamRescueCount":0,"entertainmentCount":0,"emergencyHealthCount":0,"emergencySafetyCount":0,"emergencyUrgentWorkCount":0,"emergencyOtherCount":0}},"currentNightDate":"2026-07-14","selfReport":null,"chromeProtection":{"status":"healthy","isHealthy":true,"incognitoProtected":true,"lastHeartbeatAtUtc":"2026-07-14T16:00:00Z","extensionVersion":"1.0.0"}}}}
        """.Replace("$REQUEST$", requestId, StringComparison.Ordinal);

    private static string OnboardingResponse(string requestId) => """
        {"version":1,"type":"completeOnboardingStepResult","requestId":"$REQUEST$","payload":{"status":"success","data":{"accepted":true,"onboarding":{"wizardVersion":1,"completedStep":3,"chromeVerified":false,"incognitoProtected":false,"incognitoWarningAcknowledged":false,"iPhoneConfirmedThroughStep":0,"completedAtUtc":null,"chromeDegradedAcknowledged":true}}}}
        """.Replace("$REQUEST$", requestId, StringComparison.Ordinal);

    private static string SelfReportResponse(string requestId) => """
        {"version":1,"type":"saveNightSelfReportResult","requestId":"$REQUEST$","payload":{"status":"success","data":{"saved":true,"selfReport":{"nightDate":"2026-07-14","phoneOutOfReach":true,"wakeWithinWindow":null,"updatedAtUtc":"2026-07-14T16:00:00Z"}}}}
        """.Replace("$REQUEST$", requestId, StringComparison.Ordinal);

    private static string RuleSettingsResponse(string requestId) => """
        {"version":1,"type":"saveRuleSettingsResult","requestId":"$REQUEST$","payload":{"status":"success","data":{"saved":true,"rules":{"activeAppRules":[{"id":"game-main","rootExecutablePath":"C:\\Games\\Main\\game.exe","helperExecutablePaths":["C:\\Games\\Main\\helper.exe"],"category":"game","sessionMinutes":45,"isConfigured":true}],"activeSiteRules":[{"domain":"youtube.com"}],"pendingAppRules":null,"pendingSiteRules":null,"pendingEffectiveNightDate":null,"pendingSavedAtUtc":null},"appliesImmediately":true,"appliesTonight":true}}}
        """.Replace("$REQUEST$", requestId, StringComparison.Ordinal);

    private static string StagedRuleSettingsResponse(string requestId) => """
        {"version":1,"type":"saveRuleSettingsResult","requestId":"$REQUEST$","payload":{"status":"success","data":{"saved":true,"rules":{"activeAppRules":[],"activeSiteRules":[],"pendingAppRules":[{"id":"game-main","rootExecutablePath":"C:\\Games\\Main\\game.exe","helperExecutablePaths":["C:\\Games\\Main\\helper.exe"],"category":"game","sessionMinutes":45,"isConfigured":true}],"pendingSiteRules":[{"domain":"youtube.com"}],"pendingEffectiveNightDate":"2026-07-14","pendingSavedAtUtc":"2026-07-14T12:00:00Z"},"appliesImmediately":false,"appliesTonight":true,"effectiveNight":"2026-07-14"}}}
        """.Replace("$REQUEST$", requestId, StringComparison.Ordinal);

    private static string NoticeResponse(string requestId) => """
        {"version":1,"type":"claimDueNoticeResult","requestId":"$REQUEST$","payload":{"status":"success","data":{"claimed":true,"kind":"grace10","nightDate":"2026-07-14"}}}
        """.Replace("$REQUEST$", requestId, StringComparison.Ordinal);

    private static string IPhoneProgressionResponse(string requestId) => """
        {"version":1,"type":"confirmIPhoneStepResult","requestId":"$REQUEST$","payload":{"status":"success","data":{"accepted":true,"pendingStep":2,"effectiveNightDate":"2026-07-15"}}}
        """.Replace("$REQUEST$", requestId, StringComparison.Ordinal);

    private static string ClearHistoryResponse(string requestId) => """
        {"version":1,"type":"clearHistoryResult","requestId":"$REQUEST$","payload":{"status":"success","data":{"cleared":true}}}
        """.Replace("$REQUEST$", requestId, StringComparison.Ordinal);

    private sealed class RecordingTransport(params string[] responses) : INightGatePipeTransport
    {
        private readonly Queue<string> _responses = new(responses);

        public List<ReadOnlyMemory<byte>> Requests { get; } = [];

        public ValueTask<ReadOnlyMemory<byte>> ExchangeAsync(
            ReadOnlyMemory<byte> requestUtf8,
            CancellationToken cancellationToken = default)
        {
            Requests.Add(requestUtf8.ToArray());
            return ValueTask.FromResult<ReadOnlyMemory<byte>>(
                Encoding.UTF8.GetBytes(_responses.Dequeue()));
        }
    }

    private sealed class UserExperienceRequestIds(params string[] values) : IProtocolRequestIdSource
    {
        private readonly Queue<string> _values = new(values);

        public string NextRequestId() => _values.Dequeue();
    }
}
