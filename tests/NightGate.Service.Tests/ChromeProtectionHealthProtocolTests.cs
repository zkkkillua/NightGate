using System.Text;
using System.Text.Json;
using NightGate.Core;
using NightGate.Service;

namespace NightGate.Service.Tests;

public sealed class ChromeProtectionHealthProtocolTests
{
    private const string Hash =
        "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    [Fact]
    public async Task Dispatch_PreservesOnlyBoundedHealthFacts()
    {
        CapturingHandler handler = new();
        ServiceCommandDispatcher dispatcher = new(new JsonProtocolCodec(), handler);

        string request = """
            {"version":1,"type":"recordChromeHealth","requestId":"health-1","payload":{"extensionId":"$EXTENSION$","extensionVersion":"1.2.3","profileTokenSha256":"$HASH$","policyRevision":7,"incognitoAllowed":true,"protectionReady":true}}
            """
            .Replace("$EXTENSION$", ChromeProtectionHealth.ExpectedExtensionId, StringComparison.Ordinal)
            .Replace("$HASH$", Hash, StringComparison.Ordinal);
        ProtocolDispatchResult result = await dispatcher.DispatchAsync(Message(request));

        Assert.True(result.CommandExecuted);
        RecordChromeHealthCommand command = Assert.IsType<RecordChromeHealthCommand>(
            handler.LastCommand);
        Assert.Equal(ChromeProtectionHealth.ExpectedExtensionId, command.ExtensionId);
        Assert.Equal("1.2.3", command.ExtensionVersion);
        Assert.Equal(Hash, command.ProfileTokenSha256);
        Assert.Equal(7, command.PolicyRevision);
        Assert.True(command.IncognitoAllowed);
        Assert.True(command.ProtectionReady);
        using JsonDocument response = JsonDocument.Parse(result.ResponseUtf8);
        Assert.Equal(
            "recordChromeHealthResult",
            response.RootElement.GetProperty("type").GetString());
    }

    [Fact]
    public async Task Dispatch_LegacyHealthWithoutReadinessDefaultsToFailOpen()
    {
        CapturingHandler handler = new();
        ServiceCommandDispatcher dispatcher = new(new JsonProtocolCodec(), handler);
        string request = """
            {"version":1,"type":"recordChromeHealth","requestId":"health-legacy","payload":{"extensionId":"$EXTENSION$","extensionVersion":"1.2.3","profileTokenSha256":"$HASH$","policyRevision":7,"incognitoAllowed":true}}
            """
            .Replace("$EXTENSION$", ChromeProtectionHealth.ExpectedExtensionId, StringComparison.Ordinal)
            .Replace("$HASH$", Hash, StringComparison.Ordinal);

        ProtocolDispatchResult result = await dispatcher.DispatchAsync(Message(request));

        Assert.True(result.CommandExecuted);
        RecordChromeHealthCommand command = Assert.IsType<RecordChromeHealthCommand>(
            handler.LastCommand);
        Assert.False(command.ProtectionReady);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"extensionId\":\"eefgemhlhbdodhlgjmicnoifhclhdgmm\",\"extensionVersion\":\"1.0\",\"profileTokenSha256\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\",\"policyRevision\":1,\"incognitoAllowed\":true,\"url\":\"https://example.com\"}")]
    [InlineData("{\"extensionId\":\"eefgemhlhbdodhlgjmicnoifhclhdgmm\",\"extensionVersion\":\"1.beta\",\"profileTokenSha256\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\",\"policyRevision\":1,\"incognitoAllowed\":true}")]
    [InlineData("{\"extensionId\":\"eefgemhlhbdodhlgjmicnoifhclhdgmm\",\"extensionVersion\":\"1.0\",\"profileTokenSha256\":\"AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA\",\"policyRevision\":1,\"incognitoAllowed\":true}")]
    [InlineData("{\"extensionId\":\"eefgemhlhbdodhlgjmicnoifhclhdgmm\",\"extensionVersion\":\"1.0\",\"profileTokenSha256\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\",\"policyRevision\":-1,\"incognitoAllowed\":true}")]
    [InlineData("{\"extensionId\":\"eefgemhlhbdodhlgjmicnoifhclhdgmm\",\"extensionVersion\":\"1.0\",\"profileTokenSha256\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\",\"policyRevision\":1,\"incognitoAllowed\":1}")]
    [InlineData("{\"extensionId\":\"eefgemhlhbdodhlgjmicnoifhclhdgmm\",\"extensionVersion\":\"1.0\",\"profileTokenSha256\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\",\"policyRevision\":1,\"incognitoAllowed\":true,\"protectionReady\":true,\"profileToken\":\"raw-secret\"}")]
    public async Task Dispatch_RejectsMissingExtraUnboundedOrPrivateFacts(string payload)
    {
        CapturingHandler handler = new();
        ServiceCommandDispatcher dispatcher = new(new JsonProtocolCodec(), handler);

        ProtocolDispatchResult result = await dispatcher.DispatchAsync(Message(
            $"{{\"version\":1,\"type\":\"recordChromeHealth\",\"requestId\":\"health-bad\",\"payload\":{payload}}}"));

        Assert.False(result.CommandExecuted);
        Assert.Null(handler.LastCommand);
    }

    private static byte[] Message(string json) => Encoding.UTF8.GetBytes(json);

    private sealed class CapturingHandler : IProtocolCommandHandler
    {
        public ServiceCommand? LastCommand { get; private set; }

        public ValueTask<ProtocolCommandResult> ExecuteAsync(
            ServiceCommand command,
            CancellationToken cancellationToken = default)
        {
            LastCommand = command;
            return ValueTask.FromResult(ProtocolCommandResult.Success(new { status = "recorded" }));
        }
    }
}
