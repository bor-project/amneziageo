using AmneziaGeo.Decl;
using AmneziaGeo.Ipc;
using AmneziaGeo.Ipc.Fleet;
using AmneziaGeo.Windows.App;
using AmneziaGeo.Windows.App.Fleet;
using Xunit;

namespace AmneziaGeo.Tests.Fleet;

/// <summary>
/// A rule addressed to a server rides it while it stands: it falls to the other end while the server is being
/// raised or its dial has given up, and takes its own tunnel back the moment it answers.
/// </summary>
public sealed class FleetLiveRulesTests
{
    private const long List = 1;
    private const string Home = "cidr:10.9.9.0/24";

    private static (FleetControl Fleet, FleetLive Live) Set()
    {
        var live = new FleetLive();
        var fleet = new FleetControl(live);
        fleet.SetOrder(["main", "home"]);
        fleet.SetRole("main", TunnelRoles.Primary);
        fleet.SetRole("home", TunnelRoles.Neutral);
        fleet.SetTarget(FleetTargets.Key(List, Home),
            new RuleRoute(new RuleTarget(RuleTarget.Server, "home"), new RuleTarget(RuleTarget.Block)));
        fleet.Add("main");
        fleet.Add("home");
        return (fleet, live);
    }

    private static RuleRoute ToHome() =>
        new(new RuleTarget(RuleTarget.Server, "home"), new RuleTarget(RuleTarget.Block));

    private static AgentControl Raised()
    {
        var control = new AgentControl();
        control.SetRunning(true);
        return control;
    }

    [Fact]
    public void WhileTheServerIsBeingRaised_TheRuleFallsToBlock()
    {
        var (fleet, live) = Set();
        live.Publish("home", Raised());

        Assert.Equal(RuleTarget.Block, fleet.Rides(ToHome()));
    }

    [Fact]
    public void TheServerThatStood_CarriesTheRule()
    {
        var (fleet, live) = Set();
        var home = Raised();
        live.Publish("home", home);
        home.SetConnected(true);

        Assert.Equal("home", fleet.Rides(ToHome()));
    }

    [Fact]
    public void TheServerWhoseDialGaveUp_LeavesTheRuleToTheFallback()
    {
        var (fleet, live) = Set();
        var home = Raised();
        live.Publish("home", home);
        home.SetConnected(true);
        Assert.Equal("home", fleet.Rides(ToHome()));

        home.SetConnected(false);
        home.FailConnect(ConnectFailureReason.Unknown, null);

        Assert.Equal(RuleTarget.Block, fleet.Rides(ToHome()));
    }

    [Fact]
    public void TheServerBeingRaised_StillCarriesItsOwnShare()
    {
        var (fleet, live) = Set();
        live.Publish("home", Raised());

        Assert.Equal([new GeoRule(GeoRuleKind.Cidr, "10.9.9.0/24")],
            fleet.Share("home", List, [new GeoRule(GeoRuleKind.Cidr, "10.9.9.0/24")]));
    }

    [Fact]
    public void TheServerStandingUpLate_MovesTheShareOfTheOthers()
    {
        var (fleet, live) = Set();
        var home = Raised();
        live.Publish("home", home);
        var before = fleet.StampOf("main");

        home.SetConnected(true);

        Assert.NotEqual(before, fleet.StampOf("main"));
    }
}
