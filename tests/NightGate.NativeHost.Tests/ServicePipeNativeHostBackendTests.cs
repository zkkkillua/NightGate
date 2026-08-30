using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using NightGate.Core;
using NightGate.NativeHost;
using NightGate.Service;

namespace NightGate.NativeHost.Tests;

public sealed class ServicePipeNativeHostBackendTests
{
    [Fact]
    public async Task GetPolicy_WritesExactEnvelopeAndProjectsVideoRule()
    {
        ScriptedExchange exchange = new(request => PolicyResponse(RequestId(request)));
        ServicePipeNativeHostBackend backend = Backend(exchange, "policy-1");

        ChromePolicyPayload result = await backend.GetPolicyAsync();

        JsonElement request = Parse(Assert.Single(exchange.Requests));
        Assert.Equal(4, request.EnumerateObject().Count());
        Assert.Equal("getPolicy", request.GetProperty("type").GetString());
        Assert.Equal("policy-1", request.GetProperty("requestId").GetString());
        Assert.Empty(request.GetProperty("payload").EnumerateObject());
        Assert.Equal("grandfatherOneMedia", result.Mode);
        ChromeSiteRulePayload rule = Assert.Single(result.SiteRules);
        Assert.Equal("youtube.com", rule.Domain);
        Assert.Equal("video", rule.Category);
    }

    [Fact]
    public async Task GetPolicy_AcceptsCurrentServiceProtocolSerialization()
    {
        ScriptedExchange exchange = new(request =>
            CurrentServicePolicyResponse(RequestId(request)));
        ServicePipeNativeHostBackend backend = Backend(exchange, "service-contract");

        ChromePolicyPayload result = await backend.GetPolicyAsync();

        Assert.Equal(42, result.Revision);
        Assert.Equal("2026-07-14T15:30:00.000Z", result.EvaluatedAtUtc);
        Assert.Equal("grandfatherOneMedia", result.Mode);
    }

    [Fact]
    public async Task GetPolicy_ProjectsCanonicalServiceRulesWithCategoriesAndStableRuleIds()
    {
        ScriptedExchange exchange = new(request => PolicyResponse(
            RequestId(request),
            "reddit.com",
            "steampowered.com",
            "xn--fa-hia.de",
            "youtube.com"));
        ServicePipeNativeHostBackend backend = Backend(exchange, "policy-1", "policy-2");

        ChromePolicyPayload first = await backend.GetPolicyAsync();
        ChromePolicyPayload second = await backend.GetPolicyAsync();

        Assert.NotEmpty(first.SiteRules);
        Assert.Equal(
            first.SiteRules.Select(rule => rule.RuleId),
            second.SiteRules.Select(rule => rule.RuleId));
        Assert.Collection(
            first.SiteRules,
            rule => AssertRule(rule, "reddit.com", "social"),
            rule => AssertRule(rule, "steampowered.com", "gaming"),
            rule => AssertRule(rule, "xn--fa-hia.de", "other"),
            rule => AssertRule(rule, "youtube.com", "video"));
    }

    [Theory]
    [InlineData("fa\u00df.de")]
    [InlineData("\u03bf\u03b4\u03cc\u03c2.gr")]
    [InlineData("EXAMPLE.com")]
    [InlineData("example.com.")]
    [InlineData("xn--a.com")]
    [InlineData("127.1")]
    [InlineData("127.0.1")]
    [InlineData("example.123")]
    [InlineData("999.999")]
    [InlineData("example.0x10")]
    [InlineData("999.0X10")]
    [InlineData("example.0x")]
    [InlineData("999.0X")]
    [InlineData("intranet")]
    [InlineData("localhost")]
    public async Task GetPolicy_RejectsDomainThatCanonicalServiceCannotPublish(string domain)
    {
        ScriptedExchange exchange = new(request => PolicyResponse(RequestId(request), domain));

        await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await Backend(exchange, "invalid-domain").GetPolicyAsync());
    }

    [Fact]
    public async Task GetPolicy_RejectsServiceRulesOutsideStrictOrdinalOrder()
    {
        ScriptedExchange exchange = new(request => PolicyResponse(
            RequestId(request),
            "z.example.com",
            "a.example.com"));

        await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await Backend(exchange, "invalid-order").GetPolicyAsync());
    }

    [Fact]
    public async Task GetPolicy_DegradedServiceReturnsExplicitFailOpen()
    {
        ScriptedExchange exchange = new(request => DegradedResponse(RequestId(request)));

        ChromePolicyPayload result = await Backend(exchange, "degraded").GetPolicyAsync();

        Assert.Equal("failOpen", result.Mode);
        Assert.Empty(result.SiteRules);
        Assert.Equal(0, result.Revision);
    }

    [Theory]
    [InlineData("wrongVersion")]
    [InlineData("wrongType")]
    [InlineData("wrongRequestId")]
    [InlineData("extraField")]
    [InlineData("duplicateField")]
    [InlineData("legacyPolicyWithoutRevision")]
    [InlineData("malformedPolicy")]
    [InlineData("oversized")]
    public async Task GetPolicy_RejectsUntrustedServiceResponse(string mutation)
    {
        ScriptedExchange exchange = new(request => Mutate(
            PolicyResponse(RequestId(request)), mutation));

        await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await Backend(exchange, "strict").GetPolicyAsync());
    }

    [Fact]
    public async Task RecordEvent_DropsRuleIdBeforeServiceBoundary()
    {
        ScriptedExchange exchange = new(request => EventResponse(RequestId(request), "recorded"));
        BrowserPrivacyEvent privacyEvent = new(
            DateTimeOffset.Parse("2026-07-14T15:29:00Z"),
            BrowserPrivacyEventType.NavigationBlocked,
            "rule-never-forwarded",
            BrowserSiteCategory.Social);

        Assert.True(await Backend(exchange, "event-1").RecordEventAsync(privacyEvent));

        string raw = Encoding.UTF8.GetString(Assert.Single(exchange.Requests).Span);
        Assert.DoesNotContain("rule-never-forwarded", raw, StringComparison.Ordinal);
        Assert.DoesNotContain("ruleId", raw, StringComparison.OrdinalIgnoreCase);
        JsonElement request = Parse(Encoding.UTF8.GetBytes(raw));
        Assert.Equal("recordBrowserEvent", request.GetProperty("type").GetString());
        JsonElement payload = request.GetProperty("payload");
        Assert.Equal(3, payload.EnumerateObject().Count());
        Assert.Equal("2026-07-14T15:29:00.000Z", payload.GetProperty("timestamp").GetString());
        Assert.Equal("navigationBlocked", payload.GetProperty("eventType").GetString());
        Assert.Equal("social", payload.GetProperty("category").GetString());
    }

    [Fact]
    public async Task RecordEvent_DegradedAcknowledgementIsFalse()
    {
        ScriptedExchange exchange = new(request => EventResponse(
            RequestId(request), "degraded", "degraded"));
        BrowserPrivacyEvent privacyEvent = new(
            DateTimeOffset.Parse("2026-07-14T15:29:00Z"),
            BrowserPrivacyEventType.MediaEnded,
            "local-only",
            BrowserSiteCategory.Video);

        Assert.False(await Backend(exchange, "event-2").RecordEventAsync(privacyEvent));
    }

    [Fact]
    public async Task Heartbeat_RefreshesPolicyThenRecordsSanitizedReadyHealthForTheExactRevision()
    {
        ScriptedExchange exchange = new(request =>
            RequestType(request) == "recordChromeHealth"
                ? HealthResponse(RequestId(request), "recorded")
                : PolicyResponse(RequestId(request)));
        ServicePipeNativeHostBackend backend = Backend(exchange, "policy-1", "health-1");
        long revision = Now.ToUnixTimeMilliseconds();

        Assert.True(await backend.HeartbeatAsync(Heartbeat(revision)));

        Assert.Equal(2, exchange.Requests.Count);
        Assert.Equal("getPolicy", RequestType(exchange.Requests[0]));
        string rawHealthRequest = Encoding.UTF8.GetString(exchange.Requests[1].Span);
        Assert.DoesNotContain(
            "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA",
            rawHealthRequest,
            StringComparison.Ordinal);
        JsonElement healthRequest = Parse(exchange.Requests[1]);
        Assert.Equal(4, healthRequest.EnumerateObject().Count());
        Assert.Equal("recordChromeHealth", healthRequest.GetProperty("type").GetString());
        Assert.Equal("health-1", healthRequest.GetProperty("requestId").GetString());
        JsonElement payload = healthRequest.GetProperty("payload");
        Assert.Equal(6, payload.EnumerateObject().Count());
        Assert.Equal(
            "eefgemhlhbdodhlgjmicnoifhclhdgmm",
            payload.GetProperty("extensionId").GetString());
        Assert.Equal("1.2.3", payload.GetProperty("extensionVersion").GetString());
        Assert.Equal(
            "0f007385b6f9d4b7eeb2748605afe1a984a0a3bfa3f014d09e2a784ce9e5cd1a",
            payload.GetProperty("profileTokenSha256").GetString());
        Assert.Equal(revision, payload.GetProperty("policyRevision").GetInt64());
        Assert.True(payload.GetProperty("incognitoAllowed").GetBoolean());
        Assert.True(payload.GetProperty("protectionReady").GetBoolean());
    }

    [Fact]
    public async Task Heartbeat_RevisionMinusOneRecordsNotReadyAgainstCurrentPolicy()
    {
        ScriptedExchange exchange = new(request =>
            RequestType(request) == "getPolicy"
                ? PolicyResponse(RequestId(request))
                : HealthResponse(RequestId(request), "recorded"));

        Assert.True(await Backend(exchange, "policy-1", "health-1").HeartbeatAsync(
            Heartbeat(-1, protectionReady: false)));

        Assert.Equal(
            ["getPolicy", "recordChromeHealth"],
            exchange.Requests.Select(RequestType));
        JsonElement payload = Parse(exchange.Requests[1]).GetProperty("payload");
        Assert.Equal(Now.ToUnixTimeMilliseconds(), payload.GetProperty("policyRevision").GetInt64());
        Assert.False(payload.GetProperty("protectionReady").GetBoolean());
    }

    [Fact]
    public async Task Heartbeat_DegradedHealthAcknowledgementReturnsFalseAfterPolicyCheck()
    {
        ScriptedExchange exchange = new(request =>
            RequestType(request) == "getPolicy"
                ? PolicyResponse(RequestId(request))
                : HealthResponse(RequestId(request), "degraded", "degraded"));

        Assert.False(await Backend(exchange, "policy-1", "health-1").HeartbeatAsync(
            Heartbeat(Now.ToUnixTimeMilliseconds())));

        Assert.Equal(
            ["getPolicy", "recordChromeHealth"],
            exchange.Requests.Select(RequestType));
    }

    [Fact]
    public async Task Heartbeat_RecordedHealthRejectsBackwardPolicyRevision()
    {
        ScriptedExchange exchange = new(request =>
            RequestType(request) == "recordChromeHealth"
                ? HealthResponse(RequestId(request), "recorded")
                : PolicyResponse(RequestId(request)));

        Assert.False(await Backend(exchange, "policy-1", "health-1").HeartbeatAsync(
            Heartbeat(Now.ToUnixTimeMilliseconds() + 1)));

        JsonElement payload = Parse(exchange.Requests[1]).GetProperty("payload");
        Assert.False(payload.GetProperty("protectionReady").GetBoolean());
    }

    [Fact]
    public async Task Heartbeat_CurrentPolicyNewerThanAppliedRevisionRecordsNotReadyAndRejects()
    {
        ScriptedExchange exchange = new(request =>
            RequestType(request) == "getPolicy"
                ? PolicyResponse(RequestId(request))
                : HealthResponse(RequestId(request), "recorded"));

        Assert.False(await Backend(exchange, "policy-1", "health-1").HeartbeatAsync(
            Heartbeat(Now.ToUnixTimeMilliseconds() - 1)));

        JsonElement payload = Parse(exchange.Requests[1]).GetProperty("payload");
        Assert.False(payload.GetProperty("protectionReady").GetBoolean());
    }

    [Fact]
    public async Task Heartbeat_ExplicitlyDegradedExactRevisionIsRecordedAndAccepted()
    {
        ScriptedExchange exchange = new(request =>
            RequestType(request) == "getPolicy"
                ? PolicyResponse(RequestId(request))
                : HealthResponse(RequestId(request), "recorded"));

        Assert.True(await Backend(exchange, "policy-1", "health-1").HeartbeatAsync(
            Heartbeat(Now.ToUnixTimeMilliseconds(), protectionReady: false)));

        JsonElement payload = Parse(exchange.Requests[1]).GetProperty("payload");
        Assert.False(payload.GetProperty("protectionReady").GetBoolean());
    }

    [Theory]
    [InlineData("wrongType")]
    [InlineData("wrongRequestId")]
    [InlineData("extraData")]
    [InlineData("duplicateData")]
    [InlineData("invalidDataStatus")]
    [InlineData("inconsistentStatus")]
    public async Task Heartbeat_RejectsUntrustedHealthResponse(string mutation)
    {
        ScriptedExchange exchange = new(request =>
            RequestType(request) == "getPolicy"
                ? PolicyResponse(RequestId(request))
                : MutateHealth(
                    HealthResponse(RequestId(request), "recorded"),
                    mutation));

        await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await Backend(exchange, "policy-strict", "health-strict").HeartbeatAsync(
                Heartbeat(Now.ToUnixTimeMilliseconds())));
    }

    private static readonly DateTimeOffset Now =
        DateTimeOffset.Parse("2026-07-14T15:30:00Z");

    private static ServicePipeNativeHostBackend Backend(
        IServicePipeExchange exchange,
        params string[] requestIds) => new(
            exchange,
            new FixedRequestIds(requestIds),
            new FixedClock(Now));

    private static JsonElement Parse(ReadOnlyMemory<byte> json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static string RequestId(ReadOnlyMemory<byte> request) =>
        Parse(request).GetProperty("requestId").GetString()!;

    private static string RequestType(ReadOnlyMemory<byte> request) =>
        Parse(request).GetProperty("type").GetString()!;

    private static NativeHeartbeatObservation Heartbeat(
        long revision,
        bool protectionReady = true) => new(
        "eefgemhlhbdodhlgjmicnoifhclhdgmm",
        "1.2.3",
        "0f007385b6f9d4b7eeb2748605afe1a984a0a3bfa3f014d09e2a784ce9e5cd1a",
        revision,
        true,
        protectionReady);

    private static ReadOnlyMemory<byte> PolicyResponse(
        string requestId,
        params string[] domains)
    {
        if (domains.Length == 0)
        {
            domains = ["youtube.com"];
        }
        string siteRules = JsonSerializer.Serialize(
            domains.Select(domain => new { domain }));
        string value =
            "{\"version\":1,\"type\":\"getPolicyResult\",\"requestId\":\"__ID__\",\"payload\":{\"status\":\"success\",\"data\":{"
            + "\"enforcementEnabled\":true,\"isDegraded\":false,\"degradationCode\":null,\"policy\":{"
            + "\"revision\":1784043000000,\"evaluatedAt\":\"2026-07-14T15:30:00+00:00\",\"phase\":\"grace\","
            + "\"window\":{\"nightDate\":\"2026-07-14\",\"protectedStart\":\"2026-07-14T13:00:00+00:00\",\"lastStart\":\"2026-07-14T16:05:00+00:00\",\"lock\":\"2026-07-14T16:40:00+00:00\",\"lightsOut\":\"2026-07-14T17:00:00+00:00\",\"wake\":\"2026-07-15T01:00:00+00:00\"},"
            + "\"appRules\":[],\"siteRules\":" + siteRules
            + ",\"enforcementEnabled\":true,\"isDegraded\":false,\"activeOverride\":null}}}}";
        return Encoding.UTF8.GetBytes(value.Replace("__ID__", requestId, StringComparison.Ordinal));
    }

    private static ReadOnlyMemory<byte> CurrentServicePolicyResponse(string requestId)
    {
        NightWindow window = new(
            new DateOnly(2026, 7, 14),
            new DateTimeOffset(2026, 7, 14, 13, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 7, 14, 16, 5, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 7, 14, 16, 40, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 7, 14, 17, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 7, 15, 1, 0, 0, TimeSpan.Zero));
        ServiceRuntimeStatus status = new(
            true,
            false,
            null,
            new PolicySnapshot(
                Now,
                NightPhase.Grace,
                window,
                ImmutableArray<AppRule>.Empty,
                ImmutableArray<SiteRule>.Empty)
            {
                Revision = 42,
            });
        string data = ProtocolCommandResult.Success(status).Payload.GetRawText();
        return Encoding.UTF8.GetBytes(
            "{\"version\":1,\"type\":\"getPolicyResult\",\"requestId\":\""
            + requestId
            + "\",\"payload\":{\"status\":\"success\",\"data\":"
            + data
            + "}}");
    }

    private static void AssertRule(
        ChromeSiteRulePayload rule,
        string domain,
        string category)
    {
        Assert.Equal(domain, rule.Domain);
        Assert.Equal(category, rule.Category);
        Assert.Matches("^site-[0-9a-f]{16}$", rule.RuleId);
    }

    private static ReadOnlyMemory<byte> DegradedResponse(string requestId) =>
        Encoding.UTF8.GetBytes(
            "{\"version\":1,\"type\":\"getPolicyResult\",\"requestId\":\"" + requestId
            + "\",\"payload\":{\"status\":\"degraded\",\"data\":{\"enforcementEnabled\":false,\"isDegraded\":true,\"degradationCode\":\"storage-unavailable\",\"policy\":null}}}");

    private static ReadOnlyMemory<byte> EventResponse(
        string requestId,
        string dataStatus,
        string outerStatus = "success") => Encoding.UTF8.GetBytes(
            "{\"version\":1,\"type\":\"recordBrowserEventResult\",\"requestId\":\""
            + requestId + "\",\"payload\":{\"status\":\"" + outerStatus
            + "\",\"data\":{\"status\":\"" + dataStatus + "\"}}}");

    private static ReadOnlyMemory<byte> HealthResponse(
        string requestId,
        string dataStatus,
        string outerStatus = "success") => Encoding.UTF8.GetBytes(
            "{\"version\":1,\"type\":\"recordChromeHealthResult\",\"requestId\":\""
            + requestId + "\",\"payload\":{\"status\":\"" + outerStatus
            + "\",\"data\":{\"status\":\"" + dataStatus + "\"}}}");

    private static ReadOnlyMemory<byte> MutateHealth(
        ReadOnlyMemory<byte> source,
        string mutation)
    {
        string valid = Encoding.UTF8.GetString(source.Span);
        return mutation switch
        {
            "wrongType" => Encoding.UTF8.GetBytes(valid.Replace(
                "recordChromeHealthResult",
                "recordBrowserEventResult",
                StringComparison.Ordinal)),
            "wrongRequestId" => Encoding.UTF8.GetBytes(valid.Replace(
                "health-strict",
                "someone-else",
                StringComparison.Ordinal)),
            "extraData" => Encoding.UTF8.GetBytes(valid.Replace(
                "{\"status\":\"recorded\"}",
                "{\"status\":\"recorded\",\"extra\":true}",
                StringComparison.Ordinal)),
            "duplicateData" => Encoding.UTF8.GetBytes(valid.Replace(
                "{\"status\":\"recorded\"}",
                "{\"status\":\"recorded\",\"status\":\"recorded\"}",
                StringComparison.Ordinal)),
            "invalidDataStatus" => Encoding.UTF8.GetBytes(valid.Replace(
                "\"recorded\"",
                "\"ignored\"",
                StringComparison.Ordinal)),
            "inconsistentStatus" => Encoding.UTF8.GetBytes(valid.Replace(
                "\"status\":\"success\"",
                "\"status\":\"degraded\"",
                StringComparison.Ordinal)),
            _ => throw new ArgumentOutOfRangeException(nameof(mutation)),
        };
    }

    private static ReadOnlyMemory<byte> Mutate(ReadOnlyMemory<byte> source, string mutation)
    {
        string valid = Encoding.UTF8.GetString(source.Span);
        return mutation switch
        {
            "wrongVersion" => Encoding.UTF8.GetBytes(valid.Replace("\"version\":1", "\"version\":2", StringComparison.Ordinal)),
            "wrongType" => Encoding.UTF8.GetBytes(valid.Replace("getPolicyResult", "getStatusResult", StringComparison.Ordinal)),
            "wrongRequestId" => Encoding.UTF8.GetBytes(valid.Replace("strict", "someone-else", StringComparison.Ordinal)),
            "extraField" => Encoding.UTF8.GetBytes(valid.Replace("\"payload\":", "\"extra\":true,\"payload\":", StringComparison.Ordinal)),
            "duplicateField" => Encoding.UTF8.GetBytes(valid.Replace("\"version\":1", "\"version\":1,\"version\":1", StringComparison.Ordinal)),
            "legacyPolicyWithoutRevision" => Encoding.UTF8.GetBytes(valid.Replace(
                "\"revision\":1784043000000,",
                string.Empty,
                StringComparison.Ordinal)),
            "malformedPolicy" => Encoding.UTF8.GetBytes(valid.Replace("\"phase\":\"grace\"", "\"phase\":\"unknown\"", StringComparison.Ordinal)),
            "oversized" => new byte[ChromeNativeMessageFraming.MaximumBodyBytes + 1],
            _ => throw new ArgumentOutOfRangeException(nameof(mutation)),
        };
    }

    private sealed class ScriptedExchange(
        Func<ReadOnlyMemory<byte>, ReadOnlyMemory<byte>> response) : IServicePipeExchange
    {
        public List<ReadOnlyMemory<byte>> Requests { get; } = [];

        public ValueTask<ReadOnlyMemory<byte>> ExchangeAsync(
            ReadOnlyMemory<byte> requestUtf8,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add(requestUtf8.ToArray());
            return ValueTask.FromResult(response(requestUtf8));
        }
    }

    private sealed class FixedRequestIds(params string[] values) :
        INativeHostServiceRequestIdSource
    {
        private int _index;
        public string Next() => values[_index++];
    }

    private sealed class FixedClock(DateTimeOffset value) : INativeHostClock
    {
        public DateTimeOffset UtcNow => value;
    }
}
