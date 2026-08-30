namespace NightGate.Core;

public static class NightScheduleTimeZone
{
    public const int MaximumSerializedLength = 64 * 1024;

    public static string Capture(TimeZoneInfo timeZone)
    {
        ArgumentNullException.ThrowIfNull(timeZone);
        string serialized = timeZone.ToSerializedString();
        if (string.IsNullOrWhiteSpace(serialized)
            || serialized.Length > MaximumSerializedLength)
        {
            throw new ArgumentException(
                "The schedule time zone cannot be persisted safely.",
                nameof(timeZone));
        }

        return serialized;
    }

    public static TimeZoneInfo Restore(string serialized)
    {
        if (string.IsNullOrWhiteSpace(serialized)
            || serialized.Length > MaximumSerializedLength)
        {
            throw new ArgumentException(
                "The persisted schedule time zone is invalid.",
                nameof(serialized));
        }

        return TimeZoneInfo.FromSerializedString(serialized);
    }

    public static TimeZoneInfo ResolveForActiveNight(
        NightState? state,
        TimeZoneInfo currentTimeZone)
    {
        ArgumentNullException.ThrowIfNull(currentTimeZone);
        return state is { IsClosed: false, ScheduleTimeZoneSerialized: { } serialized }
            ? Restore(serialized)
            : currentTimeZone;
    }
}
