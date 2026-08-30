using System.Runtime.Versioning;
using System.Security.Principal;

namespace NightGate.Service;

public interface IWindowsSidResolver
{
    string ResolveAccountSid(string accountName);

    string GetCurrentIdentitySid();
}

[SupportedOSPlatform("windows")]
public sealed class WindowsSidResolver : IWindowsSidResolver
{
    public string ResolveAccountSid(string accountName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accountName);
        NTAccount account = new(accountName);
        SecurityIdentifier sid = (SecurityIdentifier)account.Translate(typeof(SecurityIdentifier));
        return sid.Value;
    }

    public string GetCurrentIdentitySid()
    {
        using WindowsIdentity identity = WindowsIdentity.GetCurrent(TokenAccessLevels.Query);
        return identity.User?.Value
            ?? throw new InvalidOperationException("The current Windows identity has no SID.");
    }
}
