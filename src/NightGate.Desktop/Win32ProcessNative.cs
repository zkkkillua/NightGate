using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace NightGate.Desktop;

[Flags]
internal enum Win32ProcessAccess : uint
{
    None = 0,
    Terminate = 0x00000001,
    QueryLimitedInformation = 0x00001000,
    Synchronize = 0x00100000,
}

[Flags]
internal enum Win32TokenAccess : uint
{
    None = 0,
    Query = 0x00000008,
}

internal enum Win32ProcessWaitResult
{
    Alive,
    Exited,
    Failed,
}

internal static class Win32Error
{
    public const int Success = 0;
    public const int FileNotFound = 2;
    public const int AccessDenied = 5;
    public const int InvalidHandle = 6;
    public const int InvalidData = 13;
    public const int NoMoreFiles = 18;
    public const int NotSupported = 50;
    public const int InvalidParameter = 87;
    public const int CallNotImplemented = 120;
    public const int InsufficientBuffer = 122;
    public const int ProcNotFound = 127;
    public const int NotFound = 1168;
}

internal readonly record struct Win32StringCallResult(
    bool Succeeded,
    string? Value,
    int Error);

internal interface IWin32ProcessIdentityNative
{
    SafeWin32ProcessHandle? OpenProcess(
        int pid,
        Win32ProcessAccess access,
        out int error);

    bool TryGetProcessId(
        SafeWin32ProcessHandle process,
        out int pid,
        out int error);

    bool TryGetCreationFileTime(
        SafeWin32ProcessHandle process,
        out long creationFileTimeUtc,
        out int error);

    Win32StringCallResult QueryFullProcessImageName(
        SafeWin32ProcessHandle process,
        int capacity);

    SafeWin32TokenHandle? OpenProcessToken(
        SafeWin32ProcessHandle process,
        Win32TokenAccess access,
        out int error);

    bool TryGetTokenUserSid(
        SafeWin32TokenHandle token,
        out string sid,
        out int error);

    bool TryGetTokenSessionId(
        SafeWin32TokenHandle token,
        out uint sessionId,
        out int error);

    Win32ProcessWaitResult WaitForProcess(
        SafeWin32ProcessHandle process,
        out int error);
}

internal sealed class SafeWin32ProcessHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    private SafeWin32ProcessHandle()
        : base(ownsHandle: true)
    {
    }

    internal SafeWin32ProcessHandle(nint preexistingHandle, bool ownsHandle)
        : base(ownsHandle)
    {
        SetHandle(preexistingHandle);
    }

    protected override bool ReleaseHandle() => Win32HandleNative.Close(handle);
}

internal sealed class SafeWin32TokenHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    private SafeWin32TokenHandle()
        : base(ownsHandle: true)
    {
    }

    internal SafeWin32TokenHandle(nint preexistingHandle, bool ownsHandle)
        : base(ownsHandle)
    {
        SetHandle(preexistingHandle);
    }

    protected override bool ReleaseHandle() => Win32HandleNative.Close(handle);
}

internal sealed class Win32ProcessIdentityNative : IWin32ProcessIdentityNative
{
    private const uint WaitObject0 = 0;
    private const uint WaitTimeout = 258;
    private const uint WaitFailed = uint.MaxValue;
    private const int MaximumTokenInformationBytes = 1_048_576;

    public static Win32ProcessIdentityNative Instance { get; } = new();

    private Win32ProcessIdentityNative()
    {
    }

    public SafeWin32ProcessHandle? OpenProcess(
        int pid,
        Win32ProcessAccess access,
        out int error)
    {
        SafeWin32ProcessHandle? process = NativeMethods.OpenProcess(
            access,
            inheritHandle: false,
            unchecked((uint)pid));
        if (process is null || process.IsInvalid)
        {
            error = Marshal.GetLastPInvokeError();
            process?.Dispose();
            return null;
        }

        error = Win32Error.Success;
        return process;
    }

    public bool TryGetProcessId(
        SafeWin32ProcessHandle process,
        out int pid,
        out int error)
    {
        uint nativePid = NativeMethods.GetProcessId(process);
        if (nativePid == 0)
        {
            pid = 0;
            error = Marshal.GetLastPInvokeError();
            return false;
        }

        if (nativePid > int.MaxValue)
        {
            pid = 0;
            error = Win32Error.InvalidData;
            return false;
        }

        pid = (int)nativePid;
        error = Win32Error.Success;
        return true;
    }

    public bool TryGetCreationFileTime(
        SafeWin32ProcessHandle process,
        out long creationFileTimeUtc,
        out int error)
    {
        if (!NativeMethods.GetProcessTimes(
                process,
                out NativeFileTime creation,
                out _,
                out _,
                out _))
        {
            creationFileTimeUtc = 0;
            error = Marshal.GetLastPInvokeError();
            return false;
        }

        ulong value = ((ulong)creation.High << 32) | creation.Low;
        if (value > long.MaxValue)
        {
            creationFileTimeUtc = 0;
            error = Win32Error.InvalidData;
            return false;
        }

        creationFileTimeUtc = (long)value;
        error = Win32Error.Success;
        return true;
    }

    public Win32StringCallResult QueryFullProcessImageName(
        SafeWin32ProcessHandle process,
        int capacity)
    {
        if (capacity is <= 0
            or > Win32ExecutablePathCanonicalizer.MaximumQueryBufferCharacters)
        {
            return new(false, null, Win32Error.InvalidParameter);
        }

        StringBuilder buffer = new(capacity);
        uint size = (uint)capacity;
        if (!NativeMethods.QueryFullProcessImageName(
                process,
                flags: 0,
                buffer,
                ref size))
        {
            return new(false, null, Marshal.GetLastPInvokeError());
        }

        if (size == 0 || size >= capacity || size > int.MaxValue)
        {
            return new(false, null, Win32Error.InvalidData);
        }

        return new(
            true,
            buffer.ToString(startIndex: 0, length: (int)size),
            Win32Error.Success);
    }

    public SafeWin32TokenHandle? OpenProcessToken(
        SafeWin32ProcessHandle process,
        Win32TokenAccess access,
        out int error)
    {
        if (!NativeMethods.OpenProcessToken(process, access, out SafeWin32TokenHandle? token)
            || token is null
            || token.IsInvalid)
        {
            error = Marshal.GetLastPInvokeError();
            token?.Dispose();
            return null;
        }

        error = Win32Error.Success;
        return token;
    }

    public bool TryGetTokenUserSid(
        SafeWin32TokenHandle token,
        out string sid,
        out int error)
    {
        sid = string.Empty;
        _ = NativeMethods.GetTokenInformationBuffer(
            token,
            TokenInformationClass.User,
            nint.Zero,
            0,
            out uint required);
        int firstError = Marshal.GetLastPInvokeError();
        if (required == 0
            || required > MaximumTokenInformationBytes
            || firstError != Win32Error.InsufficientBuffer)
        {
            error = firstError == Win32Error.Success
                ? Win32Error.InvalidData
                : firstError;
            return false;
        }

        nint buffer = Marshal.AllocHGlobal(checked((int)required));
        try
        {
            if (!NativeMethods.GetTokenInformationBuffer(
                    token,
                    TokenInformationClass.User,
                    buffer,
                    required,
                    out uint written))
            {
                error = Marshal.GetLastPInvokeError();
                return false;
            }

            if (written > required || written < (uint)nint.Size)
            {
                error = Win32Error.InvalidData;
                return false;
            }

            nint sidPointer = Marshal.ReadIntPtr(buffer);
            if (sidPointer == nint.Zero)
            {
                error = Win32Error.InvalidData;
                return false;
            }

            try
            {
                SecurityIdentifier identifier = new(sidPointer);
                sid = identifier.Value;
            }
            catch (ArgumentException)
            {
                error = Win32Error.InvalidData;
                return false;
            }

            error = Win32Error.Success;
            return !string.IsNullOrWhiteSpace(sid);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    public bool TryGetTokenSessionId(
        SafeWin32TokenHandle token,
        out uint sessionId,
        out int error)
    {
        if (!NativeMethods.GetTokenInformationSession(
                token,
                TokenInformationClass.SessionId,
                out sessionId,
                sizeof(uint),
                out uint written))
        {
            error = Marshal.GetLastPInvokeError();
            return false;
        }

        if (written != sizeof(uint))
        {
            sessionId = 0;
            error = Win32Error.InvalidData;
            return false;
        }

        error = Win32Error.Success;
        return true;
    }

    public Win32ProcessWaitResult WaitForProcess(
        SafeWin32ProcessHandle process,
        out int error)
    {
        uint result = NativeMethods.WaitForSingleObject(process, milliseconds: 0);
        switch (result)
        {
            case WaitTimeout:
                error = Win32Error.Success;
                return Win32ProcessWaitResult.Alive;
            case WaitObject0:
                error = Win32Error.Success;
                return Win32ProcessWaitResult.Exited;
            case WaitFailed:
                error = Marshal.GetLastPInvokeError();
                return Win32ProcessWaitResult.Failed;
            default:
                error = Win32Error.InvalidData;
                return Win32ProcessWaitResult.Failed;
        }
    }

    private enum TokenInformationClass
    {
        User = 1,
        SessionId = 12,
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct NativeFileTime
    {
        public readonly uint Low;
        public readonly uint High;
    }

    private static class NativeMethods
    {
        [DllImport("kernel32.dll", EntryPoint = "OpenProcess", SetLastError = true)]
        internal static extern SafeWin32ProcessHandle OpenProcess(
            Win32ProcessAccess desiredAccess,
            [MarshalAs(UnmanagedType.Bool)] bool inheritHandle,
            uint processId);

        [DllImport("kernel32.dll", EntryPoint = "GetProcessId", SetLastError = true)]
        internal static extern uint GetProcessId(SafeWin32ProcessHandle process);

        [DllImport("kernel32.dll", EntryPoint = "GetProcessTimes", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetProcessTimes(
            SafeWin32ProcessHandle process,
            out NativeFileTime creation,
            out NativeFileTime exit,
            out NativeFileTime kernel,
            out NativeFileTime user);

        [DllImport(
            "kernel32.dll",
            EntryPoint = "QueryFullProcessImageNameW",
            CharSet = CharSet.Unicode,
            ExactSpelling = true,
            SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool QueryFullProcessImageName(
            SafeWin32ProcessHandle process,
            uint flags,
            StringBuilder imageName,
            ref uint size);

        [DllImport("advapi32.dll", EntryPoint = "OpenProcessToken", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool OpenProcessToken(
            SafeWin32ProcessHandle process,
            Win32TokenAccess desiredAccess,
            out SafeWin32TokenHandle token);

        [DllImport("advapi32.dll", EntryPoint = "GetTokenInformation", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetTokenInformationBuffer(
            SafeWin32TokenHandle token,
            TokenInformationClass informationClass,
            nint information,
            uint informationLength,
            out uint returnLength);

        [DllImport("advapi32.dll", EntryPoint = "GetTokenInformation", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetTokenInformationSession(
            SafeWin32TokenHandle token,
            TokenInformationClass informationClass,
            out uint information,
            uint informationLength,
            out uint returnLength);

        [DllImport("kernel32.dll", EntryPoint = "WaitForSingleObject", SetLastError = true)]
        internal static extern uint WaitForSingleObject(
            SafeWin32ProcessHandle process,
            uint milliseconds);
    }
}

internal static class Win32HandleNative
{
    public static bool Close(nint handle) => CloseHandle(handle);

    [DllImport("kernel32.dll", EntryPoint = "CloseHandle", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(nint handle);
}
