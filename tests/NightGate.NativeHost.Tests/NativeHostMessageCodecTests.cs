using System.Text;
using System.Text.Json;
using NightGate.NativeHost;

namespace NightGate.NativeHost.Tests;

public sealed class NativeHostMessageCodecTests
{
    private const string Token = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";

    [Theory]
    [InlineData("getPolicy", "{}", "GetPolicy")]
    [InlineData("heartbeat", "{\"revision\":7,\"extensionVersion\":\"1.2.3.4\",\"incognitoAllowed\":true,\"protectionReady\":true}", "Heartbeat")]
    [InlineData("mediaState", "{\"timestamp\":\"2026-07-14T15:30:00.000Z\",\"eventType\":\"mediaPlaying\",\"ruleId\":\"video-rule\",\"category\":\"video\"}", "MediaState")]
    [InlineData("navigationAttempt", "{\"timestamp\":\"2026-07-14T15:30:00Z\",\"eventType\":\"navigationBlocked\",\"ruleId\":\"social-rule\",\"category\":\"social\"}", "NavigationAttempt")]
    public void TryDecode_AcceptsOnlyTheFourTypedRequests(
        string type,
        string payload,
        string expectedKind)
    {
        string json = Envelope(type, payload);

        bool decoded = NativeHostMessageCodec.TryDecode(
            Encoding.UTF8.GetBytes(json),
            out NativeHostRequest? request);

        Assert.True(decoded);
        Assert.NotNull(request);
        Assert.Equal(expectedKind, request.Kind.ToString());
        Assert.Equal("request-1", request.RequestId);
        Assert.Equal(Token, request.ProfileToken);
        if (expectedKind == "Heartbeat")
        {
            NativeHeartbeatPayload heartbeat = Assert.IsType<NativeHeartbeatPayload>(
                request.Heartbeat);
            Assert.Equal(7, heartbeat.Revision);
            Assert.Equal("1.2.3.4", heartbeat.ExtensionVersion);
            Assert.True(heartbeat.IncognitoAllowed);
            Assert.True(heartbeat.ProtectionReady);
        }
        else
        {
            Assert.Null(request.Heartbeat);
        }
        Assert.Equal(
            expectedKind is "MediaState" or "NavigationAttempt",
            request.PrivacyEvent is not null);
    }

    [Theory]
    [InlineData("requestOverride")]
    [InlineData("history")]
    [InlineData("GetPolicy")]
    [InlineData("")]
    public void TryDecode_RejectsAnyCommandOutsideTheWhitelist(string type)
    {
        Assert.False(NativeHostMessageCodec.TryDecode(
            Encoding.UTF8.GetBytes(Envelope(type, "{}")),
            out _));
    }

    [Theory]
    [InlineData("{\"version\":2,\"type\":\"getPolicy\",\"requestId\":\"request-1\",\"profileToken\":\"AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA\",\"payload\":{}}")]
    [InlineData("{\"version\":1,\"type\":\"getPolicy\",\"requestId\":\"request-1\",\"profileToken\":\"AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA\",\"payload\":{},\"extra\":true}")]
    [InlineData("{\"version\":1,\"type\":\"getPolicy\",\"requestId\":\"request-1\",\"requestId\":\"request-2\",\"profileToken\":\"AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA\",\"payload\":{}}")]
    [InlineData("{\"version\":1,\"type\":\"getPolicy\",\"requestId\":\"\n\",\"profileToken\":\"AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA\",\"payload\":{}}")]
    [InlineData("{\"version\":1,\"type\":\"getPolicy\",\"requestId\":\"request-1\",\"profileToken\":\"short\",\"payload\":{}}")]
    [InlineData("[]")]
    [InlineData("not json")]
    public void TryDecode_RejectsWrongEnvelopeShapeCorrelationOrJson(string json)
    {
        Assert.False(NativeHostMessageCodec.TryDecode(Encoding.UTF8.GetBytes(json), out _));
    }

    [Theory]
    [InlineData("getPolicy", "{\"extra\":true}")]
    [InlineData("heartbeat", "{}")]
    [InlineData("heartbeat", "{\"revision\":-2,\"extensionVersion\":\"1\",\"incognitoAllowed\":false,\"protectionReady\":false}")]
    [InlineData("heartbeat", "{\"revision\":9007199254740992,\"extensionVersion\":\"1\",\"incognitoAllowed\":false,\"protectionReady\":false}")]
    [InlineData("heartbeat", "{\"revision\":7,\"extensionVersion\":\"1\",\"incognitoAllowed\":false,\"protectionReady\":false,\"extra\":true}")]
    [InlineData("heartbeat", "{\"revision\":7,\"extensionVersion\":\"\",\"incognitoAllowed\":false,\"protectionReady\":false}")]
    [InlineData("heartbeat", "{\"revision\":7,\"extensionVersion\":\"1.2.3.4.5\",\"incognitoAllowed\":false,\"protectionReady\":false}")]
    [InlineData("heartbeat", "{\"revision\":7,\"extensionVersion\":\"1..2\",\"incognitoAllowed\":false,\"protectionReady\":false}")]
    [InlineData("heartbeat", "{\"revision\":7,\"extensionVersion\":\"65536\",\"incognitoAllowed\":false,\"protectionReady\":false}")]
    [InlineData("heartbeat", "{\"revision\":7,\"extensionVersion\":\"1a\",\"incognitoAllowed\":false,\"protectionReady\":false}")]
    [InlineData("heartbeat", "{\"revision\":7,\"extensionVersion\":\"111111111111111111111111111111111\",\"incognitoAllowed\":false,\"protectionReady\":false}")]
    [InlineData("heartbeat", "{\"revision\":7,\"extensionVersion\":\"1\",\"incognitoAllowed\":\"false\",\"protectionReady\":false}")]
    [InlineData("heartbeat", "{\"revision\":7,\"extensionVersion\":\"1\",\"incognitoAllowed\":false,\"protectionReady\":\"false\"}")]
    [InlineData("mediaState", "{\"timestamp\":\"2026-07-14T15:30:00.000Z\",\"eventType\":\"navigationBlocked\",\"ruleId\":\"r\",\"category\":\"video\"}")]
    [InlineData("navigationAttempt", "{\"timestamp\":\"2026-07-14T15:30:00.000Z\",\"eventType\":\"mediaPlaying\",\"ruleId\":\"r\",\"category\":\"video\"}")]
    [InlineData("mediaState", "{\"timestamp\":\"2026-07-14T15:30:00.00Z\",\"eventType\":\"mediaPlaying\",\"ruleId\":\"r\",\"category\":\"video\"}")]
    [InlineData("mediaState", "{\"timestamp\":\"2026-07-14T15:30:00.000+00:00\",\"eventType\":\"mediaPlaying\",\"ruleId\":\"r\",\"category\":\"video\"}")]
    [InlineData("mediaState", "{\"timestamp\":\"2026-07-14T15:30:00.000Z\",\"eventType\":\"mediaPlaying\",\"ruleId\":\"r\",\"category\":\"unknown\"}")]
    [InlineData("mediaState", "{\"timestamp\":\"2026-07-14T15:30:00.000Z\",\"eventType\":\"mediaPlaying\",\"ruleId\":\"r\",\"category\":\"video\",\"url\":\"https://secret.invalid/path\"}")]
    [InlineData("mediaState", "{\"timestamp\":\"2026-07-14T15:30:00.000Z\",\"eventType\":\"mediaPlaying\",\"ruleId\":\"r\",\"category\":\"video\",\"nested\":{\"x\":1,\"x\":2}}")]
    public void TryDecode_RejectsMalformedOrPrivacyUnsafePayloads(string type, string payload)
    {
        Assert.False(NativeHostMessageCodec.TryDecode(
            Encoding.UTF8.GetBytes(Envelope(type, payload)),
            out _));
    }

    [Theory]
    [InlineData(-1, "0", false)]
    [InlineData(0, "65535", true)]
    [InlineData(9007199254740991, "0.1.65535.42", false)]
    public void TryDecode_AcceptsHeartbeatBoundaryValues(
        long revision,
        string extensionVersion,
        bool incognitoAllowed)
    {
        string payload = JsonSerializer.Serialize(new
        {
            revision,
            extensionVersion,
            incognitoAllowed,
            protectionReady = true,
        });

        Assert.True(NativeHostMessageCodec.TryDecode(
            Encoding.UTF8.GetBytes(Envelope("heartbeat", payload)),
            out NativeHostRequest? request));

        NativeHeartbeatPayload heartbeat = Assert.IsType<NativeHeartbeatPayload>(
            request!.Heartbeat);
        Assert.Equal(revision, heartbeat.Revision);
        Assert.Equal(extensionVersion, heartbeat.ExtensionVersion);
        Assert.Equal(incognitoAllowed, heartbeat.IncognitoAllowed);
        Assert.True(heartbeat.ProtectionReady);
    }

    [Fact]
    public void TryDecode_LegacyHeartbeatWithoutReadinessDefaultsToFailOpen()
    {
        string payload = "{\"revision\":7,\"extensionVersion\":\"1.2.3\",\"incognitoAllowed\":true}";

        Assert.True(NativeHostMessageCodec.TryDecode(
            Encoding.UTF8.GetBytes(Envelope("heartbeat", payload)),
            out NativeHostRequest? request));

        NativeHeartbeatPayload heartbeat = Assert.IsType<NativeHeartbeatPayload>(
            request!.Heartbeat);
        Assert.False(heartbeat.ProtectionReady);
    }

    [Fact]
    public void TryDecode_RejectsInvalidUtf8AndOversizedBodies()
    {
        Assert.False(NativeHostMessageCodec.TryDecode(new byte[] { 0xff, 0xfe }, out _));
        Assert.False(NativeHostMessageCodec.TryDecode(
            new byte[ChromeNativeMessageFraming.MaximumBodyBytes + 1],
            out _));
    }

    [Fact]
    public void DecodedPrivacyEventContainsNoUrlOrTitle()
    {
        string payload = "{\"timestamp\":\"2026-07-14T15:30:00.000Z\",\"eventType\":\"mediaEnded\",\"ruleId\":\"video-rule\",\"category\":\"video\"}";
        Assert.True(NativeHostMessageCodec.TryDecode(
            Encoding.UTF8.GetBytes(Envelope("mediaState", payload)),
            out NativeHostRequest? request));

        BrowserPrivacyEvent privacyEvent = Assert.IsType<BrowserPrivacyEvent>(request!.PrivacyEvent);
        Assert.Equal(BrowserPrivacyEventType.MediaEnded, privacyEvent.EventType);
        Assert.Equal(BrowserSiteCategory.Video, privacyEvent.Category);
        Assert.Equal("video-rule", privacyEvent.RuleId);
        Assert.Equal(DateTimeOffset.Parse("2026-07-14T15:30:00Z"), privacyEvent.TimestampUtc);
        Assert.DoesNotContain(
            typeof(BrowserPrivacyEvent).GetProperties(),
            property => property.Name.Contains("Url", StringComparison.OrdinalIgnoreCase)
                || property.Name.Contains("Title", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void EncodeAcknowledgement_EchoesOnlyCorrelationAndAcceptedFlag()
    {
        Assert.True(NativeHostMessageCodec.TryDecode(
            Encoding.UTF8.GetBytes(Envelope(
                "heartbeat",
                "{\"revision\":7,\"extensionVersion\":\"1.2.3\",\"incognitoAllowed\":false,\"protectionReady\":true}")),
            out NativeHostRequest? request));

        byte[] response = NativeHostMessageCodec.EncodeAcknowledgement(request!, accepted: true);
        using JsonDocument document = JsonDocument.Parse(response);
        JsonElement root = document.RootElement;

        Assert.Equal(5, root.EnumerateObject().Count());
        Assert.Equal(1, root.GetProperty("version").GetInt32());
        Assert.Equal("heartbeatResult", root.GetProperty("type").GetString());
        Assert.Equal("request-1", root.GetProperty("requestId").GetString());
        Assert.Equal(Token, root.GetProperty("profileToken").GetString());
        JsonElement responsePayload = root.GetProperty("payload");
        Assert.Single(responsePayload.EnumerateObject());
        Assert.True(responsePayload.GetProperty("accepted").GetBoolean());
    }

    private static string Envelope(string type, string payload) =>
        $$"""
        {"version":1,"type":"{{type}}","requestId":"request-1","profileToken":"{{Token}}","payload":{{payload}}}
        """;
}
