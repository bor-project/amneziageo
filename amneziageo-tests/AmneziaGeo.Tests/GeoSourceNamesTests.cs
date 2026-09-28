using AmneziaGeo.Decl;
using AmneziaGeo.Geo;
using Xunit;

namespace AmneziaGeo.Tests;

/// <summary>
/// A new geo source is named after its kind and place, stepping past the names the held sources already carry.
/// </summary>
public sealed class GeoSourceNamesTests
{
    [Fact]
    public void AFreeNameIsTakenAsItIs()
    {
        Assert.Equal("geosite-1", GeoSourceNames.Free([], "geosite", 1));
    }

    [Fact]
    public void ATakenNameIsSteppedPast()
    {
        GeoSource[] held =
        [
            new("geoip-2", "geoip", "https://example.org/two.dat", 3),
            new("geosite-3", "geosite", "https://example.org/three.dat", 4),
            new("geoip-4", "geoip", "https://example.org/four.dat", 4),
            new("geoip-5", "geoip", "https://example.org/five.dat", 5),
        ];

        Assert.Equal("geoip-6", GeoSourceNames.Free(held, "geoip", 4));
        Assert.Equal("geosite-4", GeoSourceNames.Free(held, "geosite", 4));
    }
}
