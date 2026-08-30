using System.Text;
using System.Text.Json;
using NightGate.Core;
using NightGate.Service;

namespace NightGate.Service.Tests;

public sealed class DesktopSessionProtocolTests
{
    private const string SessionId = "0123456789abcdef0123456789abcdef";

    [Theory]
    [InlineData("getDesktopPolicy", typeof(GetDesktopPolicyCommand))]
    [InlineData("endDesktopSession", typeof(EndDesktopSessionCommand))]
    public async Task Dispatch_AcceptsExactCanonicalSessionPayload(
        string type,
        Type expectedCommandType)
    {
        StubCommandHandler handler = new();
        ServiceCommandDispatcher dispatcher = new(new JsonProtocolCodec(), handler);

        ProtocolDispatchResult result = await dispatcher.DispatchAsync(Message(
            type,
            $"{{\"sessionId\":\"{SessionId}\"}}"));

        Assert.True(result.CommandExecuted);
        Assert.Equal(expectedCommandType, handler.LastCommand!.GetType());
        Assert.Equal(
            SessionId,
            handler.LastCommand switch
            {
                GetDesktopPolicyCommand command => command.SessionId,
                EndDesktopSessionCommand command => command.SessionId,
                _ => null,
            });
    }

    [Theory]
    [InlineData("getDesktopPolicy", "{}")]
    [InlineData("getDesktopPolicy", "{\"sessionId\":null}")]
    [InlineData("getDesktopPolicy", "{\"sessionId\":123}")]
    [InlineData("getDesktopPolicy", "{\"sessionId\":\"0123456789abcdef0123456789abcde\"}")]
    [InlineData("getDesktopPolicy", "{\"sessionId\":\"0123456789abcdef0123456789abcdef0\"}")]
    [InlineData("getDesktopPolicy", "{\"sessionId\":\"0123456789ABCDEF0123456789ABCDEF\"}")]
    [InlineData("getDesktopPolicy", "{\"sessionId\":\"0123456789abcdef0123456789abcdeg\"}")]
    [InlineData("getDesktopPolicy", "{\"sessionId\":\"0123456789abcdef0123456789abcdef\",\"extra\":true}")]
    [InlineData("getDesktopPolicy", "{\"sessionId\":\"0123456789abcdef0123456789abcdef\",\"sessionId\":\"0123456789abcdef0123456789abcdef\"}")]
    [InlineData("endDesktopSession", "{}")]
    [InlineData("endDesktopSession", "{\"sessionId\":\" 123456789abcdef0123456789abcdef\"}")]
    public async Task Dispatch_RejectsEveryNoncanonicalSessionPayload(
        string type,
        string payload)
    {
        StubCommandHandler handler = new();
        ServiceCommandDispatcher dispatcher = new(new JsonProtocolCodec(), handler);

        ProtocolDispatchResult result = await dispatcher.DispatchAsync(
            Message(type, payload));

        Assert.False(result.CommandExecuted);
        Assert.Null(handler.LastCommand);
        using JsonDocument response = JsonDocument.Parse(result.ResponseUtf8);
        Assert.Equal("error", response.RootElement.GetProperty("type").GetString());
        Assert.Equal(
            "malformedPayload",
            response.RootElement.GetProperty("payload").GetProperty("code").GetString());
    }

    private static byte[] Message(string type, string payload) => Encoding.UTF8.GetBytes(
        $"{{\"version\":1,\"type\":\"{type}\",\"requestId\":\"desktop-session\",\"payload\":{payload}}}");

    private sealed class StubCommandHandler : IProtocolCommandHandler
    {
        public ServiceCommand? LastCommand { get; private set; }

        public ValueTask<ProtocolCommandResult> ExecuteAsync(
            ServiceCommand command,
            CancellationToken cancellationToken = default)
        {
            LastCommand = command;
            return ValueTask.FromResult(
                ProtocolCommandResult.Success(new { accepted = true }));
        }
    }
}
