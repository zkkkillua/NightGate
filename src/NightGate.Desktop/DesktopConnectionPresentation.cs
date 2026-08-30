namespace NightGate.Desktop;

public enum DesktopConnectionState
{
    Loading,
    Available,
    Unavailable,
}

public sealed record DesktopConnectionPresentation(
    DesktopConnectionState State,
    string Title,
    string Body);
