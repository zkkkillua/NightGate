using System.Text;
using System.Text.Json;
using NightGate.Core;
using NightGate.Service;

namespace NightGate.Service.Tests;

public sealed class BrowserEventProtocolTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 7, 12, 15, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("mediaPlaying", BrowserEventType.MediaPlaying)]
    [InlineData("mediaPaused", BrowserEventType.MediaPaused)]
    [InlineData("mediaEnded", BrowserEventType.MediaEnded)]
    [InlineData("navigationBlocked", BrowserEventType.NavigationBlocked)]
    public async Task Dispatch_RecordBrowserEventAcceptsEveryCanonicalEventType(
        string token,
        BrowserEventType expected)
    {
        StubCommandHandler handler = new();
        ServiceCommandDispatcher dispatcher = CreateDispatcher(handler);

        ProtocolDispatchResult result = await dispatcher.DispatchAsync(Message(
            Payload(EventJson(token, "video", Timestamp(Now)))));

        Assert.True(result.CommandExecuted);
        RecordBrowserEventCommand command =
            Assert.IsType<RecordBrowserEventCommand>(handler.LastCommand);
        Assert.Equal(expected, command.Event.EventType);
        Assert.Equal(BrowserSiteCategory.Video, command.Event.Category);
        Assert.Equal(Now, command.Event.TimestampUtc);
        AssertResponse(result, "success", "recorded");
    }

    [Theory]
    [InlineData("gaming", BrowserSiteCategory.Gaming)]
    [InlineData("video", BrowserSiteCategory.Video)]
    [InlineData("social", BrowserSiteCategory.Social)]
    [InlineData("other", BrowserSiteCategory.Other)]
    public async Task Dispatch_RecordBrowserEventAcceptsEveryCanonicalCategory(
        string token,
        BrowserSiteCategory expected)
    {
        StubCommandHandler handler = new();
        ServiceCommandDispatcher dispatcher = CreateDispatcher(handler);

        ProtocolDispatchResult result = await dispatcher.DispatchAsync(Message(
            Payload(EventJson("mediaPlaying", token, Timestamp(Now)))));

        Assert.True(result.CommandExecuted);
        RecordBrowserEventCommand command =
            Assert.IsType<RecordBrowserEventCommand>(handler.LastCommand);
        Assert.Equal(expected, command.Event.Category);
    }

    [Theory]
    [InlineData("\"ruleId\":\"video-1\",")]
    [InlineData("\"url\":\"https://private.example/watch/1\",")]
    [InlineData("\"title\":\"private title\",")]
    [InlineData("\"unknown\":true,")]
    public async Task Dispatch_RecordBrowserEventRejectsEveryExtraPrivacyField(
        string extraField)
    {
        StubCommandHandler handler = new();
        ServiceCommandDispatcher dispatcher = CreateDispatcher(handler);
        string eventJson =
            $"{{{extraField}\"timestamp\":\"{Timestamp(Now)}\",\"eventType\":\"mediaPlaying\",\"category\":\"video\"}}";

        ProtocolDispatchResult result = await dispatcher.DispatchAsync(
            Message(Payload(eventJson)));

        AssertMalformedWithoutExecution(result, handler);
    }

    [Theory]
    [InlineData("{\"timestamp\":\"2026-07-12T15:00:00.000Z\",\"timestamp\":\"2026-07-12T15:00:00.000Z\",\"eventType\":\"mediaPlaying\",\"category\":\"video\"}")]
    [InlineData("{\"timestamp\":\"2026-07-12T15:00:00.000Z\",\"eventType\":\"mediaPlaying\",\"eventType\":\"mediaPlaying\",\"category\":\"video\"}")]
    [InlineData("{\"timestamp\":\"2026-07-12T15:00:00.000Z\",\"eventType\":\"mediaPlaying\",\"category\":\"video\",\"category\":\"video\"}")]
    public async Task Dispatch_RecordBrowserEventRejectsDuplicateFields(string eventJson)
    {
        StubCommandHandler handler = new();
        ServiceCommandDispatcher dispatcher = CreateDispatcher(handler);

        ProtocolDispatchResult result = await dispatcher.DispatchAsync(
            Message(Payload(eventJson)));

        AssertMalformedWithoutExecution(result, handler);
    }

    [Theory]
    [InlineData("2026-07-12T15:00:00Z")]
    [InlineData("2026-07-12T15:00:00.0000000Z")]
    [InlineData("2026-07-12T15:00:00.000+00:00")]
    [InlineData("2026-07-12T15:00:00.000z")]
    [InlineData("2026-07-12 15:00:00.000Z")]
    [InlineData("not-a-time")]
    public async Task Dispatch_RecordBrowserEventRejectsNoncanonicalUtcTimestamp(
        string timestamp)
    {
        StubCommandHandler handler = new();
        ServiceCommandDispatcher dispatcher = CreateDispatcher(handler);

        ProtocolDispatchResult result = await dispatcher.DispatchAsync(Message(
            Payload(EventJson("mediaPlaying", "video", timestamp))));

        AssertMalformedWithoutExecution(result, handler);
    }

    [Theory]
    [InlineData("MediaPlaying", "video")]
    [InlineData("mediaPlaying ", "video")]
    [InlineData("pageViewed", "video")]
    [InlineData("mediaPlaying", "Video")]
    [InlineData("mediaPlaying", "medical")]
    public async Task Dispatch_RecordBrowserEventRejectsUnknownOrNoncanonicalEnums(
        string eventType,
        string category)
    {
        StubCommandHandler handler = new();
        ServiceCommandDispatcher dispatcher = CreateDispatcher(handler);

        ProtocolDispatchResult result = await dispatcher.DispatchAsync(Message(
            Payload(EventJson(eventType, category, Timestamp(Now)))));

        AssertMalformedWithoutExecution(result, handler);
    }

    [Theory]
    [InlineData(-300_001)]
    [InlineData(300_001)]
    public async Task Dispatch_RecordBrowserEventRejectsOverageAndFutureTimestamps(
        int offsetMilliseconds)
    {
        StubCommandHandler handler = new();
        ServiceCommandDispatcher dispatcher = CreateDispatcher(handler);
        string timestamp = Timestamp(Now.AddMilliseconds(offsetMilliseconds));

        ProtocolDispatchResult result = await dispatcher.DispatchAsync(Message(
            Payload(EventJson("navigationBlocked", "social", timestamp))));

        AssertMalformedWithoutExecution(result, handler);
    }

    [Theory]
    [InlineData(-300_000)]
    [InlineData(300_000)]
    public async Task Dispatch_RecordBrowserEventAcceptsExactFreshnessBoundaries(
        int offsetMilliseconds)
    {
        StubCommandHandler handler = new();
        ServiceCommandDispatcher dispatcher = CreateDispatcher(handler);
        string timestamp = Timestamp(Now.AddMilliseconds(offsetMilliseconds));

        ProtocolDispatchResult result = await dispatcher.DispatchAsync(Message(
            Payload(EventJson("navigationBlocked", "social", timestamp))));

        Assert.True(result.CommandExecuted);
        Assert.IsType<RecordBrowserEventCommand>(handler.LastCommand);
    }

    private static ServiceCommandDispatcher CreateDispatcher(StubCommandHandler handler) =>
        new(new JsonProtocolCodec(), handler, new FixedClock(Now));

    private static string Payload(string eventJson) =>
        $"{{\"version\":1,\"type\":\"recordBrowserEvent\",\"requestId\":\"browser-event\",\"payload\":{eventJson}}}";

    private static string EventJson(string eventType, string category, string timestamp) =>
        $"{{\"timestamp\":\"{timestamp}\",\"eventType\":\"{eventType}\",\"category\":\"{category}\"}}";

    private static string Timestamp(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'");

    private static byte[] Message(string json) => Encoding.UTF8.GetBytes(json);

    private static void AssertMalformedWithoutExecution(
        ProtocolDispatchResult result,
        StubCommandHandler handler)
    {
        Assert.False(result.CommandExecuted);
        Assert.Null(handler.LastCommand);
        using JsonDocument response = JsonDocument.Parse(result.ResponseUtf8);
        Assert.Equal("error", response.RootElement.GetProperty("type").GetString());
        Assert.Equal(
            "malformedPayload",
            response.RootElement.GetProperty("payload").GetProperty("code").GetString());
    }

    private static void AssertResponse(
        ProtocolDispatchResult result,
        string outerStatus,
        string dataStatus)
    {
        using JsonDocument response = JsonDocument.Parse(result.ResponseUtf8);
        JsonElement payload = response.RootElement.GetProperty("payload");
        Assert.Equal(outerStatus, payload.GetProperty("status").GetString());
        JsonElement data = payload.GetProperty("data");
        Assert.Equal(["status"], data.EnumerateObject().Select(property => property.Name));
        Assert.Equal(dataStatus, data.GetProperty("status").GetString());
    }

    private sealed class StubCommandHandler : IProtocolCommandHandler
    {
        public ServiceCommand? LastCommand { get; private set; }

        public ValueTask<ProtocolCommandResult> ExecuteAsync(
            ServiceCommand command,
            CancellationToken cancellationToken = default)
        {
            LastCommand = command;
            return ValueTask.FromResult(
                ProtocolCommandResult.Success(new { status = "recorded" }));
        }
    }

    private sealed class FixedClock(DateTimeOffset utcNow) : IClock
    {
        public DateTimeOffset UtcNow => utcNow;
    }
}
