using System.Collections.Immutable;
using System.Runtime.InteropServices;

namespace NightGate.Desktop;

internal enum Win32TopLevelWindowEnumerationStatus
{
    Complete,
    Unavailable,
    Ambiguous,
}

internal readonly record struct Win32TopLevelWindowEnumerationResult(
    Win32TopLevelWindowEnumerationStatus Status,
    ImmutableArray<nint> Windows,
    int Error);

internal enum Win32WindowProbeStatus
{
    Success,
    Unavailable,
    Ambiguous,
}

internal readonly record struct Win32TopLevelWindowState(
    int ProcessId,
    bool IsVisible,
    bool IsEnabled,
    nint Owner);

internal readonly record struct Win32TopLevelWindowProbeResult(
    Win32WindowProbeStatus Status,
    Win32TopLevelWindowState State,
    int Error);

internal interface IWin32ExactProcessActionNative
{
    Win32TopLevelWindowEnumerationResult EnumerateTopLevelWindows();

    Win32TopLevelWindowProbeResult ProbeTopLevelWindow(nint window);

    Win32ProcessWaitResult WaitForProcess(
        SafeWin32ProcessHandle process,
        out int error);

    bool TryPostMessage(
        nint window,
        uint message,
        nuint wParam,
        nint lParam,
        out int error);

    bool TryTerminate(
        SafeWin32ProcessHandle process,
        uint exitCode,
        out int error);
}

internal sealed class Win32ExactProcessActionNative : IWin32ExactProcessActionNative
{
    internal const int MaximumTopLevelWindowCount = 16_384;

    private const uint GetWindowOwner = 4;

    public static Win32ExactProcessActionNative Instance { get; } = new();

    private Win32ExactProcessActionNative()
    {
    }

    public Win32TopLevelWindowEnumerationResult EnumerateTopLevelWindows()
    {
        List<nint> windows = new(capacity: 256);
        HashSet<nint> unique = [];
        bool callbackFault = false;
        EnumWindowsCallback callback = (window, _) =>
        {
            try
            {
                if (window == nint.Zero
                    || windows.Count >= MaximumTopLevelWindowCount
                    || !unique.Add(window))
                {
                    callbackFault = true;
                    return false;
                }

                windows.Add(window);
                return true;
            }
            catch (Exception)
            {
                callbackFault = true;
                return false;
            }
        };

        Marshal.SetLastPInvokeError(Win32Error.Success);
        bool completed = NativeMethods.EnumWindows(callback, nint.Zero);
        GC.KeepAlive(callback);
        int error = Marshal.GetLastPInvokeError();

        if (callbackFault)
        {
            return new(
                Win32TopLevelWindowEnumerationStatus.Ambiguous,
                ImmutableArray<nint>.Empty,
                error == Win32Error.Success ? Win32Error.InvalidData : error);
        }

        if (!completed)
        {
            return new(
                Win32TopLevelWindowEnumerationStatus.Unavailable,
                ImmutableArray<nint>.Empty,
                error == Win32Error.Success ? Win32Error.InvalidData : error);
        }

        return new(
            Win32TopLevelWindowEnumerationStatus.Complete,
            windows.ToImmutableArray(),
            Win32Error.Success);
    }

    public Win32TopLevelWindowProbeResult ProbeTopLevelWindow(nint window)
    {
        if (window == nint.Zero)
        {
            return AmbiguousProbe(Win32Error.InvalidHandle);
        }

        Marshal.SetLastPInvokeError(Win32Error.Success);
        uint threadId = NativeMethods.GetWindowThreadProcessId(window, out uint nativePid);
        int pidError = Marshal.GetLastPInvokeError();
        if (threadId == 0 || nativePid == 0 || nativePid > int.MaxValue)
        {
            return FailedProbe(
                pidError == Win32Error.Success ? Win32Error.InvalidHandle : pidError);
        }

        bool visible = NativeMethods.IsWindowVisible(window);
        bool enabled = NativeMethods.IsWindowEnabled(window);

        Marshal.SetLastPInvokeError(Win32Error.Success);
        nint owner = NativeMethods.GetWindow(window, GetWindowOwner);
        int ownerError = Marshal.GetLastPInvokeError();
        if (owner == nint.Zero && ownerError != Win32Error.Success)
        {
            return FailedProbe(ownerError);
        }

        return new(
            Win32WindowProbeStatus.Success,
            new Win32TopLevelWindowState((int)nativePid, visible, enabled, owner),
            Win32Error.Success);
    }

    public Win32ProcessWaitResult WaitForProcess(
        SafeWin32ProcessHandle process,
        out int error) =>
        Win32ProcessIdentityNative.Instance.WaitForProcess(process, out error);

    public bool TryPostMessage(
        nint window,
        uint message,
        nuint wParam,
        nint lParam,
        out int error)
    {
        Marshal.SetLastPInvokeError(Win32Error.Success);
        if (NativeMethods.PostMessage(window, message, wParam, lParam))
        {
            error = Win32Error.Success;
            return true;
        }

        error = Marshal.GetLastPInvokeError();
        if (error == Win32Error.Success)
        {
            error = Win32Error.InvalidData;
        }

        return false;
    }

    public bool TryTerminate(
        SafeWin32ProcessHandle process,
        uint exitCode,
        out int error)
    {
        Marshal.SetLastPInvokeError(Win32Error.Success);
        if (NativeMethods.TerminateProcess(process, exitCode))
        {
            error = Win32Error.Success;
            return true;
        }

        error = Marshal.GetLastPInvokeError();
        if (error == Win32Error.Success)
        {
            error = Win32Error.InvalidData;
        }

        return false;
    }

    private static Win32TopLevelWindowProbeResult FailedProbe(int error) =>
        IsUnavailable(error)
            ? new(
                Win32WindowProbeStatus.Unavailable,
                default,
                error)
            : AmbiguousProbe(error);

    private static Win32TopLevelWindowProbeResult AmbiguousProbe(int error) =>
        new(Win32WindowProbeStatus.Ambiguous, default, error);

    private static bool IsUnavailable(int error) => error is
        Win32Error.AccessDenied
        or Win32Error.CallNotImplemented
        or Win32Error.NotSupported
        or Win32Error.ProcNotFound;

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private delegate bool EnumWindowsCallback(nint window, nint parameter);

    private static class NativeMethods
    {
        [DllImport(
            "user32.dll",
            EntryPoint = "EnumWindows",
            ExactSpelling = true,
            SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool EnumWindows(
            EnumWindowsCallback callback,
            nint parameter);

        [DllImport(
            "user32.dll",
            EntryPoint = "GetWindowThreadProcessId",
            ExactSpelling = true,
            SetLastError = true)]
        internal static extern uint GetWindowThreadProcessId(
            nint window,
            out uint processId);

        [DllImport(
            "user32.dll",
            EntryPoint = "IsWindowVisible",
            ExactSpelling = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool IsWindowVisible(nint window);

        [DllImport(
            "user32.dll",
            EntryPoint = "IsWindowEnabled",
            ExactSpelling = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool IsWindowEnabled(nint window);

        [DllImport(
            "user32.dll",
            EntryPoint = "GetWindow",
            ExactSpelling = true,
            SetLastError = true)]
        internal static extern nint GetWindow(nint window, uint command);

        [DllImport(
            "user32.dll",
            EntryPoint = "PostMessageW",
            ExactSpelling = true,
            SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool PostMessage(
            nint window,
            uint message,
            nuint wParam,
            nint lParam);

        [DllImport(
            "kernel32.dll",
            EntryPoint = "TerminateProcess",
            ExactSpelling = true,
            SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool TerminateProcess(
            SafeWin32ProcessHandle process,
            uint exitCode);
    }
}
