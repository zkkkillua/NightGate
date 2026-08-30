using NightGate.Desktop;

namespace NightGate.Desktop.Tests;

public sealed class DesktopSingleInstanceCoordinatorTests
{
    [Fact]
    public async Task SecondInstance_SignalsTheExistingInstanceAndDoesNotBecomePrimary()
    {
        string identity = $"test-{Guid.NewGuid():N}";
        using DesktopSingleInstanceCoordinator primary =
            DesktopSingleInstanceCoordinator.Create(identity);
        using DesktopSingleInstanceCoordinator secondary =
            DesktopSingleInstanceCoordinator.Create(identity);
        TaskCompletionSource activated = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        primary.StartListening(() => activated.TrySetResult());
        bool signaled = secondary.SignalExistingInstance();

        await activated.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(primary.IsPrimary);
        Assert.False(secondary.IsPrimary);
        Assert.True(signaled);
    }

    [Fact]
    public void DisposedPrimary_AllowsTheNextLaunchToBecomePrimary()
    {
        string identity = $"test-{Guid.NewGuid():N}";
        DesktopSingleInstanceCoordinator first =
            DesktopSingleInstanceCoordinator.Create(identity);
        DesktopSingleInstanceCoordinator second =
            DesktopSingleInstanceCoordinator.Create(identity);
        Assert.True(first.IsPrimary);
        Assert.False(second.IsPrimary);

        second.Dispose();
        first.Dispose();

        using DesktopSingleInstanceCoordinator replacement =
            DesktopSingleInstanceCoordinator.Create(identity);
        Assert.True(replacement.IsPrimary);
    }

    [Fact]
    public void DifferentUsersOrTestIdentities_DoNotBlockEachOther()
    {
        using DesktopSingleInstanceCoordinator first =
            DesktopSingleInstanceCoordinator.Create($"test-{Guid.NewGuid():N}");
        using DesktopSingleInstanceCoordinator second =
            DesktopSingleInstanceCoordinator.Create($"test-{Guid.NewGuid():N}");

        Assert.True(first.IsPrimary);
        Assert.True(second.IsPrimary);
    }
}
