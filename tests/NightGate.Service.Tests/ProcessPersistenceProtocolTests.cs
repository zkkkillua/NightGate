using System.Text;
using System.Text.Json;
using NightGate.Core;
using NightGate.Service;

namespace NightGate.Service.Tests;

public sealed class ProcessPersistenceProtocolTests
{
    [Theory]
    [InlineData("processGateEnvelope", ProcessPersistenceSlot.ProcessGateEnvelope)]
    [InlineData("processSourceContinuity", ProcessPersistenceSlot.ProcessSourceContinuity)]
    public async Task Dispatch_LoadFixedSlot_CreatesTypedCommand(
        string slotToken,
        ProcessPersistenceSlot expectedSlot)
    {
        CapturingHandler handler = new();
        ServiceCommandDispatcher dispatcher = new(new JsonProtocolCodec(), handler);

        byte[] request = JsonSerializer.SerializeToUtf8Bytes(new
        {
            version = 1,
            type = "loadProcessPersistence",
            requestId = "load-slot",
            payload = new { slot = slotToken },
        });
        ProtocolDispatchResult result = await dispatcher.DispatchAsync(request);

        Assert.True(result.CommandExecuted);
        LoadProcessPersistenceCommand command = Assert.IsType<LoadProcessPersistenceCommand>(
            handler.LastCommand);
        Assert.Equal(expectedSlot, command.Slot);
    }

    [Fact]
    public async Task Dispatch_CompareExchange_EmbedsPayloadAsObjectAndCreatesTypedCommand()
    {
        CapturingHandler handler = new();
        ServiceCommandDispatcher dispatcher = new(new JsonProtocolCodec(), handler);

        ProtocolDispatchResult result = await dispatcher.DispatchAsync(Message(
            """
            {"version":1,"type":"compareExchangeProcessPersistence","requestId":"cas-slot","payload":{"slot":"processGateEnvelope","expectedVersion":null,"schemaVersion":1,"replacementVersion":1,"payload":{"schemaVersion":1,"message":"睡觉"}}}
            """));

        Assert.True(result.CommandExecuted);
        CompareExchangeProcessPersistenceCommand command =
            Assert.IsType<CompareExchangeProcessPersistenceCommand>(handler.LastCommand);
        Assert.Equal(ProcessPersistenceSlot.ProcessGateEnvelope, command.Slot);
        Assert.Null(command.ExpectedVersion);
        Assert.Equal(1, command.Replacement.Version);
        using JsonDocument payload = JsonDocument.Parse(command.Replacement.PayloadJson);
        Assert.Equal(JsonValueKind.Object, payload.RootElement.ValueKind);
        Assert.Equal("睡觉", payload.RootElement.GetProperty("message").GetString());
    }

    [Theory]
    [InlineData("loadProcessPersistence", "{\"slot\":\"arbitraryKey\"}")]
    [InlineData("loadProcessPersistence", "{\"slot\":\"processGateEnvelope\",\"key\":\"other\"}")]
    [InlineData("loadProcessPersistence", "{\"slot\":\"processGateEnvelope\",\"slot\":\"processGateEnvelope\"}")]
    [InlineData("compareExchangeProcessPersistence", "{\"slot\":\"processGateEnvelope\",\"expectedVersion\":null,\"schemaVersion\":2,\"replacementVersion\":1,\"payload\":{\"schemaVersion\":2}}")]
    [InlineData("compareExchangeProcessPersistence", "{\"slot\":\"processGateEnvelope\",\"expectedVersion\":-1,\"schemaVersion\":1,\"replacementVersion\":1,\"payload\":{\"schemaVersion\":1}}")]
    [InlineData("compareExchangeProcessPersistence", "{\"slot\":\"processGateEnvelope\",\"expectedVersion\":2,\"schemaVersion\":1,\"replacementVersion\":4,\"payload\":{\"schemaVersion\":1}}")]
    [InlineData("compareExchangeProcessPersistence", "{\"slot\":\"processGateEnvelope\",\"expectedVersion\":null,\"schemaVersion\":1,\"replacementVersion\":1,\"payload\":\"not-an-object\"}")]
    [InlineData("compareExchangeProcessPersistence", "{\"slot\":\"processGateEnvelope\",\"expectedVersion\":null,\"schemaVersion\":1,\"replacementVersion\":1,\"payload\":{\"schemaVersion\":1,\"nested\":{\"x\":1,\"x\":2}}}")]
    public async Task Dispatch_InvalidPersistencePayload_IsRejectedWithoutExecution(
        string type,
        string payload)
    {
        CapturingHandler handler = new();
        ServiceCommandDispatcher dispatcher = new(new JsonProtocolCodec(), handler);

        ProtocolDispatchResult result = await dispatcher.DispatchAsync(Message(
            $"{{\"version\":1,\"type\":\"{type}\",\"requestId\":\"invalid-slot\",\"payload\":{payload}}}"));

        Assert.False(result.CommandExecuted);
        Assert.Null(handler.LastCommand);
        AssertError(result, "invalid-slot", "malformedPayload");
    }

    [Fact]
    public async Task Dispatch_PersistencePayloadAboveSlotLimit_IsRejectedWithinFrameLimit()
    {
        CapturingHandler handler = new();
        ServiceCommandDispatcher dispatcher = new(new JsonProtocolCodec(), handler);
        string value = new('x', ProcessPersistenceLimits.MaximumPayloadBytes);
        byte[] request = JsonSerializer.SerializeToUtf8Bytes(new
        {
            version = 1,
            type = "compareExchangeProcessPersistence",
            requestId = "large-slot",
            payload = new
            {
                slot = "processGateEnvelope",
                expectedVersion = (long?)null,
                schemaVersion = 1,
                replacementVersion = 1,
                payload = new { schemaVersion = 1, value },
            },
        });
        Assert.True(request.Length <= JsonProtocolCodec.MaximumMessageBytes);

        ProtocolDispatchResult result = await dispatcher.DispatchAsync(request);

        Assert.False(result.CommandExecuted);
        AssertError(result, "large-slot", "malformedPayload");
    }

    private static byte[] Message(string json) => Encoding.UTF8.GetBytes(json);

    private static void AssertError(
        ProtocolDispatchResult result,
        string requestId,
        string code)
    {
        using JsonDocument response = JsonDocument.Parse(result.ResponseUtf8);
        Assert.Equal("error", response.RootElement.GetProperty("type").GetString());
        Assert.Equal(requestId, response.RootElement.GetProperty("requestId").GetString());
        Assert.Equal(
            code,
            response.RootElement.GetProperty("payload").GetProperty("code").GetString());
    }

    private sealed class CapturingHandler : IProtocolCommandHandler
    {
        public ServiceCommand? LastCommand { get; private set; }

        public ValueTask<ProtocolCommandResult> ExecuteAsync(
            ServiceCommand command,
            CancellationToken cancellationToken = default)
        {
            LastCommand = command;
            return ValueTask.FromResult(ProtocolCommandResult.Success(new { accepted = true }));
        }
    }
}
