using AmneziaGeo.Ipc.Fleet;
using AmneziaGeo.Windows.App.Fleet;
using Xunit;

namespace AmneziaGeo.Tests.Fleet;

/// <summary>
/// A rule riding the quickest server must not follow every reading: every move of the pick dials the tunnels of
/// the set again. So the pick stands while the server it names answers, the primary takes it back the moment it
/// is up again, and any other server takes it only by answering in less than half the time.
/// </summary>
public sealed class FleetBalancerTests
{
    private const long List = 1;

    private static readonly RuleRoute Best = new(new RuleTarget(RuleTarget.Best), RuleTarget.Default);

    private static FleetControl Set()
    {
        var fleet = new FleetControl(new FleetLive());
        fleet.SetOrder(["alpha", "bravo", "charlie"]);
        fleet.SetRole("alpha", TunnelRoles.Primary);
        fleet.Add("alpha");
        fleet.Add("bravo");
        fleet.Add("charlie");
        fleet.SetTarget(FleetTargets.Key(List, "geosite:github"), Best);
        return fleet;
    }

    private static Dictionary<string, int> Trips(int alpha, int bravo, int charlie)
    {
        return new Dictionary<string, int> { ["alpha"] = alpha, ["bravo"] = bravo, ["charlie"] = charlie };
    }

    [Fact]
    public void ThePickStandsWhileTheServerItNamesAnswers()
    {
        var fleet = Set();
        fleet.Remove("alpha");

        Assert.Equal("bravo", fleet.Rides(Best, Trips(5, 30, 40)));

        // A reading moving under the pick is not a reason to hand the rule to another tunnel.
        Assert.Equal("bravo", fleet.Rides(Best, Trips(5, 35, 30)));
    }

    [Fact]
    public void APickThatLeavesTheSetIsReplacedAtOnce()
    {
        var fleet = Set();
        fleet.Remove("alpha");

        Assert.Equal("bravo", fleet.Rides(Best, Trips(5, 30, 40)));

        fleet.Remove("bravo");

        Assert.Equal("charlie", fleet.Rides(Best, Trips(5, 30, 40)));
    }

    [Fact]
    public void OneSilentLookIsATunnelBeingDialledAgain()
    {
        var fleet = Set();
        fleet.Remove("alpha");

        Assert.Equal("bravo", fleet.Rides(Best, Trips(5, 30, 40)));

        // A tunnel taking its share over again answers nothing for a moment, and the rule is not handed over
        // for that; a server that stays silent loses the pick on the look after it.
        var silent = new Dictionary<string, int> { ["charlie"] = 40 };
        Assert.False(fleet.Rebalance(silent));
        Assert.Equal("bravo", fleet.Best);
        Assert.Equal("bravo", fleet.Rides(Best, silent));

        Assert.True(fleet.Rebalance(silent));
        Assert.Equal("charlie", fleet.Best);
    }

    [Fact]
    public void TheTimedLookGoesBackToThePrimary()
    {
        var fleet = Set();
        fleet.Remove("alpha");

        Assert.Equal("bravo", fleet.Rides(Best, Trips(5, 30, 40)));

        fleet.Add("alpha");

        // The primary answers again, and it takes the pick back however the readings stand.
        Assert.True(fleet.Rebalance(Trips(90, 30, 40)));
        Assert.Equal("alpha", fleet.Best);
    }

    [Fact]
    public void TheTimedLookMovesOnlyForTwiceTheSpeed()
    {
        var fleet = Set();
        fleet.Remove("alpha");

        Assert.Equal("bravo", fleet.Rides(Best, Trips(5, 30, 40)));

        Assert.False(fleet.Rebalance(Trips(5, 30, 16)));
        Assert.Equal("bravo", fleet.Best);

        Assert.True(fleet.Rebalance(Trips(5, 30, 14)));
        Assert.Equal("charlie", fleet.Best);
    }

    [Fact]
    public void ASetWhereNobodyAnswersKeepsThePickItHas()
    {
        var fleet = Set();
        fleet.Remove("alpha");

        Assert.Equal("bravo", fleet.Rides(Best, Trips(5, 30, 40)));

        // Every tunnel of the set is being dialled again; there is nobody to hand the rules to.
        var silence = new Dictionary<string, int>();
        Assert.False(fleet.Rebalance(silence));
        Assert.False(fleet.Rebalance(silence));
        Assert.Equal("bravo", fleet.Best);
    }

    [Fact]
    public void ThePickFollowsTheServerThroughARename()
    {
        var fleet = Set();
        fleet.Remove("alpha");

        Assert.Equal("bravo", fleet.Rides(Best, Trips(5, 30, 40)));

        fleet.Rename("bravo", "delta");

        Assert.Equal("delta", fleet.Best);
        Assert.Equal("delta", fleet.Rides(Best, new Dictionary<string, int> { ["delta"] = 30, ["charlie"] = 40 }));
    }

    [Fact]
    public void TheNumbersTheSettingsHoldDecideTheHandover()
    {
        var fleet = Set();
        fleet.Remove("alpha");
        fleet.SetPolicy(new BalancePolicy(30, 1, 90));

        Assert.Equal("bravo", fleet.Rides(Best, Trips(5, 30, 40)));

        // A tenth quicker is enough at 90%, where the pick would stand at the default half.
        Assert.True(fleet.Rebalance(Trips(5, 30, 26)));
        Assert.Equal("charlie", fleet.Best);

        // One silent look is all a single strike gives.
        Assert.True(fleet.Rebalance(new Dictionary<string, int> { ["bravo"] = 30 }));
        Assert.Equal("bravo", fleet.Best);
    }

    [Fact]
    public void APickNoRuleFollowsLeavesTheTunnelsAlone()
    {
        var fleet = new FleetControl(new FleetLive());
        fleet.SetOrder(["alpha", "bravo"]);
        fleet.Add("alpha");
        fleet.Add("bravo");
        var stamp = fleet.Stamp;

        Assert.False(fleet.Rebalance(new Dictionary<string, int> { ["alpha"] = 90, ["bravo"] = 30 }));
        Assert.Equal("bravo", fleet.Best);
        Assert.Equal(stamp, fleet.Stamp);
    }

    [Fact]
    public void ARuleFollowingTheBalancerIsHandedOverOnTheMove()
    {
        var fleet = Set();
        fleet.Remove("alpha");
        fleet.Rides(Best, Trips(5, 30, 40));
        var stamp = fleet.Stamp;

        Assert.True(fleet.Rebalance(Trips(5, 30, 10)));
        Assert.NotEqual(stamp, fleet.Stamp);
    }
}
