using System.Collections.Immutable;

namespace NightGate.Core;

public static class SupportedEntertainmentSiteCatalog
{
    public static ImmutableArray<string> Domains { get; } =
    [
        "bilibili.com",
        "iqiyi.com",
        "netflix.com",
        "v.qq.com",
        "youtube.com",
    ];

    private static readonly ImmutableHashSet<string> DomainSet =
        Domains.ToImmutableHashSet(StringComparer.Ordinal);

    public static bool IsSupported(string? domain) =>
        domain is not null && DomainSet.Contains(domain);
}
