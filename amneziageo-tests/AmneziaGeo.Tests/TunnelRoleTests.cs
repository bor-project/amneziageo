using AmneziaGeo.Decl;
using AmneziaGeo.Routing;
using AmneziaGeo.Windows.App;
using Xunit;

namespace AmneziaGeo.Tests;

/// <summary>
/// The place a tunnel takes on the machine: which one carries everything no rule sends elsewhere, and which one
/// takes the default route from whoever holds it.
/// </summary>
public sealed class TunnelRoleTests
{
    private static readonly ServerFleet Fleet = new(true, ["fi", "de", "nl"], ["fi", "de"]);

    [Fact]
    public void HeadOfTheFleet_CarriesEverythingAndTakesTheDefaultRoute()
    {
        var role = RoutingDistributor.Role("fi", 7, Fleet, null, null);

        Assert.False(role.Split);
        Assert.True(role.Preferred);
        Assert.Equal(7L, role.ListId);
    }

    [Fact]
    public void ServerBesideTheHead_CarriesOnlyItsShare()
    {
        var role = RoutingDistributor.Role("de", 7, Fleet, null, null);

        Assert.True(role.Split);
        Assert.False(role.Preferred);
    }

    [Fact]
    public void WithSeveralServers_ThePickedConfigurationIsNotRead()
    {
        var role = RoutingDistributor.Role("de", 7, Fleet, null, "de");

        Assert.False(role.Preferred);
        Assert.True(RoutingDistributor.Role("fi", 7, Fleet, null, "de").Preferred);
    }

    [Fact]
    public void WithSeveralServers_TheListsFullTunnelFlagIsNotRead()
    {
        Assert.True(RoutingDistributor.Role("de", 7, Fleet, Full(7), null).Split);
        Assert.False(RoutingDistributor.Role("fi", 7, Fleet, Split(7), null).Split);
    }

    [Fact]
    public void WithSeveralServersAndNoList_OnlyTheHeadCarriesAnything()
    {
        Assert.False(RoutingDistributor.Role("fi", null, Fleet, null, null).Split);
        Assert.True(RoutingDistributor.Role("de", null, Fleet, null, null).Split);
    }

    [Fact]
    public void WithTheModeOff_TheListSaysWhetherEverythingGoesThroughTheTunnel()
    {
        var single = ServerFleet.Single("fi");

        Assert.True(RoutingDistributor.Role("fi", 7, single, Split(7), null).Split);
        Assert.False(RoutingDistributor.Role("fi", 7, single, Full(7), null).Split);
    }

    [Fact]
    public void WithTheModeOffAndNoList_TheTunnelCarriesEverything()
    {
        Assert.False(RoutingDistributor.Role("fi", null, ServerFleet.Single("fi"), null, null).Split);
    }

    [Fact]
    public void WithTheModeOff_ThePickedConfigurationTakesTheDefaultRoute()
    {
        var single = ServerFleet.Single("fi");

        Assert.True(RoutingDistributor.Role("fi", 7, single, null, "fi").Preferred);
        Assert.False(RoutingDistributor.Role("fi", 7, single, null, "de").Preferred);
        Assert.False(RoutingDistributor.Role("fi", 7, single, null, null).Preferred);
    }

    private static RoutingSettings Full(long listId)
    {
        return new RoutingSettings(listId, string.Empty, false, "full", true);
    }

    private static RoutingSettings Split(long listId)
    {
        return new RoutingSettings(listId, string.Empty, false);
    }
}
