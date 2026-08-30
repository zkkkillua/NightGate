using NightGate.Desktop;

namespace NightGate.Desktop.Tests;

public sealed class Win32ProcessIdentitySmokeTests
{
    [Fact]
    [Trait("Category", "WindowsSmoke")]
    public void ProductionReaderCanInspectTheCurrentTestProcessWithoutMutation()
    {
        using Win32ProcessIdentityReadResult result = new Win32ProcessIdentityReader()
            .OpenAndRead(
                Environment.ProcessId,
                Win32ProcessAccess.QueryLimitedInformation
                    | Win32ProcessAccess.Synchronize);

        Assert.Equal(Win32ProcessIdentityReadStatus.Success, result.Status);
        Assert.NotNull(result.Handle);
        Assert.NotNull(result.Identity);
        Assert.Equal(Environment.ProcessId, result.Identity.Key.Pid);
        Assert.Equal(TimeSpan.Zero, result.Identity.CreationInstantUtc.Offset);
        Assert.EndsWith(".exe", result.Identity.ExecutablePath, StringComparison.OrdinalIgnoreCase);
        Assert.StartsWith("S-", result.Identity.UserSid, StringComparison.OrdinalIgnoreCase);
        Assert.True(result.Identity.SessionId >= 0);
    }
}
