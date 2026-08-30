using System.Diagnostics.Eventing.Reader;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace NightGate.Service;

public sealed record WindowsBootEvent(
    long RecordId,
    DateTimeOffset TimeCreatedUtc);

public interface IWindowsBootEventSource
{
    WindowsBootEvent? ReadLatestBootEvent();
}

public sealed class WindowsEventLogBootEventSource : IWindowsBootEventSource
{
    private const string Query =
        "*[System[Provider[@Name='Microsoft-Windows-Kernel-General'] and EventID=12]]";

    public WindowsBootEvent? ReadLatestBootEvent()
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        EventLogQuery query = new("System", PathType.LogName, Query)
        {
            ReverseDirection = true,
            TolerateQueryErrors = true,
        };
        using EventLogReader reader = new(query);
        using EventRecord? record = reader.ReadEvent();
        if (record?.RecordId is not { } recordId || record.TimeCreated is not { } timeCreated)
        {
            return null;
        }

        return new(recordId, new DateTimeOffset(timeCreated).ToUniversalTime());
    }
}

public interface IBootSessionIdProvider
{
    Guid? Current { get; }
}

public sealed class WindowsBootSessionIdProvider : IBootSessionIdProvider
{
    private readonly Guid? _current;

    public WindowsBootSessionIdProvider(IWindowsBootEventSource eventSource)
    {
        ArgumentNullException.ThrowIfNull(eventSource);
        try
        {
            _current = Derive(eventSource.ReadLatestBootEvent());
        }
        catch (Exception)
        {
            _current = null;
        }
    }

    public Guid? Current => _current;

    private static Guid? Derive(WindowsBootEvent? bootEvent)
    {
        if (bootEvent is null
            || bootEvent.RecordId <= 0
            || bootEvent.TimeCreatedUtc == default)
        {
            return null;
        }

        string stableMarker = string.Create(
            CultureInfo.InvariantCulture,
            $"Microsoft-Windows-Kernel-General|12|{bootEvent.RecordId}|{bootEvent.TimeCreatedUtc.UtcDateTime.Ticks}");
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(stableMarker));
        Guid identifier = new(hash.AsSpan(0, 16));
        return identifier == Guid.Empty ? null : identifier;
    }
}
