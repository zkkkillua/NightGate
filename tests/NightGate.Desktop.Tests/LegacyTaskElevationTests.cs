using NightGate.Desktop;

namespace NightGate.Desktop.Tests;

public sealed class LegacyTaskElevationTests
{
    [Theory]
    [InlineData(0, "disable", LegacyTaskMutationStatus.Disabled)]
    [InlineData(0, "restore", LegacyTaskMutationStatus.Restored)]
    [InlineData(1, "disable", LegacyTaskMutationStatus.Unchanged)]
    [InlineData(2, "disable", LegacyTaskMutationStatus.Changed)]
    [InlineData(3, "disable", LegacyTaskMutationStatus.Missing)]
    [InlineData(5, "disable", LegacyTaskMutationStatus.Invalid)]
    [InlineData(99, "disable", LegacyTaskMutationStatus.Unavailable)]
    public void ExitCodeMapping_PreservesExactElevatedMutationOutcome(
        int exitCode,
        string operation,
        LegacyTaskMutationStatus expected)
    {
        Assert.Equal(
            expected,
            LegacyTaskElevationEntryPoint.FromExitCode(exitCode, operation));
    }

    [Fact]
    public void MalformedHelperArguments_ReturnInvalidWithoutRunningTaskMutation()
    {
        Assert.True(LegacyTaskElevationEntryPoint.TryRun(
            [LegacyTaskElevationEntryPoint.CommandFlag],
            out int exitCode));
        Assert.Equal(
            LegacyTaskMutationStatus.Invalid,
            LegacyTaskElevationEntryPoint.FromExitCode(exitCode, "disable"));
    }

    [Fact]
    public void OrdinaryStartupArguments_AreNotClaimedByHelper()
    {
        Assert.False(LegacyTaskElevationEntryPoint.TryRun([], out _));
        Assert.False(LegacyTaskElevationEntryPoint.TryRun(["--open-settings"], out _));
        Assert.False(LegacyTaskElevationEntryPoint.TryRun(["--background"], out _));
    }
}
