using System.Runtime.InteropServices;

namespace NightGate.Desktop;

internal interface IWorkstationLockNative
{
    bool TryLock();
}

internal sealed class Win32WorkstationLockNative : IWorkstationLockNative
{
    public bool TryLock() => LockWorkStation();

    [DllImport(
        "user32.dll",
        EntryPoint = "LockWorkStation",
        ExactSpelling = true,
        SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool LockWorkStation();
}
