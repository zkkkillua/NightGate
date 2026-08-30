using NightGate.Core;

namespace NightGate.Core.Tests;

public sealed class SupportedEntertainmentSiteCatalogTests
{
    [Fact]
    public void Domains_AreTheExactChromeV1CatalogInCanonicalOrder()
    {
        Assert.Equal(
            new[]
            {
                "bilibili.com",
                "iqiyi.com",
                "netflix.com",
                "v.qq.com",
                "youtube.com",
            },
            SupportedEntertainmentSiteCatalog.Domains.ToArray());
    }

    [Theory]
    [InlineData("bilibili.com", true)]
    [InlineData("youtube.com", true)]
    [InlineData("www.youtube.com", false)]
    [InlineData("example.com", false)]
    [InlineData("YouTube.com", false)]
    [InlineData(" youtube.com", false)]
    public void IsSupported_AcceptsOnlyCanonicalCatalogRoots(
        string domain,
        bool expected)
    {
        Assert.Equal(
            expected,
            SupportedEntertainmentSiteCatalog.IsSupported(domain));
    }
}
