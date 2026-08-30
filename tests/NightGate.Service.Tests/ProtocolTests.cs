using System.Text;
using System.Text.Json;
using NightGate.Core;
using NightGate.Service;

namespace NightGate.Service.Tests;

public sealed class ProtocolTests
{
    [Fact]
    public void Decode_ExactlySixtyFiveThousandFiveHundredThirtySixBytes_IsAccepted()
    {
        byte[] minimal = Encoding.UTF8.GetBytes(
            "{\"version\":1,\"type\":\"getStatus\",\"requestId\":\"boundary\",\"payload\":{}}");
        byte[] message = new byte[JsonProtocolCodec.MaximumMessageBytes];
        minimal.CopyTo(message, 0);
        Array.Fill(message, (byte)' ', minimal.Length, message.Length - minimal.Length);

        ProtocolDecodeResult result = new JsonProtocolCodec().Decode(message);

        Assert.Equal(ProtocolDecodeStatus.Success, result.Status);
        Assert.Equal("boundary", result.Envelope!.RequestId);
    }

    [Fact]
    public void Decode_SixtyFiveThousandFiveHundredThirtySevenBytes_IsRejectedBeforeParsing()
    {
        CountingEnvelopeParser parser = new();
        byte[] message = new byte[JsonProtocolCodec.MaximumMessageBytes + 1];

        ProtocolDecodeResult result = new JsonProtocolCodec(parser).Decode(message);

        Assert.Equal(ProtocolDecodeStatus.MessageTooLarge, result.Status);
        Assert.Equal(0, parser.CallCount);
    }

    [Fact]
    public async Task Dispatch_UnknownVersionReturnsCorrelatedTypedErrorWithoutExecution()
    {
        StubCommandHandler handler = new();
        ServiceCommandDispatcher dispatcher = new(new JsonProtocolCodec(), handler);

        ProtocolDispatchResult result = await dispatcher.DispatchAsync(Message(
            "{\"version\":2,\"type\":\"getStatus\",\"requestId\":\"version-id\",\"payload\":{}}"));

        Assert.False(result.CommandExecuted);
        Assert.Equal(0, handler.ExecutionCount);
        AssertError(result, "version-id", "unsupportedVersion");
    }

    [Theory]
    [InlineData("\"1\"")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("true")]
    public async Task Dispatch_NonnumericVersionReturnsCorrelatedTypedErrorWithoutExecution(
        string version)
    {
        StubCommandHandler handler = new();
        ServiceCommandDispatcher dispatcher = new(new JsonProtocolCodec(), handler);
        string json = $"{{\"version\":{version},\"type\":\"getStatus\",\"requestId\":\"version-kind-id\",\"payload\":{{}}}}";

        ProtocolDispatchResult result = await dispatcher.DispatchAsync(Message(json));

        Assert.False(result.CommandExecuted);
        Assert.Equal(0, handler.ExecutionCount);
        AssertError(result, "version-kind-id", "malformedMessage");
    }

    [Fact]
    public async Task Dispatch_UnknownTypeReturnsCorrelatedTypedErrorWithoutExecution()
    {
        StubCommandHandler handler = new();
        ServiceCommandDispatcher dispatcher = new(new JsonProtocolCodec(), handler);

        ProtocolDispatchResult result = await dispatcher.DispatchAsync(Message(
            "{\"version\":1,\"type\":\"shutdownMachine\",\"requestId\":\"type-id\",\"payload\":{}}"));

        Assert.False(result.CommandExecuted);
        Assert.Equal(0, handler.ExecutionCount);
        AssertError(result, "type-id", "unknownType");
    }

    [Fact]
    public async Task Dispatch_MalformedJsonReturnsTypedErrorWithoutExecution()
    {
        StubCommandHandler handler = new();
        ServiceCommandDispatcher dispatcher = new(new JsonProtocolCodec(), handler);

        ProtocolDispatchResult result = await dispatcher.DispatchAsync(Message("{not-json"));

        Assert.False(result.CommandExecuted);
        Assert.Equal(0, handler.ExecutionCount);
        AssertError(result, string.Empty, "malformedMessage");
    }

    [Fact]
    public async Task Dispatch_MalformedTypedPayloadReturnsCorrelatedErrorWithoutExecution()
    {
        StubCommandHandler handler = new();
        ServiceCommandDispatcher dispatcher = new(new JsonProtocolCodec(), handler);

        ProtocolDispatchResult result = await dispatcher.DispatchAsync(Message(
            "{\"version\":1,\"type\":\"requestOverride\",\"requestId\":\"payload-id\",\"payload\":{}}"));

        Assert.False(result.CommandExecuted);
        Assert.Equal(0, handler.ExecutionCount);
        AssertError(result, "payload-id", "malformedPayload");
    }

    [Theory]
    [InlineData("getPolicy")]
    [InlineData("getStatus")]
    [InlineData("clearHistory")]
    public async Task Dispatch_EmptyPayloadCommandRejectsUnknownFields(string type)
    {
        StubCommandHandler handler = new();
        ServiceCommandDispatcher dispatcher = new(new JsonProtocolCodec(), handler);
        string json = $"{{\"version\":1,\"type\":\"{type}\",\"requestId\":\"empty-id\",\"payload\":{{\"unexpected\":true}}}}";

        ProtocolDispatchResult result = await dispatcher.DispatchAsync(Message(json));

        Assert.False(result.CommandExecuted);
        Assert.Equal(0, handler.ExecutionCount);
        AssertError(result, "empty-id", "malformedPayload");
    }

    [Theory]
    [InlineData("requestOverride", "{\"kind\":99}")]
    [InlineData("requestOverride", "{\"kind\":0}")]
    [InlineData("recordEvent", "{\"kind\":99}")]
    [InlineData("recordEvent", "{\"kind\":\"notAnEvent\"}")]
    public async Task Dispatch_NumericOrUndefinedEnumsAreMalformedAndNeverExecute(
        string type,
        string payload)
    {
        StubCommandHandler handler = new();
        ServiceCommandDispatcher dispatcher = new(new JsonProtocolCodec(), handler);
        string json = $"{{\"version\":1,\"type\":\"{type}\",\"requestId\":\"enum-id\",\"payload\":{payload}}}";

        ProtocolDispatchResult result = await dispatcher.DispatchAsync(Message(json));

        Assert.False(result.CommandExecuted);
        Assert.Equal(0, handler.ExecutionCount);
        AssertError(result, "enum-id", "malformedPayload");
    }

    [Theory]
    [InlineData("requestOverride", "{\"kind\":\"TeamRescue\"}")]
    [InlineData("requestOverride", "{\"kind\":\"teamRescue \"}")]
    [InlineData("requestOverride", "{\"kind\":\"teamRescue,emergency\"}")]
    [InlineData("requestOverride", "{\"kind\":\"emergency\",\"emergencyReason\":\"Health\"}")]
    [InlineData("requestOverride", "{\"kind\":\"emergency\",\"emergencyReason\":\"health \"}")]
    [InlineData("requestOverride", "{\"kind\":\"emergency\",\"emergencyReason\":\"other\"}")]
    [InlineData("requestOverride", "{\"kind\":\"emergency\",\"emergencyReason\":0}")]
    [InlineData("recordEvent", "{\"kind\":\"StateObserved\"}")]
    [InlineData("recordEvent", "{\"kind\":\"stateObserved \"}")]
    [InlineData("recordEvent", "{\"kind\":\"stateObserved,nightClosed\"}")]
    [InlineData("recordEvent", "{\"kind\":\"stateObserved\",\"basePhase\":\"Grace\"}")]
    [InlineData("recordEvent", "{\"kind\":\"stateObserved\",\"basePhase\":\"grace \"}")]
    [InlineData("recordEvent", "{\"kind\":\"stateObserved\",\"basePhase\":0}")]
    [InlineData("recordEvent", "{\"kind\":\"overrideEnded\",\"overrideKind\":\"TeamRescue\"}")]
    [InlineData("recordEvent", "{\"kind\":\"overrideEnded\",\"overrideKind\":\"teamRescue \"}")]
    [InlineData("recordEvent", "{\"kind\":\"overrideEnded\",\"overrideKind\":0}")]
    public async Task Dispatch_NoncanonicalEnumTokensAreMalformedAndNeverExecute(
        string type,
        string payload)
    {
        StubCommandHandler handler = new();
        ServiceCommandDispatcher dispatcher = new(new JsonProtocolCodec(), handler);
        string json = $"{{\"version\":1,\"type\":\"{type}\",\"requestId\":\"strict-enum-id\",\"payload\":{payload}}}";

        ProtocolDispatchResult result = await dispatcher.DispatchAsync(Message(json));

        Assert.False(result.CommandExecuted);
        Assert.Equal(0, handler.ExecutionCount);
        AssertError(result, "strict-enum-id", "malformedPayload");
    }

    [Theory]
    [InlineData("coolingOff")]
    [InlineData("overrideActive")]
    public async Task Dispatch_TemporaryPhaseCannotPopulateBasePhase(string phase)
    {
        StubCommandHandler handler = new();
        ServiceCommandDispatcher dispatcher = new(new JsonProtocolCodec(), handler);
        string json = $"{{\"version\":1,\"type\":\"recordEvent\",\"requestId\":\"base-phase-id\",\"payload\":{{\"kind\":\"stateObserved\",\"basePhase\":\"{phase}\"}}}}";

        ProtocolDispatchResult result = await dispatcher.DispatchAsync(Message(json));

        Assert.False(result.CommandExecuted);
        Assert.Equal(0, handler.ExecutionCount);
        AssertError(result, "base-phase-id", "malformedPayload");
    }

    [Theory]
    [InlineData("getPolicy", "{}", typeof(GetPolicyCommand))]
    [InlineData("getStatus", "{}", typeof(GetStatusCommand))]
    [InlineData("requestOverride", "{\"kind\":\"teamRescue\"}", typeof(RequestOverrideCommand))]
    [InlineData("recordEvent", "{\"kind\":\"stateObserved\",\"basePhase\":\"grace\"}", typeof(RecordEventCommand))]
    [InlineData("recordEvent", "{\"kind\":\"workstationLocked\"}", typeof(RecordEventCommand))]
    [InlineData(
        "confirmIPhoneStep",
        "{\"step\":2,\"checklist\":{\"healthSleepScheduleConfigured\":true,\"sleepFocusConfigured\":true,\"downtimeConfigured\":true,\"blockAtDowntimeEnabled\":true,\"entertainmentCategoriesRestricted\":true,\"requiredAppsAllowed\":true,\"safariNotAllowlisted\":true,\"distinctRecoverableScreenTimePasscodeAcknowledged\":true,\"oldAlarmsChecked\":true,\"phonePlacementPlanned\":true}}",
        typeof(ConfirmIPhoneStepCommand))]
    [InlineData("clearHistory", "{}", typeof(ClearHistoryCommand))]
    public async Task Dispatch_AllowlistedTypesExecuteTypedCommands(
        string type,
        string payload,
        Type expectedCommandType)
    {
        StubCommandHandler handler = new();
        ServiceCommandDispatcher dispatcher = new(new JsonProtocolCodec(), handler);
        string json = $"{{\"version\":1,\"type\":\"{type}\",\"requestId\":\"allowed-id\",\"payload\":{payload}}}";

        ProtocolDispatchResult result = await dispatcher.DispatchAsync(Message(json));

        Assert.True(result.CommandExecuted);
        Assert.Equal(1, handler.ExecutionCount);
        Assert.IsType(expectedCommandType, handler.LastCommand);
        using JsonDocument response = JsonDocument.Parse(result.ResponseUtf8);
        Assert.Equal(1, response.RootElement.GetProperty("version").GetInt32());
        Assert.Equal($"{type}Result", response.RootElement.GetProperty("type").GetString());
        Assert.Equal("allowed-id", response.RootElement.GetProperty("requestId").GetString());
    }

    [Fact]
    public async Task Dispatch_ResponsePreservesRequestCorrelation()
    {
        StubCommandHandler handler = new();
        ServiceCommandDispatcher dispatcher = new(new JsonProtocolCodec(), handler);

        ProtocolDispatchResult result = await dispatcher.DispatchAsync(Message(
            "{\"version\":1,\"type\":\"getStatus\",\"requestId\":\"correlation-123\",\"payload\":{}}"));

        using JsonDocument response = JsonDocument.Parse(result.ResponseUtf8);
        Assert.Equal("correlation-123", response.RootElement.GetProperty("requestId").GetString());
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"step\":1,\"checklist\":{\"healthSleepScheduleConfigured\":true,\"sleepFocusConfigured\":true,\"downtimeConfigured\":true,\"blockAtDowntimeEnabled\":true,\"requiredAppsAllowed\":true,\"safariNotAllowlisted\":true,\"distinctRecoverableScreenTimePasscodeAcknowledged\":true,\"oldAlarmsChecked\":true,\"phonePlacementPlanned\":true}}")]
    [InlineData("{\"step\":2,\"observedAtUtc\":\"2026-07-14T14:29:59.000Z\",\"checklist\":{\"healthSleepScheduleConfigured\":true,\"sleepFocusConfigured\":true,\"downtimeConfigured\":true,\"blockAtDowntimeEnabled\":true,\"requiredAppsAllowed\":true,\"safariNotAllowlisted\":true,\"distinctRecoverableScreenTimePasscodeAcknowledged\":true,\"oldAlarmsChecked\":true,\"phonePlacementPlanned\":true}}")]
    [InlineData("{\"step\":2,\"step\":2,\"checklist\":{\"healthSleepScheduleConfigured\":true,\"sleepFocusConfigured\":true,\"downtimeConfigured\":true,\"blockAtDowntimeEnabled\":true,\"requiredAppsAllowed\":true,\"safariNotAllowlisted\":true,\"distinctRecoverableScreenTimePasscodeAcknowledged\":true,\"oldAlarmsChecked\":true,\"phonePlacementPlanned\":true}}")]
    [InlineData("{\"step\":2,\"checklist\":{\"healthSleepScheduleConfigured\":\"true\",\"sleepFocusConfigured\":true,\"downtimeConfigured\":true,\"blockAtDowntimeEnabled\":true,\"requiredAppsAllowed\":true,\"safariNotAllowlisted\":true,\"distinctRecoverableScreenTimePasscodeAcknowledged\":true,\"oldAlarmsChecked\":true,\"phonePlacementPlanned\":true}}")]
    [InlineData("{\"step\":2,\"checklist\":{\"healthSleepScheduleConfigured\":true,\"sleepFocusConfigured\":true,\"downtimeConfigured\":true,\"blockAtDowntimeEnabled\":true,\"requiredAppsAllowed\":true,\"safariNotAllowlisted\":true,\"distinctRecoverableScreenTimePasscodeAcknowledged\":true,\"oldAlarmsChecked\":true,\"phonePlacementPlanned\":true}}")]
    [InlineData("{\"step\":2,\"checklist\":{\"healthSleepScheduleConfigured\":true,\"sleepFocusConfigured\":true,\"downtimeConfigured\":true,\"blockAtDowntimeEnabled\":true,\"requiredAppsAllowed\":true,\"safariNotAllowlisted\":true,\"distinctRecoverableScreenTimePasscodeAcknowledged\":true,\"oldAlarmsChecked\":true}}")]
    [InlineData("{\"step\":2,\"checklist\":{\"healthSleepScheduleConfigured\":true,\"sleepFocusConfigured\":true,\"downtimeConfigured\":true,\"blockAtDowntimeEnabled\":true,\"requiredAppsAllowed\":true,\"safariNotAllowlisted\":true,\"distinctRecoverableScreenTimePasscodeAcknowledged\":true,\"oldAlarmsChecked\":true,\"phonePlacementPlanned\":true,\"passcode\":\"secret\"}}")]
    [InlineData("{\"step\":2,\"password\":\"secret\",\"checklist\":{\"healthSleepScheduleConfigured\":true,\"sleepFocusConfigured\":true,\"downtimeConfigured\":true,\"blockAtDowntimeEnabled\":true,\"requiredAppsAllowed\":true,\"safariNotAllowlisted\":true,\"distinctRecoverableScreenTimePasscodeAcknowledged\":true,\"oldAlarmsChecked\":true,\"phonePlacementPlanned\":true}}")]
    [InlineData("{\"step\":2,\"checklist\":{\"healthSleepScheduleConfigured\":true,\"sleepFocusConfigured\":true,\"downtimeConfigured\":true,\"blockAtDowntimeEnabled\":true,\"requiredAppsAllowed\":true,\"safariNotAllowlisted\":true,\"distinctRecoverableScreenTimePasscodeAcknowledged\":true,\"oldAlarmsChecked\":true,\"phonePlacementPlanned\":true,\"appleCredential\":\"secret\"}}")]
    [InlineData("{\"step\":2,\"checklist\":{\"healthSleepScheduleConfigured\":true,\"sleepFocusConfigured\":true,\"downtimeConfigured\":true,\"blockAtDowntimeEnabled\":true,\"requiredAppsAllowed\":true,\"safariNotAllowlisted\":true,\"distinctRecoverableScreenTimePasscodeAcknowledged\":true,\"oldAlarmsChecked\":true,\"phonePlacementPlanned\":true,\"recoveryKey\":\"secret\"}}")]
    [InlineData("{\"step\":2,\"checklist\":{\"healthSleepScheduleConfigured\":true,\"sleepFocusConfigured\":true,\"downtimeConfigured\":true,\"blockAtDowntimeEnabled\":true,\"requiredAppsAllowed\":true,\"safariNotAllowlisted\":true,\"distinctRecoverableScreenTimePasscodeAcknowledged\":true,\"oldAlarmsChecked\":true,\"phonePlacementPlanned\":true,\"notes\":\"free text\"}}")]
    [InlineData("{\"step\":2,\"checklist\":{\"healthSleepScheduleConfigured\":true,\"healthSleepScheduleConfigured\":true,\"sleepFocusConfigured\":true,\"downtimeConfigured\":true,\"blockAtDowntimeEnabled\":true,\"requiredAppsAllowed\":true,\"safariNotAllowlisted\":true,\"distinctRecoverableScreenTimePasscodeAcknowledged\":true,\"oldAlarmsChecked\":true,\"phonePlacementPlanned\":true}}")]
    public async Task Dispatch_ConfirmIPhoneStepRejectsNonExactOrSecretBearingPayload(
        string payload)
    {
        StubCommandHandler handler = new();
        ServiceCommandDispatcher dispatcher = new(new JsonProtocolCodec(), handler);

        ProtocolDispatchResult result = await dispatcher.DispatchAsync(Message(
            $"{{\"version\":1,\"type\":\"confirmIPhoneStep\",\"requestId\":\"iphone-invalid\",\"payload\":{payload}}}"));

        Assert.False(result.CommandExecuted);
        Assert.Equal(0, handler.ExecutionCount);
        AssertError(result, "iphone-invalid", "malformedPayload");
    }

    [Theory]
    [InlineData("unicode-\u4F60")]
    [InlineData("line\nbreak")]
    public async Task Dispatch_NonPrintableAsciiRequestIdIsMalformedWithoutExecution(string requestId)
    {
        StubCommandHandler handler = new();
        ServiceCommandDispatcher dispatcher = new(new JsonProtocolCodec(), handler);
        string encodedRequestId = JsonSerializer.Serialize(requestId);

        ProtocolDispatchResult result = await dispatcher.DispatchAsync(Message(
            $"{{\"version\":1,\"type\":\"getPolicy\",\"requestId\":{encodedRequestId},\"payload\":{{}}}}"));

        Assert.False(result.CommandExecuted);
        Assert.Equal(0, handler.ExecutionCount);
        AssertError(result, string.Empty, "malformedMessage");
    }

    [Fact]
    public async Task Dispatch_RequestIdLongerThanSixtyFourCharactersIsMalformedWithoutExecution()
    {
        StubCommandHandler handler = new();
        ServiceCommandDispatcher dispatcher = new(new JsonProtocolCodec(), handler);
        string requestId = new('x', 65);

        ProtocolDispatchResult result = await dispatcher.DispatchAsync(Message(
            $"{{\"version\":1,\"type\":\"getPolicy\",\"requestId\":\"{requestId}\",\"payload\":{{}}}}"));

        Assert.False(result.CommandExecuted);
        Assert.Equal(0, handler.ExecutionCount);
        AssertError(result, string.Empty, "malformedMessage");
    }

    [Fact]
    public async Task Dispatch_EnvelopeWithUnknownFieldIsMalformedWithoutExecution()
    {
        StubCommandHandler handler = new();
        ServiceCommandDispatcher dispatcher = new(new JsonProtocolCodec(), handler);

        ProtocolDispatchResult result = await dispatcher.DispatchAsync(Message(
            "{\"version\":1,\"type\":\"getPolicy\",\"requestId\":\"strict\",\"payload\":{},\"extra\":true}"));

        Assert.False(result.CommandExecuted);
        Assert.Equal(0, handler.ExecutionCount);
        AssertError(result, "strict", "malformedMessage");
    }

    private static byte[] Message(string json) => Encoding.UTF8.GetBytes(json);

    private static void AssertError(ProtocolDispatchResult result, string requestId, string code)
    {
        using JsonDocument response = JsonDocument.Parse(result.ResponseUtf8);
        JsonElement root = response.RootElement;
        Assert.Equal(1, root.GetProperty("version").GetInt32());
        Assert.Equal("error", root.GetProperty("type").GetString());
        Assert.Equal(requestId, root.GetProperty("requestId").GetString());
        Assert.Equal(code, root.GetProperty("payload").GetProperty("code").GetString());
    }

    private sealed class CountingEnvelopeParser : IProtocolEnvelopeParser
    {
        public int CallCount { get; private set; }

        public ProtocolParseResult Parse(ReadOnlyMemory<byte> utf8Json)
        {
            CallCount++;
            return new(ProtocolDecodeStatus.MalformedMessage, null);
        }
    }

    private sealed class StubCommandHandler : IProtocolCommandHandler
    {
        public int ExecutionCount { get; private set; }

        public ServiceCommand? LastCommand { get; private set; }

        public ValueTask<ProtocolCommandResult> ExecuteAsync(
            ServiceCommand command,
            CancellationToken cancellationToken = default)
        {
            ExecutionCount++;
            LastCommand = command;
            return ValueTask.FromResult(ProtocolCommandResult.Success(new { accepted = true }));
        }
    }
}
