using AmneziaGeo.Decl;
using AmneziaGeo.Ipc.Fleet;
using AmneziaGeo.Windows.App.Fleet;
using Xunit;

namespace AmneziaGeo.Tests.Fleet;

/// <summary>
/// A rule addressed to a server asked for after the others: it falls to the other end while nobody holds it,
/// and takes its own tunnel back the moment the set does.
/// </summary>
public sealed class FleetLateJoinTests
{
    private const long List = 1;
    private const string Home = "cidr:10.9.9.0/24";

    private static FleetControl Set()
    {
        var fleet = new FleetControl(new FleetLive());
        fleet.SetOrder(["main", "spare", "home"]);
        fleet.SetRole("main", TunnelRoles.Primary);
        fleet.SetRole("spare", TunnelRoles.Reserve);
        fleet.SetRole("home", TunnelRoles.Neutral);
        fleet.SetTarget(FleetTargets.Key(List, Home),
            new RuleRoute(new RuleTarget(RuleTarget.Server, "home"), new RuleTarget(RuleTarget.Block)));
        return fleet;
    }

    private static IReadOnlyList<GeoRule> Rules()
    {
        return [new GeoRule(GeoRuleKind.Cidr, "10.9.9.0/24"), new GeoRule(GeoRuleKind.GeoSite, "github")];
    }

    [Fact]
    public void WhileTheHomeServerIsNotAskedFor_TheRuleFallsToBlock()
    {
        var fleet = Set();
        fleet.Add("main");

        Assert.Equal(RuleTarget.Block, fleet.Rides(new RuleRoute(new RuleTarget(RuleTarget.Server, "home"), new RuleTarget(RuleTarget.Block))));
        Assert.Equal(
            [new GeoRule(GeoRuleKind.Cidr, "10.9.9.0/24", RouteRole.Block), new GeoRule(GeoRuleKind.GeoSite, "github")],
            fleet.Share("main", List, Rules()));
    }

    [Fact]
    public void TheHomeServerAskedForLast_TakesTheRuleOffBlock()
    {
        var fleet = Set();
        fleet.Add("main");
        var before = fleet.StampOf("main");

        fleet.Add("home");

        Assert.Equal([new GeoRule(GeoRuleKind.GeoSite, "github")], fleet.Share("main", List, Rules()));
        Assert.NotEqual(before, fleet.StampOf("main"));
    }

    [Fact]
    public void TheHomeServerAskedForLast_CarriesTheRuleItself()
    {
        var fleet = Set();
        fleet.Add("main");
        fleet.Add("home");

        Assert.Equal([new GeoRule(GeoRuleKind.Cidr, "10.9.9.0/24")], fleet.Share("home", List, Rules()));
    }
}
