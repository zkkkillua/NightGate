using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using NightGate.Protocol;
using NightGate.Service;

namespace NightGate.Service.Tests;

public sealed class SystemNamedPipeSecurityTests
{
    private const string ConfiguredUserSid = "S-1-5-21-1-2-3-1001";

    [Fact]
    public void Create_ReturnsProtectedAclWithOnlyRequiredAllowIdentities()
    {
        PipeSecurity security = SystemNamedPipeSecurity.Create(ConfiguredUserSid);

        PipeAccessRule[] rules = GetAccessRules(security);
        string[] identities = rules
            .Select(rule => rule.IdentityReference.Value)
            .ToArray();

        Assert.True(security.AreAccessRulesProtected);
        Assert.Equal(3, rules.Length);
        Assert.All(rules, rule => Assert.Equal(AccessControlType.Allow, rule.AccessControlType));
        AssertRule(
            rules,
            ConfiguredUserSid,
            PipeAccessRights.ReadWrite | PipeAccessRights.Synchronize);
        AssertRule(rules, WellKnownSid(WellKnownSidType.LocalServiceSid), PipeAccessRights.FullControl);
        AssertRule(rules, WellKnownSid(WellKnownSidType.LocalSystemSid), PipeAccessRights.FullControl);

        string[] forbiddenIdentities =
        [
            WellKnownSid(WellKnownSidType.WorldSid),
            WellKnownSid(WellKnownSidType.BuiltinUsersSid),
            WellKnownSid(WellKnownSidType.AuthenticatedUserSid),
            WellKnownSid(WellKnownSidType.AnonymousSid),
            WellKnownSid(WellKnownSidType.BuiltinAdministratorsSid),
        ];
        Assert.All(forbiddenIdentities, identity => Assert.DoesNotContain(identity, identities));
    }

    [Fact]
    public void Create_ConfiguredUserRuleExcludesElevatedRights()
    {
        PipeSecurity security = SystemNamedPipeSecurity.Create(ConfiguredUserSid);

        PipeAccessRights rights = Assert.Single(
            GetAccessRules(security),
            rule => rule.IdentityReference.Value == ConfiguredUserSid).PipeAccessRights;

        Assert.Equal(PipeAccessRights.ReadWrite | PipeAccessRights.Synchronize, rights);
        Assert.False(rights.HasFlag(PipeAccessRights.CreateNewInstance));
        Assert.False(rights.HasFlag(PipeAccessRights.ChangePermissions));
        Assert.False(rights.HasFlag(PipeAccessRights.TakeOwnership));
        Assert.NotEqual(PipeAccessRights.FullControl, rights);
    }

    [Fact]
    public void Factory_CanonicalizesAndExposesConfiguredUserSid()
    {
        SystemNamedPipeServerFactory factory = new(
            "NightGateCanonicalSidTest",
            "s-1-5-21-1-2-3-1001");

        Assert.Equal(ConfiguredUserSid, factory.ConfiguredUserSid);
    }

    [Fact]
    public async Task Factory_CurrentUserCanCompleteDuplexFramedRoundTrip()
    {
        using WindowsIdentity identity = WindowsIdentity.GetCurrent(TokenAccessLevels.Query);
        string currentUserSid = identity.User?.Value
            ?? throw new InvalidOperationException("The test identity has no Windows SID.");
        string pipeName = $"NightGateServiceTests-{Guid.NewGuid():N}";
        SystemNamedPipeServerFactory factory = new(pipeName, currentUserSid);
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));

        Task<IPipeConnection> acceptTask = factory
            .AcceptConnectionAsync(timeout.Token)
            .AsTask();
        await using NamedPipeClientStream client = new(
            ".",
            pipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous,
            TokenImpersonationLevel.Impersonation);
        await client.ConnectAsync(timeout.Token);
        await using IPipeConnection server = await acceptTask;

        byte[] request = Encoding.UTF8.GetBytes("request-frame");
        byte[] response = Encoding.UTF8.GetBytes("response-frame");
        Task<ReadOnlyMemory<byte>> requestRead = server
            .ReadMessageAsync(timeout.Token)
            .AsTask();
        await ProtocolFraming.WriteFrameAsync(client, request, timeout.Token);
        Assert.Equal(request, (await requestRead).ToArray());

        Task<ReadOnlyMemory<byte>> responseRead = ProtocolFraming
            .ReadFrameAsync(client, timeout.Token)
            .AsTask();
        await server.WriteMessageAsync(response, timeout.Token);
        Assert.Equal(response, (await responseRead).ToArray());
    }

    private static PipeAccessRule[] GetAccessRules(PipeSecurity security) =>
        security
            .GetAccessRules(
                includeExplicit: true,
                includeInherited: true,
                targetType: typeof(SecurityIdentifier))
            .Cast<PipeAccessRule>()
            .ToArray();

    private static void AssertRule(
        IEnumerable<PipeAccessRule> rules,
        string identity,
        PipeAccessRights rights)
    {
        PipeAccessRule rule = Assert.Single(
            rules,
            candidate => candidate.IdentityReference.Value == identity);
        Assert.Equal(rights, rule.PipeAccessRights);
    }

    private static string WellKnownSid(WellKnownSidType sidType) =>
        new SecurityIdentifier(sidType, domainSid: null).Value;
}
