using AmneziaGeo.Decl;
using AmneziaGeo.Geo;
using AmneziaGeo.Windows.App.Fleet;
using Xunit;

namespace AmneziaGeo.Tests.Fleet;

/// <summary>
/// A machine looks addresses up through one tunnel, so a name matched by a rule riding another one arrives at
/// the holder. It has to tell whose name it is before it can hand it over.
/// </summary>
public sealed class FleetLentNamesTests
{
    [Fact]
    public void ANameARuleOfAnotherTunnelMatchesNamesThatTunnel()
    {
        var owners = new List<FleetLentOwner>
        {
            new("bravo", new DomainMatcher([new GeoDomain(GeoDomainKind.Domain, "youtube.com")])),
            new("charlie", new DomainMatcher([new GeoDomain(GeoDomainKind.Full, "rutracker.org")])),
        };

        Assert.Equal("bravo", FleetLentNames.OwnerOf(owners, "www.youtube.com"));
        Assert.Equal("charlie", FleetLentNames.OwnerOf(owners, "rutracker.org"));

        // A name none of them matches is answered here, as every other name is.
        Assert.Null(FleetLentNames.OwnerOf(owners, "example.com"));
        Assert.Null(FleetLentNames.OwnerOf(owners, "forum.rutracker.org"));
        Assert.Null(FleetLentNames.OwnerOf([], "www.youtube.com"));
    }
}
