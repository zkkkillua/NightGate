using System.Reflection;
using NightGate.Desktop;

namespace NightGate.Desktop.Tests;

public sealed class Win32WorkstationLockerContractTests
{
    [Fact]
    public void DesktopAssembly_ExposesOneNarrowWorkstationLockerBoundary()
    {
        Assembly desktop = typeof(IWorkstationLocker).Assembly;

        Assert.NotNull(desktop.GetType("NightGate.Desktop.IWorkstationLockNative"));
        Assert.NotNull(desktop.GetType("NightGate.Desktop.Win32WorkstationLockNative"));
        Assert.NotNull(desktop.GetType("NightGate.Desktop.Win32WorkstationLocker"));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Adapter_DelegatesExactlyOnceAndReturnsNativeResult(bool nativeResult)
    {
        RecordingNative native = new() { Result = nativeResult };
        Win32WorkstationLocker locker = new(native);

        bool result = locker.TryLock();

        Assert.Equal(nativeResult, result);
        Assert.Equal(1, native.CallCount);
    }

    private sealed class RecordingNative : IWorkstationLockNative
    {
        public bool Result { get; init; }

        public int CallCount { get; private set; }

        public bool TryLock()
        {
            CallCount++;
            return Result;
        }
    }
}
