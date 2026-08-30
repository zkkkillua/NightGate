using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;

namespace NightGate.Service;

public static class SystemNamedPipeSecurity
{
    public static PipeSecurity Create(string configuredUserSid)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(configuredUserSid);
        SecurityIdentifier configuredUser = new(configuredUserSid);
        SecurityIdentifier localService = new(WellKnownSidType.LocalServiceSid, domainSid: null);
        SecurityIdentifier localSystem = new(WellKnownSidType.LocalSystemSid, domainSid: null);

        PipeSecurity security = new();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new PipeAccessRule(
            configuredUser,
            PipeAccessRights.ReadWrite | PipeAccessRights.Synchronize,
            AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(
            localService,
            PipeAccessRights.FullControl,
            AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(
            localSystem,
            PipeAccessRights.FullControl,
            AccessControlType.Allow));
        return security;
    }
}
