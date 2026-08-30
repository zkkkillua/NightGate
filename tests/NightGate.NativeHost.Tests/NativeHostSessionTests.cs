using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using NightGate.NativeHost;

namespace NightGate.NativeHost.Tests;

public sealed class NativeHostSessionTests
{
    private const string Token = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";

    [Fact]
    public async Task RunAsync_ProjectsPolicyAndWritesOneCorrelatedFrame()
    {
        FakeBackend backend = new() { Policy = Policy() };
        MemoryStream output = new();

        NativeHostExitCode exitCode = await NativeHostSession.RunAsync(
            Input(Envelope("getPolicy", "policy-1", "{}")),
            output,
            backend);

        Assert.Equal(NativeHostExitCode.Success, exitCode);
        Assert.Equal(1, backend.PolicyCalls);
        JsonElement response = ReadSingleResponse(output);
        Assert.Equal("getPolicyResult", response.GetProperty("type").GetString());
        Assert.Equal("policy-1", response.GetProperty("requestId").GetString());
        Assert.Equal(Token, response.GetProperty("profileToken").GetString());
        Assert.Equal("grandfatherOneMedia", response.GetProperty("payload").GetProperty("mode").GetString());
    }

    [Fact]
    public async Task RunAsync_ForwardsHeartbeatAndPrivacyEventAsTypedValuesOnly()
    {
        string heartbeat = Envelope(
            "heartbeat",
            "heartbeat-1",
            HeartbeatPayload(7, "1.2.3", incognitoAllowed: true));
        string media = Envelope(
            "mediaState",
            "media-1",
            "{\"timestamp\":\"2026-07-14T15:30:00.000Z\",\"eventType\":\"mediaPaused\",\"ruleId\":\"video-rule\",\"category\":\"video\"}");
        FakeBackend backend = new();
        MemoryStream output = new();

        NativeHostExitCode exitCode = await NativeHostSession.RunAsync(
            Input(heartbeat, media),
            output,
            backend);

        Assert.Equal(NativeHostExitCode.Success, exitCode);
        NativeHeartbeatObservation observation = Assert.Single(backend.Heartbeats);
        Assert.Equal("eefgemhlhbdodhlgjmicnoifhclhdgmm", observation.ExtensionId);
        Assert.Equal("1.2.3", observation.ExtensionVersion);
        Assert.Equal(
            "0f007385b6f9d4b7eeb2748605afe1a984a0a3bfa3f014d09e2a784ce9e5cd1a",
            observation.ProfileTokenSha256);
        Assert.Equal(7, observation.PolicyRevision);
        Assert.True(observation.IncognitoAllowed);
        Assert.True(observation.ProtectionReady);
        Assert.DoesNotContain(
            typeof(NativeHeartbeatObservation).GetProperties(),
            property => string.Equals(
                property.Name,
                "ProfileToken",
                StringComparison.OrdinalIgnoreCase));
        BrowserPrivacyEvent privacyEvent = Assert.Single(backend.Events);
        Assert.Equal(BrowserPrivacyEventType.MediaPaused, privacyEvent.EventType);
        Assert.Equal("video-rule", privacyEvent.RuleId);
        Assert.Equal(2, CountFrames(output));
    }

    [Theory]
    [InlineData("not-json")]
    [InlineData("{\"version\":1}")]
    public async Task RunAsync_MalformedInputExitsFailOpenWithoutProtocolOutput(string message)
    {
        MemoryStream output = new();

        NativeHostExitCode exitCode = await NativeHostSession.RunAsync(
            Input(message),
            output,
            new FakeBackend());

        Assert.Equal(NativeHostExitCode.InvalidInput, exitCode);
        Assert.Equal(0, output.Length);
    }

    [Fact]
    public async Task RunAsync_BackendFailureExitsFailOpenWithoutInventingPolicy()
    {
        MemoryStream output = new();
        FakeBackend backend = new() { Failure = new IOException("service unavailable") };

        NativeHostExitCode exitCode = await NativeHostSession.RunAsync(
            Input(Envelope("getPolicy", "policy-1", "{}")),
            output,
            backend);

        Assert.Equal(NativeHostExitCode.BackendUnavailable, exitCode);
        Assert.Equal(0, output.Length);
    }

    [Fact]
    public async Task RunAsync_ProfileChangeOrDuplicateRequestIdEndsTheConnection()
    {
        string first = Envelope("heartbeat", "same-id", HeartbeatPayload(1));
        string duplicate = Envelope("heartbeat", "same-id", HeartbeatPayload(2));
        string otherToken = Envelope(
            "heartbeat",
            "new-id",
            HeartbeatPayload(3),
            "BBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB");

        foreach (string second in new[] { duplicate, otherToken })
        {
            FakeBackend backend = new();
            MemoryStream output = new();
            NativeHostExitCode exitCode = await NativeHostSession.RunAsync(
                Input(first, second),
                output,
                backend);

            Assert.Equal(NativeHostExitCode.InvalidInput, exitCode);
            Assert.Equal([1], backend.Heartbeats.Select(item => item.PolicyRevision));
            Assert.Equal(1, CountFrames(output));
        }
    }

    [Fact]
    public async Task RunAsync_CallerCancellationPropagatesAndWritesNothing()
    {
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();
        MemoryStream output = new();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await NativeHostSession.RunAsync(
                Input(Envelope("getPolicy", "policy-1", "{}")),
                output,
                new FakeBackend(),
                cancellation.Token));

        Assert.Equal(0, output.Length);
    }

    private static ChromePolicyPayload Policy() => ChromePolicyProjector.Project(new(
        true,
        false,
        DateTimeOffset.Parse("2026-07-14T15:30:00Z").ToUnixTimeMilliseconds(),
        DateTimeOffset.Parse("2026-07-14T15:30:00Z"),
        "grace",
        new DateOnly(2026, 7, 14),
        DateTimeOffset.Parse("2026-07-14T16:05:00Z"),
        DateTimeOffset.Parse("2026-07-14T16:40:00Z"),
        DateTimeOffset.Parse("2026-07-15T01:00:00Z"),
        null,
        [new("video.example", BrowserSiteCategory.Video)]));

    private static MemoryStream Input(params string[] messages)
    {
        MemoryStream stream = new();
        foreach (string message in messages)
        {
            byte[] body = Encoding.UTF8.GetBytes(message);
            byte[] prefix = new byte[4];
            BinaryPrimitives.WriteInt32LittleEndian(prefix, body.Length);
            stream.Write(prefix);
            stream.Write(body);
        }
        stream.Position = 0;
        return stream;
    }

    private static string Envelope(
        string type,
        string requestId,
        string payload,
        string token = Token) =>
        "{\"version\":1,\"type\":\"" + type + "\",\"requestId\":\"" + requestId
        + "\",\"profileToken\":\"" + token + "\",\"payload\":" + payload + "}";

    private static string HeartbeatPayload(
        long revision,
        string extensionVersion = "1.0",
        bool incognitoAllowed = false,
        bool protectionReady = true) => JsonSerializer.Serialize(new
        {
            revision,
            extensionVersion,
            incognitoAllowed,
            protectionReady,
        });

    private static JsonElement ReadSingleResponse(MemoryStream output)
    {
        Assert.Equal(1, CountFrames(output));
        output.Position = 0;
        byte[] prefix = new byte[4];
        output.ReadExactly(prefix);
        int length = BinaryPrimitives.ReadInt32LittleEndian(prefix);
        byte[] body = new byte[length];
        output.ReadExactly(body);
        using JsonDocument document = JsonDocument.Parse(body);
        return document.RootElement.Clone();
    }

    private static int CountFrames(MemoryStream output)
    {
        byte[] data = output.ToArray();
        int offset = 0;
        int count = 0;
        while (offset < data.Length)
        {
            Assert.True(data.Length - offset >= 4);
            int length = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(offset, 4));
            offset += 4;
            Assert.InRange(length, 0, ChromeNativeMessageFraming.MaximumBodyBytes);
            Assert.True(data.Length - offset >= length);
            offset += length;
            count++;
        }
        return count;
    }

    private sealed class FakeBackend : INativeHostBackend
    {
        public ChromePolicyPayload Policy { get; set; } = Policy();
        public Exception? Failure { get; set; }
        public int PolicyCalls { get; private set; }
        public List<NativeHeartbeatObservation> Heartbeats { get; } = [];
        public List<BrowserPrivacyEvent> Events { get; } = [];

        public ValueTask<ChromePolicyPayload> GetPolicyAsync(
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Failure is not null) throw Failure;
            PolicyCalls++;
            return ValueTask.FromResult(Policy);
        }

        public ValueTask<bool> HeartbeatAsync(
            NativeHeartbeatObservation observation,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Failure is not null) throw Failure;
            Heartbeats.Add(observation);
            return ValueTask.FromResult(true);
        }

        public ValueTask<bool> RecordEventAsync(
            BrowserPrivacyEvent privacyEvent,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Failure is not null) throw Failure;
            Events.Add(privacyEvent);
            return ValueTask.FromResult(true);
        }
    }
}
