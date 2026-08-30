namespace NightGate.Core;

public enum BrowserEventType
{
    MediaPlaying,
    MediaPaused,
    MediaEnded,
    NavigationBlocked,
}

public enum BrowserSiteCategory
{
    Gaming,
    Video,
    Social,
    Other,
}

public sealed record BrowserPrivacyEvent(
    DateTimeOffset TimestampUtc,
    BrowserEventType EventType,
    BrowserSiteCategory Category);

public static class BrowserEventLimits
{
    public static TimeSpan MaximumAge { get; } = TimeSpan.FromMinutes(5);

    public static TimeSpan MaximumFutureSkew { get; } = TimeSpan.FromMinutes(5);
}
