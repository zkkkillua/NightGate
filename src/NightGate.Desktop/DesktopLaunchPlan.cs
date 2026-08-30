namespace NightGate.Desktop;

internal sealed record DesktopLaunchPlan(
    bool IsBackground,
    bool ShouldSignalExistingInstance,
    bool ShouldOpenDashboard)
{
    internal const string BackgroundFlag = "--background";

    internal static DesktopLaunchPlan Create(
        IReadOnlyList<string> arguments,
        bool isPrimary)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        bool isBackground = arguments.Any(argument => string.Equals(
            argument,
            BackgroundFlag,
            StringComparison.OrdinalIgnoreCase));
        return new(
            isBackground,
            ShouldSignalExistingInstance: !isBackground && !isPrimary,
            ShouldOpenDashboard: !isBackground && isPrimary);
    }
}
