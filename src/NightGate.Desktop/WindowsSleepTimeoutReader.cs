using System.Runtime.InteropServices;

namespace NightGate.Desktop;

internal interface IWindowsSleepTimeoutNative
{
    bool TryRead(
        out byte acLineStatus,
        out uint acSeconds,
        out uint batterySeconds);
}

public sealed class WindowsSleepTimeoutReader : ISleepTimeoutReader
{
    private readonly IWindowsSleepTimeoutNative _native;

    public WindowsSleepTimeoutReader()
        : this(new WindowsSleepTimeoutNative())
    {
    }

    internal WindowsSleepTimeoutReader(IWindowsSleepTimeoutNative native)
    {
        ArgumentNullException.ThrowIfNull(native);
        _native = native;
    }

    public SleepTimeoutSnapshot? Read()
    {
        try
        {
            if (!_native.TryRead(
                    out byte acLineStatus,
                    out uint acSeconds,
                    out uint batterySeconds))
            {
                return null;
            }

            DesktopPowerSource source = acLineStatus switch
            {
                0 => DesktopPowerSource.Battery,
                1 => DesktopPowerSource.Ac,
                _ => DesktopPowerSource.Unknown,
            };
            return new(
                source,
                TimeSpan.FromSeconds(acSeconds),
                TimeSpan.FromSeconds(batterySeconds));
        }
        catch (Exception exception) when (exception is not OutOfMemoryException
            and not StackOverflowException
            and not AccessViolationException)
        {
            return null;
        }
    }
}

internal sealed class WindowsSleepTimeoutNative : IWindowsSleepTimeoutNative
{
    private static readonly Guid SleepSubgroup =
        new("238C9FA8-0AAD-41ED-83F4-97BE242C8F20");
    private static readonly Guid SleepIdleTimeout =
        new("29F6C1DB-86DA-48C5-9FDB-F2B67B1F44DA");

    public bool TryRead(
        out byte acLineStatus,
        out uint acSeconds,
        out uint batterySeconds)
    {
        acLineStatus = byte.MaxValue;
        acSeconds = 0;
        batterySeconds = 0;
        if (!OperatingSystem.IsWindows()
            || NativeMethods.PowerGetActiveScheme(nint.Zero, out nint schemePointer) != 0
            || schemePointer == nint.Zero)
        {
            return false;
        }

        try
        {
            Guid scheme = Marshal.PtrToStructure<Guid>(schemePointer);
            Guid subgroup = SleepSubgroup;
            Guid setting = SleepIdleTimeout;
            if (NativeMethods.PowerReadACValueIndex(
                    nint.Zero,
                    ref scheme,
                    ref subgroup,
                    ref setting,
                    out acSeconds) != 0
                || NativeMethods.PowerReadDCValueIndex(
                    nint.Zero,
                    ref scheme,
                    ref subgroup,
                    ref setting,
                    out batterySeconds) != 0
                || !NativeMethods.GetSystemPowerStatus(out SystemPowerStatus status))
            {
                return false;
            }

            acLineStatus = status.ACLineStatus;
            return true;
        }
        finally
        {
            _ = NativeMethods.LocalFree(schemePointer);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SystemPowerStatus
    {
        internal byte ACLineStatus;
        internal byte BatteryFlag;
        internal byte BatteryLifePercent;
        internal byte SystemStatusFlag;
        internal uint BatteryLifeTime;
        internal uint BatteryFullLifeTime;
    }

    private static class NativeMethods
    {
        [DllImport(
            "powrprof.dll",
            EntryPoint = "PowerGetActiveScheme",
            ExactSpelling = true)]
        internal static extern uint PowerGetActiveScheme(
            nint userRootPowerKey,
            out nint activePolicyGuid);

        [DllImport(
            "powrprof.dll",
            EntryPoint = "PowerReadACValueIndex",
            ExactSpelling = true)]
        internal static extern uint PowerReadACValueIndex(
            nint rootPowerKey,
            ref Guid schemeGuid,
            ref Guid subgroupOfPowerSettingsGuid,
            ref Guid powerSettingGuid,
            out uint valueIndex);

        [DllImport(
            "powrprof.dll",
            EntryPoint = "PowerReadDCValueIndex",
            ExactSpelling = true)]
        internal static extern uint PowerReadDCValueIndex(
            nint rootPowerKey,
            ref Guid schemeGuid,
            ref Guid subgroupOfPowerSettingsGuid,
            ref Guid powerSettingGuid,
            out uint valueIndex);

        [DllImport(
            "kernel32.dll",
            EntryPoint = "GetSystemPowerStatus",
            ExactSpelling = true,
            SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetSystemPowerStatus(
            out SystemPowerStatus systemPowerStatus);

        [DllImport(
            "kernel32.dll",
            EntryPoint = "LocalFree",
            ExactSpelling = true,
            SetLastError = true)]
        internal static extern nint LocalFree(nint memory);
    }
}
