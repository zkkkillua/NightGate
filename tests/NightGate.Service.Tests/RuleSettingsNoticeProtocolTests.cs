using System.Text;
using System.Text.Json;
using NightGate.Core;
using NightGate.Service;

namespace NightGate.Service.Tests;

public sealed class RuleSettingsNoticeProtocolTests
{
    [Fact]
    public async Task Dispatch_SaveRuleSettingsCreatesCanonicalTypedCommand()
    {
        RecordingHandler handler = new();
        ServiceCommandDispatcher dispatcher = new(new JsonProtocolCodec(), handler);

        ProtocolDispatchResult result = await dispatcher.DispatchAsync(Message("""
            {"version":1,"type":"saveRuleSettings","requestId":"save-rules","payload":{"appRules":[{"id":"game","rootExecutablePath":"C:\\Games\\game.exe","helperExecutablePaths":["C:\\Games\\helper.exe"],"category":"game","sessionMinutes":45},{"id":"voice","rootExecutablePath":"C:\\Voice\\voice.exe","helperExecutablePaths":[],"category":"voice","sessionMinutes":35}],"siteRules":[{"domain":"youtube.com"},{"domain":"bilibili.com"},{"domain":"youtube.com"}]}}
            """));

        Assert.True(result.CommandExecuted);
        SaveRuleSettingsCommand command = Assert.IsType<SaveRuleSettingsCommand>(handler.LastCommand);
        Assert.Equal(["game", "voice"], command.AppRules.Select(rule => rule.Id).ToArray());
        Assert.Equal(AppRuleCategory.Game, command.AppRules[0].Category);
        Assert.Equal(45, command.AppRules[0].SessionMinutes);
        Assert.Equal(
            ["bilibili.com", "youtube.com"],
            command.SiteRules.Select(rule => rule.Domain).ToArray());
    }

    [Fact]
    public async Task Dispatch_ClaimDueNoticeRequiresAndAcceptsEmptyPayload()
    {
        RecordingHandler handler = new();
        ServiceCommandDispatcher dispatcher = new(new JsonProtocolCodec(), handler);

        ProtocolDispatchResult result = await dispatcher.DispatchAsync(Message(
            "{\"version\":1,\"type\":\"claimDueNotice\",\"requestId\":\"notice\",\"payload\":{}}"));

        Assert.True(result.CommandExecuted);
        Assert.IsType<ClaimDueNoticeCommand>(handler.LastCommand);
    }

    [Theory]
    [InlineData("saveRuleSettings", "{}")]
    [InlineData("saveRuleSettings", "{\"appRules\":[],\"siteRules\":[],\"observedAtUtc\":\"2026-07-14T14:30:00Z\"}")]
    [InlineData("saveRuleSettings", "{\"appRules\":[],\"siteRules\":[],\"effectiveNight\":\"2026-07-15\"}")]
    [InlineData("saveRuleSettings", "{\"appRules\":[],\"siteRules\":[],\"isConfigured\":true}")]
    [InlineData("saveRuleSettings", "{\"AppRules\":[],\"siteRules\":[]}")]
    [InlineData("saveRuleSettings", "{\"appRules\":[],\"appRules\":[],\"siteRules\":[]}")]
    [InlineData("saveRuleSettings", "{\"appRules\":[{\"id\":\"game\",\"rootExecutablePath\":\"C:\\\\Games\\\\game.exe\",\"helperExecutablePaths\":[],\"category\":\"Game\",\"sessionMinutes\":35}],\"siteRules\":[]}")]
    [InlineData("saveRuleSettings", "{\"appRules\":[{\"id\":\"game\",\"rootExecutablePath\":\"C:\\\\Games\\\\game.exe\",\"helperExecutablePaths\":[],\"category\":\"game\",\"sessionMinutes\":14}],\"siteRules\":[]}")]
    [InlineData("saveRuleSettings", "{\"appRules\":[{\"id\":\"game\",\"rootExecutablePath\":\"C:\\\\Games\\\\game.exe\",\"helperExecutablePaths\":[],\"category\":\"game\",\"sessionMinutes\":35,\"extra\":true}],\"siteRules\":[]}")]
    [InlineData("saveRuleSettings", "{\"appRules\":[],\"siteRules\":[{\"domain\":\"example.com\"}]}")]
    [InlineData("saveRuleSettings", "{\"appRules\":[],\"siteRules\":[{\"Domain\":\"youtube.com\"}]}")]
    [InlineData("saveRuleSettings", "{\"appRules\":[],\"siteRules\":[{\"domain\":\"youtube.com\",\"domain\":\"youtube.com\"}]}")]
    [InlineData("saveRuleSettings", "{\"appRules\":[{\"id\":\"one\",\"rootExecutablePath\":\"C:\\\\Games\\\\one.exe\",\"helperExecutablePaths\":[\"C:\\\\Games\\\\shared.exe\"],\"category\":\"game\",\"sessionMinutes\":35},{\"id\":\"two\",\"rootExecutablePath\":\"C:\\\\Games\\\\two.exe\",\"helperExecutablePaths\":[\"c:\\\\games\\\\SHARED.exe\"],\"category\":\"game\",\"sessionMinutes\":35}],\"siteRules\":[]}")]
    [InlineData("saveRuleSettings", "{\"appRules\":[{\"id\":\"one\",\"rootExecutablePath\":\"C:\\\\Games\\\\one.exe\",\"helperExecutablePaths\":[],\"category\":\"game\",\"sessionMinutes\":35},{\"id\":\"two\",\"rootExecutablePath\":\"C:\\\\Games\\\\two.exe\",\"helperExecutablePaths\":[\"c:\\\\games\\\\ONE.exe\"],\"category\":\"game\",\"sessionMinutes\":35}],\"siteRules\":[]}")]
    [InlineData("claimDueNotice", "{\"extra\":true}")]
    [InlineData("claimDueNotice", "{\"nightDate\":\"2026-07-14\"}")]
    public async Task Dispatch_RuleAndNoticePayloadsAreExactAndServerTimed(
        string type,
        string payload)
    {
        RecordingHandler handler = new();
        ServiceCommandDispatcher dispatcher = new(new JsonProtocolCodec(), handler);

        ProtocolDispatchResult result = await dispatcher.DispatchAsync(Message(
            $"{{\"version\":1,\"type\":\"{type}\",\"requestId\":\"invalid\",\"payload\":{payload}}}"));

        Assert.False(result.CommandExecuted);
        Assert.Null(handler.LastCommand);
        using JsonDocument response = JsonDocument.Parse(result.ResponseUtf8);
        Assert.Equal(
            "malformedPayload",
            response.RootElement.GetProperty("payload").GetProperty("code").GetString());
    }

    private static byte[] Message(string json) => Encoding.UTF8.GetBytes(json);

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
