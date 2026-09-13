using AmneziaGeo.Decl;
using AmneziaGeo.Geo;
using Xunit;

namespace AmneziaGeo.Tests;

/// <summary>
/// The ranges a tunnel keeps on its adapter are worked out again on every list edit, so the same inputs have to
/// give the same set at bring-up and live.
/// </summary>
public sealed class StandingRangesTests
{
    private static GeoRule Cidr(string value, RouteRole role = RouteRole.Proxy) => new(GeoRuleKind.Cidr, value, role);

    [Fact]
    public void ARangeARuleNames_StandsInSplit()
    {
        var standing = StandingRanges.Of(["10.80.0.0/16", "1.2.3.0/24"], [Cidr("10.80.0.0/16")], split: true, inbound: false, [], new HashSet<string>());

        Assert.Equal(["10.80.0.0/16"], standing.Named);
        Assert.Empty(standing.Return);
        Assert.Equal(["10.80.0.0/16"], standing.All);
    }

    [Fact]
    public void ARangeAnotherTunnelCarries_DoesNotStand()
    {
        var standing = StandingRanges.Of(["1.2.3.0/24"], [Cidr("10.80.0.0/16")], split: true, inbound: false, [], new HashSet<string>());

        Assert.Empty(standing.All);
    }

    [Fact]
    public void WithInboundAccess_ThePrivateNetworksCarriedStandOnceAsTheWayBack()
    {
        var standing = StandingRanges.Of(["10.80.0.0/16", "1.2.3.0/24"], [Cidr("10.80.0.0/16")], split: true, inbound: true, [], new HashSet<string>());

        Assert.Equal(["10.80.0.0/16"], standing.Return);
        Assert.Empty(standing.Named);
        Assert.Equal(["10.80.0.0/16"], standing.All);
    }

    [Fact]
    public void ARangeOfTheInfrastructure_IsLeftOut()
    {
        var standing = StandingRanges.Of(["10.8.2.0/24"], [Cidr("10.8.2.0/24")], split: true, inbound: true, ["10.8.2.0/24"], new HashSet<string> { "10.8.2.0/24" });

        Assert.Empty(standing.All);
    }

    [Fact]
    public void ANetworkOfTheConfiguration_StandsUnlessARuleSpeaksAboutIt()
    {
        var free = StandingRanges.Of([], [], split: true, inbound: false, ["192.168.50.0/24"], new HashSet<string>());
        var spoken = StandingRanges.Of([], [Cidr("192.168.50.0/25", RouteRole.Direct)], split: true, inbound: false, ["192.168.50.0/24"], new HashSet<string>());

        Assert.Equal(["192.168.50.0/24"], free.Networks);
        Assert.Empty(spoken.Networks);
    }

    [Fact]
    public void AFullTunnel_KeepsOnlyTheWayBack()
    {
        var standing = StandingRanges.Of(["10.80.0.0/16"], [Cidr("10.80.0.0/16")], split: false, inbound: true, ["192.168.50.0/24"], new HashSet<string>());

        Assert.Equal(["10.80.0.0/16"], standing.Return);
        Assert.Empty(standing.Named);
        Assert.Empty(standing.Networks);
    }

    [Fact]
    public void Diff_TellsTheAddedFromTheRemoved()
    {
        var (added, removed) = StandingRanges.Diff(["10.80.0.0/16", "192.168.50.0/24"], ["192.168.50.0/24", "10.81.0.0/16"]);

        Assert.Equal(["10.81.0.0/16"], added);
        Assert.Equal(["10.80.0.0/16"], removed);
    }
}
