using System.Text;
using System.Text.Json;
using NightGate.Core;
using NightGate.Service;

namespace NightGate.Service.Tests;

public sealed class UserStateProtocolTests
{
    [Theory]
    [InlineData(
        "getUserState",
        "{}",
        typeof(GetUserStateCommand))]
    [InlineData(
        "completeOnboardingStep",
        "{\"step\":3,\"chromeVerified\":true,\"incognitoProtected\":false,\"incognitoWarningAcknowledged\":true,\"iPhoneConfirmedThroughStep\":2,\"chromeDegradedAcknowledged\":false}",
        typeof(CompleteOnboardingStepCommand))]
    [InlineData(
        "saveNightSelfReport",
        "{\"nightDate\":\"2026-07-14\",\"phoneOutOfReach\":null,\"wakeWithinWindow\":false}",
        typeof(SaveNightSelfReportCommand))]
    public async Task Dispatch_ExactUserStateCommandsExecuteTypedCommand(
        string type,
        string payload,
        Type expectedCommandType)
    {
        RecordingHandler handler = new();
        ServiceCommandDispatcher dispatcher = new(new JsonProtocolCodec(), handler);

        ProtocolDispatchResult result = await dispatcher.DispatchAsync(Message(
            $"{{\"version\":1,\"type\":\"{type}\",\"requestId\":\"user-state\",\"payload\":{payload}}}"));

        Assert.True(result.CommandExecuted);
        Assert.IsType(expectedCommandType, handler.LastCommand);
    }

    [Fact]
    public async Task Dispatch_CompleteOnboardingStepPreservesTypedFacts()
    {
        RecordingHandler handler = new();
        ServiceCommandDispatcher dispatcher = new(new JsonProtocolCodec(), handler);

        await dispatcher.DispatchAsync(Message(
            "{\"version\":1,\"type\":\"completeOnboardingStep\",\"requestId\":\"onboarding\",\"payload\":{\"step\":4,\"chromeVerified\":true,\"incognitoProtected\":true,\"incognitoWarningAcknowledged\":false,\"iPhoneConfirmedThroughStep\":3,\"chromeDegradedAcknowledged\":true}}"));

        CompleteOnboardingStepCommand command = Assert.IsType<CompleteOnboardingStepCommand>(
            handler.LastCommand);
        Assert.Equal(4, command.Step);
        Assert.True(command.ChromeVerified);
        Assert.True(command.IncognitoProtected);
        Assert.False(command.IncognitoWarningAcknowledged);
        Assert.Equal(3, command.IPhoneConfirmedThroughStep);
        Assert.True(command.ChromeDegradedAcknowledged);
    }

    [Fact]
    public async Task Dispatch_SaveNightSelfReportPreservesNullableFacts()
    {
        RecordingHandler handler = new();
        ServiceCommandDispatcher dispatcher = new(new JsonProtocolCodec(), handler);

        await dispatcher.DispatchAsync(Message(
            "{\"version\":1,\"type\":\"saveNightSelfReport\",\"requestId\":\"self-report\",\"payload\":{\"nightDate\":\"2026-07-14\",\"phoneOutOfReach\":null,\"wakeWithinWindow\":true}}"));

        SaveNightSelfReportCommand command = Assert.IsType<SaveNightSelfReportCommand>(
            handler.LastCommand);
        Assert.Equal(new DateOnly(2026, 7, 14), command.NightDate);
        Assert.Null(command.PhoneOutOfReach);
        Assert.True(command.WakeWithinWindow);
    }

    [Theory]
    [InlineData("getUserState", "{\"extra\":true}")]
    [InlineData("getUserState", "{\"password\":\"secret\"}")]
    [InlineData("getUserState", "{\"Extra\":true}")]
    [InlineData("completeOnboardingStep", "{}")]
    [InlineData("completeOnboardingStep", "{\"step\":3,\"chromeVerified\":true,\"incognitoProtected\":true,\"incognitoWarningAcknowledged\":true,\"iPhoneConfirmedThroughStep\":0}")]
    [InlineData("completeOnboardingStep", "{\"step\":0,\"chromeVerified\":true,\"incognitoProtected\":true,\"incognitoWarningAcknowledged\":true,\"iPhoneConfirmedThroughStep\":0,\"chromeDegradedAcknowledged\":false}")]
    [InlineData("completeOnboardingStep", "{\"step\":6,\"chromeVerified\":true,\"incognitoProtected\":true,\"incognitoWarningAcknowledged\":true,\"iPhoneConfirmedThroughStep\":0,\"chromeDegradedAcknowledged\":false}")]
    [InlineData("completeOnboardingStep", "{\"step\":\"3\",\"chromeVerified\":true,\"incognitoProtected\":true,\"incognitoWarningAcknowledged\":true,\"iPhoneConfirmedThroughStep\":0,\"chromeDegradedAcknowledged\":false}")]
    [InlineData("completeOnboardingStep", "{\"step\":3,\"chromeVerified\":1,\"incognitoProtected\":true,\"incognitoWarningAcknowledged\":true,\"iPhoneConfirmedThroughStep\":0,\"chromeDegradedAcknowledged\":false}")]
    [InlineData("completeOnboardingStep", "{\"step\":3,\"chromeVerified\":true,\"incognitoProtected\":true,\"incognitoWarningAcknowledged\":true,\"iPhoneConfirmedThroughStep\":5,\"chromeDegradedAcknowledged\":false}")]
    [InlineData("completeOnboardingStep", "{\"step\":3,\"chromeVerified\":true,\"incognitoProtected\":true,\"incognitoWarningAcknowledged\":true,\"iPhoneConfirmedThroughStep\":0,\"chromeDegradedAcknowledged\":\"true\"}")]
    [InlineData("completeOnboardingStep", "{\"step\":3,\"chromeVerified\":true,\"incognitoProtected\":true,\"incognitoWarningAcknowledged\":true,\"iPhoneConfirmedThroughStep\":0,\"chromeDegradedAcknowledged\":false,\"notes\":\"free text\"}")]
    [InlineData("completeOnboardingStep", "{\"step\":3,\"chromeVerified\":true,\"incognitoProtected\":true,\"incognitoWarningAcknowledged\":true,\"iPhoneConfirmedThroughStep\":0,\"chromeDegradedAcknowledged\":false,\"passcode\":\"secret\"}")]
    [InlineData("completeOnboardingStep", "{\"step\":3,\"step\":3,\"chromeVerified\":true,\"incognitoProtected\":true,\"incognitoWarningAcknowledged\":true,\"iPhoneConfirmedThroughStep\":0,\"chromeDegradedAcknowledged\":false}")]
    [InlineData("completeOnboardingStep", "{\"Step\":3,\"chromeVerified\":true,\"incognitoProtected\":true,\"incognitoWarningAcknowledged\":true,\"iPhoneConfirmedThroughStep\":0,\"chromeDegradedAcknowledged\":false}")]
    [InlineData("saveNightSelfReport", "{}")]
    [InlineData("saveNightSelfReport", "{\"nightDate\":\"2026-7-14\",\"phoneOutOfReach\":true,\"wakeWithinWindow\":true}")]
    [InlineData("saveNightSelfReport", "{\"nightDate\":\"2026-02-30\",\"phoneOutOfReach\":true,\"wakeWithinWindow\":true}")]
    [InlineData("saveNightSelfReport", "{\"nightDate\":\"2026-07-14T00:00:00Z\",\"phoneOutOfReach\":true,\"wakeWithinWindow\":true}")]
    [InlineData("saveNightSelfReport", "{\"nightDate\":\"2026-07-14\",\"phoneOutOfReach\":\"true\",\"wakeWithinWindow\":true}")]
    [InlineData("saveNightSelfReport", "{\"nightDate\":\"2026-07-14\",\"phoneOutOfReach\":true,\"wakeWithinWindow\":true,\"observedAtUtc\":\"2026-07-14T16:00:00Z\"}")]
    [InlineData("saveNightSelfReport", "{\"nightDate\":\"2026-07-14\",\"phoneOutOfReach\":true,\"wakeWithinWindow\":true,\"password\":\"secret\"}")]
    [InlineData("saveNightSelfReport", "{\"nightDate\":\"2026-07-14\",\"nightDate\":\"2026-07-14\",\"phoneOutOfReach\":true,\"wakeWithinWindow\":true}")]
    [InlineData("saveNightSelfReport", "{\"NightDate\":\"2026-07-14\",\"phoneOutOfReach\":true,\"wakeWithinWindow\":true}")]
    public async Task Dispatch_UserStatePayloadMustBeExactAndSecretFree(
        string type,
        string payload)
    {
        RecordingHandler handler = new();
        ServiceCommandDispatcher dispatcher = new(new JsonProtocolCodec(), handler);

        ProtocolDispatchResult result = await dispatcher.DispatchAsync(Message(
            $"{{\"version\":1,\"type\":\"{type}\",\"requestId\":\"invalid-user-state\",\"payload\":{payload}}}"));

        Assert.False(result.CommandExecuted);
        Assert.Null(handler.LastCommand);
        AssertError(result, "malformedPayload");
    }

    private static byte[] Message(string json) => Encoding.UTF8.GetBytes(json);

    private static void AssertError(ProtocolDispatchResult result, string expectedCode)
    {
        using JsonDocument response = JsonDocument.Parse(result.ResponseUtf8);
        JsonElement root = response.RootElement;
        Assert.Equal("error", root.GetProperty("type").GetString());
        Assert.Equal(
            expectedCode,
            root.GetProperty("payload").GetProperty("code").GetString());
    }

    private sealed class RecordingHandler : IProtocolCommandHandler
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
