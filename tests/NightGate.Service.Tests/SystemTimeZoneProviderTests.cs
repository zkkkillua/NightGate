using NightGate.Service;

namespace NightGate.Service.Tests;

public sealed class SystemTimeZoneProviderTests
{
    [Fact]
    public void Local_SameProviderInstanceReadsTheCurrentWindowsSelectionEveryTime()
    {
        TimeZoneInfo utcPlusEight = TimeZoneInfo.CreateCustomTimeZone(
            "NightGate-Provider-UTC+8",
            TimeSpan.FromHours(8),
            "NightGate Provider UTC+8",
            "NightGate Provider UTC+8");
        TimeZoneInfo selected = TimeZoneInfo.Utc;
        TimeZoneInfo cached = selected;
        int cacheRefreshes = 0;
        SystemTimeZoneProvider provider = new(
            () =>
            {
                cacheRefreshes++;
                cached = selected;
            },
            () => cached);

        TimeZoneInfo first = provider.Local;
        selected = utcPlusEight;
        TimeZoneInfo second = provider.Local;

        Assert.Equal(TimeZoneInfo.Utc.Id, first.Id);
        Assert.Equal(utcPlusEight.Id, second.Id);
        Assert.Equal(2, cacheRefreshes);
        Assert.NotSame(cached, second);
    }

    [Fact]
    public async Task Local_ConcurrentReadsSerializeTheGlobalCacheRefreshAndSnapshot()
    {
        int refreshInProgress = 0;
        int overlapObserved = 0;
        SystemTimeZoneProvider provider = new(
            () =>
            {
                if (Interlocked.Exchange(ref refreshInProgress, 1) != 0)
                {
                    Interlocked.Exchange(ref overlapObserved, 1);
                }
            },
            () =>
            {
                Thread.Sleep(2);
                Interlocked.Exchange(ref refreshInProgress, 0);
                return TimeZoneInfo.Utc;
            });

        await Task.WhenAll(Enumerable.Range(0, 16).Select(_ => Task.Run(() => provider.Local)));

        Assert.Equal(0, overlapObserved);
    }
}
