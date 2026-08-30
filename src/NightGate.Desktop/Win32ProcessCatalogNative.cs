using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace NightGate.Desktop;

internal readonly record struct Win32ProcessCatalogEntry(
    int ProcessId,
    int ParentProcessId,
    string ExecutableName);

internal enum Win32ProcessCatalogMoveStatus
{
    Entry,
    Completed,
    Failed,
}

internal readonly record struct Win32ProcessCatalogMoveResult(
    Win32ProcessCatalogMoveStatus Status,
    Win32ProcessCatalogEntry? Value,
    int Error)
{
    internal static Win32ProcessCatalogMoveResult Entry(
        Win32ProcessCatalogEntry value) => new(
        Win32ProcessCatalogMoveStatus.Entry,
        value,
        Win32Error.Success);

    internal static Win32ProcessCatalogMoveResult Completed() => new(
        Win32ProcessCatalogMoveStatus.Completed,
        null,
        Win32Error.NoMoreFiles);

    internal static Win32ProcessCatalogMoveResult Failure(int error) => new(
        Win32ProcessCatalogMoveStatus.Failed,
        null,
        error);
}

internal interface IWin32ProcessCatalogNative
{
    SafeWin32ProcessSnapshotHandle? CreateProcessSnapshot(out int error);

    Win32ProcessCatalogMoveResult ReadFirst(
        SafeWin32ProcessSnapshotHandle snapshot);

    Win32ProcessCatalogMoveResult ReadNext(
        SafeWin32ProcessSnapshotHandle snapshot);
}

internal sealed class SafeWin32ProcessSnapshotHandle
    : SafeHandleZeroOrMinusOneIsInvalid
{
    private SafeWin32ProcessSnapshotHandle()
        : base(ownsHandle: true)
    {
    }

    internal SafeWin32ProcessSnapshotHandle(nint preexistingHandle, bool ownsHandle)
        : base(ownsHandle)
    {
        SetHandle(preexistingHandle);
    }

    protected override bool ReleaseHandle() => Win32HandleNative.Close(handle);
}

internal sealed class Win32ProcessCatalogNative : IWin32ProcessCatalogNative
{
    private const uint SnapshotProcesses = 0x00000002;
    private const int MaximumPath = 260;

    internal static Win32ProcessCatalogNative Instance { get; } = new();

    private Win32ProcessCatalogNative()
    {
    }

    public SafeWin32ProcessSnapshotHandle? CreateProcessSnapshot(out int error)
    {
        SafeWin32ProcessSnapshotHandle? snapshot =
            NativeMethods.CreateToolhelp32Snapshot(SnapshotProcesses, 0);
        if (snapshot is null || snapshot.IsInvalid || snapshot.IsClosed)
        {
            error = Marshal.GetLastPInvokeError();
            snapshot?.Dispose();
            return null;
        }

        error = Win32Error.Success;
        return snapshot;
    }

    public Win32ProcessCatalogMoveResult ReadFirst(
        SafeWin32ProcessSnapshotHandle snapshot) => Read(snapshot, first: true);

    public Win32ProcessCatalogMoveResult ReadNext(
        SafeWin32ProcessSnapshotHandle snapshot) => Read(snapshot, first: false);

    private static Win32ProcessCatalogMoveResult Read(
        SafeWin32ProcessSnapshotHandle snapshot,
        bool first)
    {
        NativeProcessEntry entry = new()
        {
            Size = checked((uint)Marshal.SizeOf<NativeProcessEntry>()),
        };
        bool succeeded = first
            ? NativeMethods.Process32First(snapshot, ref entry)
            : NativeMethods.Process32Next(snapshot, ref entry);
        if (!succeeded)
        {
            int error = Marshal.GetLastPInvokeError();
            return error == Win32Error.NoMoreFiles
                ? Win32ProcessCatalogMoveResult.Completed()
                : Win32ProcessCatalogMoveResult.Failure(error);
        }

        if (entry.ProcessId > int.MaxValue
            || entry.ParentProcessId > int.MaxValue
            || string.IsNullOrWhiteSpace(entry.ExecutableFile))
        {
            return Win32ProcessCatalogMoveResult.Failure(Win32Error.InvalidData);
        }

        return Win32ProcessCatalogMoveResult.Entry(new(
            (int)entry.ProcessId,
            (int)entry.ParentProcessId,
            entry.ExecutableFile));
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NativeProcessEntry
    {
        internal uint Size;
        internal uint Usage;
        internal uint ProcessId;
        internal nuint DefaultHeapId;
        internal uint ModuleId;
        internal uint Threads;
        internal uint ParentProcessId;
        internal int BasePriority;
        internal uint Flags;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = MaximumPath)]
        internal string ExecutableFile;
    }

    private static class NativeMethods
    {
        [DllImport(
            "kernel32.dll",
            EntryPoint = "CreateToolhelp32Snapshot",
            ExactSpelling = true,
            SetLastError = true)]
        internal static extern SafeWin32ProcessSnapshotHandle CreateToolhelp32Snapshot(
            uint flags,
            uint processId);

        [DllImport(
            "kernel32.dll",
            EntryPoint = "Process32FirstW",
            CharSet = CharSet.Unicode,
            ExactSpelling = true,
            SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool Process32First(
            SafeWin32ProcessSnapshotHandle snapshot,
            ref NativeProcessEntry entry);

        [DllImport(
            "kernel32.dll",
            EntryPoint = "Process32NextW",
            CharSet = CharSet.Unicode,
            ExactSpelling = true,
            SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool Process32Next(
            SafeWin32ProcessSnapshotHandle snapshot,
            ref NativeProcessEntry entry);
    }
}
