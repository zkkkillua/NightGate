namespace NightGate.Desktop;

public sealed class Win32WorkstationLocker : IWorkstationLocker
{
    private readonly IWorkstationLockNative _native;

    public Win32WorkstationLocker()
        : this(new Win32WorkstationLockNative())
    {
    }

    internal Win32WorkstationLocker(IWorkstationLockNative native)
    {
        ArgumentNullException.ThrowIfNull(native);
        _native = native;
    }

    public bool TryLock() => _native.TryLock();
}
