using AmneziaGeo.Decl;
using AmneziaGeo.Windows.App;
using Xunit;

namespace AmneziaGeo.Tests;

/// <summary>
/// The rule-entry preview shown under a geo rule in the editor renders geosite entries in v2ray notation, so a
/// suffix match reads as a bare domain and every other match type carries its prefix.
/// </summary>
public sealed class GeoConfiguratorTests
{
    [Fact]
    public void SuffixMatchIsRenderedBare()
    {
        Assert.Equal("example.com", GeoConfigurator.FormatDomain(new GeoDomain(GeoDomainKind.Domain, "example.com")));
    }

    [Fact]
    public void OtherMatchKindsCarryTheirPrefix()
    {
        Assert.Equal("full:www.example.com", GeoConfigurator.FormatDomain(new GeoDomain(GeoDomainKind.Full, "www.example.com")));
        Assert.Equal("regexp:.*\\.example\\.com$", GeoConfigurator.FormatDomain(new GeoDomain(GeoDomainKind.Regex, ".*\\.example\\.com$")));
        Assert.Equal("keyword:example", GeoConfigurator.FormatDomain(new GeoDomain(GeoDomainKind.Plain, "example")));
    }
}
