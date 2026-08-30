using System.Runtime.InteropServices;
using System.Security.Principal;

namespace NightGate.Desktop;

public sealed record CurrentInteractiveIdentity(string UserSid, int SessionId);

public interface ICurrentInteractiveIdentityProvider
{
    CurrentInteractiveIdentity? Read();
}

internal interface ICurrentInteractiveIdentityNative
{
    string? ReadCurrentUserSid();

    bool TryReadCurrentProcessSessionId(out int sessionId);
}

public sealed class WindowsCurrentInteractiveIdentityProvider :
    ICurrentInteractiveIdentityProvider
{
    private readonly ICurrentInteractiveIdentityNative _native;

    public WindowsCurrentInteractiveIdentityProvider()
        : this(new WindowsCurrentInteractiveIdentityNative())
    {
    }

    internal WindowsCurrentInteractiveIdentityProvider(
        ICurrentInteractiveIdentityNative native)
    {
        ArgumentNullException.ThrowIfNull(native);
        _native = native;
    }

    public CurrentInteractiveIdentity? Read()
    {
        try
        {
            string? sid = _native.ReadCurrentUserSid();
            if (string.IsNullOrWhiteSpace(sid)
                || !sid.StartsWith("S-", StringComparison.OrdinalIgnoreCase)
                || !_native.TryReadCurrentProcessSessionId(out int sessionId)
                || sessionId < 0)
            {
                return null;
            }

            return new(sid, sessionId);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException
            and not StackOverflowException
            and not AccessViolationException)
        {
            return null;
        }
    }
}

internal sealed class WindowsCurrentInteractiveIdentityNative :
    ICurrentInteractiveIdentityNative
{
    public string? ReadCurrentUserSid()
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        using WindowsIdentity identity = WindowsIdentity.GetCurrent();
        return identity.User?.Value;
    }

    public bool TryReadCurrentProcessSessionId(out int sessionId)
    {
        sessionId = -1;
        if (!OperatingSystem.IsWindows()
            || !NativeMethods.ProcessIdToSessionId(
                checked((uint)Environment.ProcessId),
                out uint nativeSessionId)
            || nativeSessionId > int.MaxValue)
        {
            return false;
        }

        sessionId = (int)nativeSessionId;
        return true;
    }

    private static class NativeMethods
    {
        [DllImport(
            "kernel32.dll",
            EntryPoint = "ProcessIdToSessionId",
            ExactSpelling = true,
            SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool ProcessIdToSessionId(
            uint processId,
            out uint sessionId);
    }
}
