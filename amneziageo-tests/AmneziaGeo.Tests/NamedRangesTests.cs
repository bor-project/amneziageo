using AmneziaGeo.Decl;
using AmneziaGeo.Geo;
using Xunit;

namespace AmneziaGeo.Tests;

/// <summary>
/// Диапазон, названный правилом, объявляется при подъёме туннеля, а не зарабатывает маршрут контактом.
/// </summary>
public sealed class NamedRangesTests
{
    [Fact]
    public void NamedRanges_TakesOnlyWhatTheRulesName()
    {
        List<GeoRule> rules =
        [
            new(GeoRuleKind.Cidr, "192.168.1.0/24"),
            new(GeoRuleKind.GeoIp, "geoip:ru"),
            new(GeoRuleKind.Domain, "example.com"),
            new(GeoRuleKind.App, "pkg=org.telegram.messenger"),
        ];

        Assert.Equal(new[] { "192.168.1.0/24" }, GeoMaterializer.NamedRanges(rules, RouteRole.Proxy));
    }

    [Fact]
    public void NamedRanges_KeepsEachBucketToItself()
    {
        List<GeoRule> rules =
        [
            new(GeoRuleKind.Cidr, "10.9.9.0/24"),
            new(GeoRuleKind.Cidr, "10.0.110.0/24", RouteRole.Direct),
            new(GeoRuleKind.Cidr, "10.10.10.0/24", RouteRole.Block),
        ];

        Assert.Equal(new[] { "10.9.9.0/24" }, GeoMaterializer.NamedRanges(rules, RouteRole.Proxy));
        Assert.Equal(new[] { "10.0.110.0/24" }, GeoMaterializer.NamedRanges(rules, RouteRole.Direct));
    }

    [Fact]
    public void NamedRanges_KeepsTheOrderAndDropsRepeats()
    {
        List<GeoRule> rules =
        [
            new(GeoRuleKind.Cidr, "192.168.5.0/24"),
            new(GeoRuleKind.Cidr, "192.168.1.0/24"),
            new(GeoRuleKind.Cidr, "192.168.5.0/24"),
        ];

        Assert.Equal(new[] { "192.168.5.0/24", "192.168.1.0/24" }, GeoMaterializer.NamedRanges(rules, RouteRole.Proxy));
    }

    [Fact]
    public void NamedRanges_TakesOnlyTheRangesTheShareCarries()
    {
        List<GeoRule> rules =
        [
            new(GeoRuleKind.Cidr, "192.168.1.0/24"),
            new(GeoRuleKind.Cidr, "10.9.9.0/24"),
        ];

        Assert.Equal(
            new[] { "10.9.9.0/24" },
            GeoMaterializer.NamedRanges(rules, RouteRole.Proxy, ["10.9.9.0/24", "8.8.8.8/32"]));
    }

    [Fact]
    public void NamedRanges_TakesNothingWhileTheShareCarriesNothing()
    {
        List<GeoRule> rules = [new(GeoRuleKind.Cidr, "192.168.1.0/24")];

        Assert.Empty(GeoMaterializer.NamedRanges(rules, RouteRole.Proxy, []));
    }
}
