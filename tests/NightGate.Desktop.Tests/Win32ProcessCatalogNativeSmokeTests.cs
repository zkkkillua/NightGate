namespace NightGate.Desktop.Tests;

public sealed class Win32ProcessCatalogNativeSmokeTests
{
    [Fact]
    public void EnumeratesCurrentProcessAndCompletesOnlyAtNoMoreFiles()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        IWin32ProcessCatalogNative native = Win32ProcessCatalogNative.Instance;
        using SafeWin32ProcessSnapshotHandle? snapshot =
            native.CreateProcessSnapshot(out int createError);
        Assert.Equal(Win32Error.Success, createError);
        Assert.NotNull(snapshot);
        Assert.False(snapshot!.IsInvalid);

        bool sawCurrentProcess = false;
        int rows = 0;
        Win32ProcessCatalogMoveResult move = native.ReadFirst(snapshot);
        while (move.Status == Win32ProcessCatalogMoveStatus.Entry)
        {
            Assert.True(++rows <= 131_072);
            Win32ProcessCatalogEntry row = Assert.IsType<Win32ProcessCatalogEntry>(
                move.Value);
            if (row.ProcessId == Environment.ProcessId)
            {
                sawCurrentProcess = true;
            }

            move = native.ReadNext(snapshot);
        }

        Assert.Equal(Win32ProcessCatalogMoveStatus.Completed, move.Status);
        Assert.Equal(Win32Error.NoMoreFiles, move.Error);
        Assert.True(sawCurrentProcess);
    }
}
