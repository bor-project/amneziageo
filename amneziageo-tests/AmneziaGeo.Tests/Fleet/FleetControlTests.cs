using AmneziaGeo.Ipc.Fleet;
using AmneziaGeo.Windows.App.Fleet;
using Xunit;

namespace AmneziaGeo.Tests.Fleet;

/// <summary>
/// One tunnel of a set carries what no rule sends elsewhere and holds the resolver; handing that to a second
/// one takes the machine off the first. So the arbiter has to name exactly one, and name none at all when the
/// set holds nothing that may take it.
/// </summary>
public sealed class FleetControlTests
{
    [Fact]
    public void FirstAskedForCarriesTheDefault()
    {
        var fleet = new FleetControl(new FleetLive());
        fleet.Add("alpha");
        fleet.Add("bravo");

        Assert.Equal("alpha", fleet.Carrier);
        Assert.Equal(TunnelDuties.Sole, fleet.For("alpha"));
        Assert.Equal(TunnelDuties.None, fleet.For("bravo"));
    }

    [Fact]
    public void PrimaryCarriesWhereverItStandsInTheSet()
    {
        var fleet = new FleetControl(new FleetLive());
        fleet.Add("alpha");
        fleet.Add("bravo");
        fleet.SetRole("bravo", TunnelRoles.Primary);

        Assert.Equal("bravo", fleet.Carrier);
        Assert.Equal(TunnelDuties.Sole, fleet.For("bravo"));
        Assert.Equal(TunnelDuties.None, fleet.For("alpha"));
    }

    [Fact]
    public void SecondPrimaryDemotesTheFirst()
    {
        var fleet = new FleetControl(new FleetLive());
        fleet.Add("alpha");
        fleet.Add("bravo");
        fleet.SetRole("alpha", TunnelRoles.Primary);
        fleet.SetRole("bravo", TunnelRoles.Primary);

        Assert.Equal("bravo", fleet.Carrier);
        Assert.Equal(TunnelRoles.Reserve, fleet.RoleOf("alpha"));
    }

    [Fact]
    public void NeutralNeverCarries()
    {
        var fleet = new FleetControl(new FleetLive());
        fleet.SetRole("alpha", TunnelRoles.Neutral);
        fleet.Add("alpha");

        Assert.Null(fleet.Carrier);
        Assert.Equal(TunnelDuties.None, fleet.For("alpha"));

        fleet.Add("bravo");

        Assert.Equal("bravo", fleet.Carrier);
    }

    [Fact]
    public void LeavingTheSetMovesTheDefaultOn()
    {
        var fleet = new FleetControl(new FleetLive());
        fleet.Add("alpha");
        fleet.Add("bravo");
        fleet.Remove("alpha");

        Assert.Equal("bravo", fleet.Carrier);
        Assert.Equal(TunnelDuties.Sole, fleet.For("bravo"));
    }

    [Fact]
    public void AServerTheLibraryDropsLeavesTheSetAndItsRules()
    {
        var fleet = new FleetControl(new FleetLive());
        fleet.SetOrder(["alpha", "bravo"]);
        fleet.Add("alpha");
        fleet.Add("bravo");
        fleet.SetRole("bravo", TunnelRoles.Primary);
        fleet.SetTarget(FleetTargets.Key(1, "geosite:github"),
            new RuleRoute(new RuleTarget(RuleTarget.Server, "bravo"), new RuleTarget(RuleTarget.Server, "alpha")));

        Assert.True(fleet.Forget("bravo"));

        Assert.Equal(["alpha"], fleet.Wanted);
        Assert.Equal(["alpha"], fleet.Order);
        Assert.Equal(string.Empty, fleet.Primary);
        Assert.True(fleet.Moved);

        // The end that named it is left to the machine again; the other end stands as it was addressed.
        Assert.Equal(RuleRoute.Parse("auto,alpha"), fleet.TargetOf(FleetTargets.Key(1, "geosite:github")));
        Assert.False(fleet.Forget("bravo"));
    }

    [Fact]
    public void ARuleAddressedOnlyToAServerThatIsGoneIsLeftToTheMachine()
    {
        var fleet = new FleetControl(new FleetLive());
        fleet.Add("alpha");
        fleet.SetTarget(FleetTargets.Key(1, "geosite:github"), new RuleRoute(new RuleTarget(RuleTarget.Server, "alpha"), RuleTarget.Default));

        fleet.Forget("alpha");

        Assert.Equal(RuleRoute.Default, fleet.TargetOf(FleetTargets.Key(1, "geosite:github")));
    }

    [Fact]
    public void AServerTheLibraryRenamesKeepsItsPlaceAndItsRules()
    {
        var fleet = new FleetControl(new FleetLive());
        fleet.SetOrder(["alpha", "bravo"]);
        fleet.Add("alpha");
        fleet.Add("bravo");
        fleet.SetRole("bravo", TunnelRoles.Primary);
        fleet.SetTarget(FleetTargets.Key(1, "geosite:github"),
            new RuleRoute(new RuleTarget(RuleTarget.Server, "bravo"), new RuleTarget(RuleTarget.Server, "alpha")));

        Assert.True(fleet.Rename("bravo", "delta"));

        Assert.Equal(["alpha", "delta"], fleet.Order);
        Assert.Equal(["alpha", "delta"], fleet.Wanted);
        Assert.Equal("delta", fleet.Primary);
        Assert.Equal("delta", fleet.Carrier);
        Assert.Equal(RuleRoute.Parse("delta,alpha"), fleet.TargetOf(FleetTargets.Key(1, "geosite:github")));
        Assert.False(fleet.Rename("bravo", "echo"));
    }

    [Fact]
    public void ARenamedServerIsCarriedOverRatherThanDialledAgain()
    {
        var fleet = new FleetControl(new FleetLive());
        fleet.Add("alpha");
        fleet.SetTarget(FleetTargets.Key(1, "geosite:github"), new RuleRoute(new RuleTarget(RuleTarget.Server, "alpha"), RuleTarget.Default));
        var stamp = fleet.Stamp;

        Assert.True(fleet.Rename("alpha", "delta"));

        Assert.Equal(stamp, fleet.Stamp);
        Assert.True(fleet.Moved);

        var carried = Assert.Single(fleet.DrainRenames());
        Assert.Equal("alpha", carried.From);
        Assert.Equal("delta", carried.To);
        Assert.Empty(fleet.DrainRenames());
    }

    [Fact]
    public void AskingTwiceLeavesTheSetAlone()
    {
        var fleet = new FleetControl(new FleetLive());

        Assert.True(fleet.Add("alpha"));
        Assert.False(fleet.Add("alpha"));
        Assert.Equal(["alpha"], fleet.Wanted);
        Assert.True(fleet.Remove("alpha"));
        Assert.False(fleet.Remove("alpha"));
        Assert.Empty(fleet.Wanted);
        Assert.Null(fleet.Carrier);
    }

    [Fact]
    public void EveryTunnelOfTheSetStandsThroughANeighboursBringUp()
    {
        var fleet = new FleetControl(new FleetLive());
        fleet.Add("alpha");
        fleet.Add("bravo");

        Assert.Equal(["alpha", "bravo"], fleet.Standing("alpha"));
        Assert.Equal(["alpha", "bravo"], fleet.Standing("bravo"));
        Assert.Equal(["alpha", "bravo", "charlie"], fleet.Standing("charlie"));
    }

    [Fact]
    public void TheModeStandsBackUpOnWhatItStored()
    {
        var fleet = new FleetControl(new FleetLive());
        var stored = new FleetState(
            ["alpha", "bravo", "charlie"],
            new Dictionary<string, string> { ["charlie"] = TunnelRoles.Neutral },
            "bravo",
            ["bravo", "charlie"],
            FleetTargets.Empty);

        fleet.Restore(stored);

        Assert.Equal(["bravo", "charlie"], fleet.Wanted);
        Assert.Equal("bravo", fleet.Carrier);
        Assert.Equal("bravo", fleet.Primary);
        Assert.Equal(TunnelRoles.Neutral, fleet.RoleOf("charlie"));
        Assert.Equal(["alpha", "bravo", "charlie"], fleet.Order);
    }

    [Fact]
    public void TheOrderTheModeListsDecidesWhoCarries()
    {
        var fleet = new FleetControl(new FleetLive());
        fleet.SetOrder(["bravo", "alpha"]);
        fleet.Add("alpha");
        fleet.Add("bravo");

        Assert.Equal("bravo", fleet.Carrier);

        fleet.Remove("bravo");

        Assert.Equal("alpha", fleet.Carrier);
    }

    [Fact]
    public void WhatIsStoredIsWhatTheSetStandsOn()
    {
        var fleet = new FleetControl(new FleetLive());
        fleet.SetOrder(["alpha", "bravo"]);
        fleet.Add("bravo");
        fleet.SetRole("bravo", TunnelRoles.Primary);

        var stood = new FleetControl(new FleetLive());
        stood.Restore(fleet.Snapshot());

        Assert.Equal(fleet.Order, stood.Order);
        Assert.Equal(fleet.Wanted, stood.Wanted);
        Assert.Equal(fleet.Primary, stood.Primary);
        Assert.Equal(fleet.Carrier, stood.Carrier);
    }

    [Fact]
    public void TheWindowIsToldTheWholeLibraryInTheModesOrder()
    {
        var fleet = new FleetControl(new FleetLive());
        fleet.SetOrder(["bravo", "alpha"]);
        fleet.Add("bravo");
        fleet.SetRole("charlie", TunnelRoles.Neutral);

        var described = fleet.Describe(["alpha", "bravo", "charlie"]);

        Assert.Equal(["bravo", "alpha", "charlie"], described.Servers.Select(server => server.Name));
        Assert.Equal("bravo", described.Carrier);
        Assert.Equal(string.Empty, described.Primary);

        var carrier = described.Servers[0];
        Assert.True(carrier.Wanted);
        Assert.True(carrier.CarriesDefault);
        Assert.True(carrier.HoldsResolver);
        Assert.Equal(TunnelRoles.Reserve, carrier.Role);

        var idle = described.Servers[1];
        Assert.False(idle.Wanted);
        Assert.False(idle.CarriesDefault);
        Assert.Equal(TunnelRoles.Neutral, described.Servers[2].Role);
    }

    [Fact]
    public void AServerTheLibraryNoLongerHoldsIsNotDescribed()
    {
        var fleet = new FleetControl(new FleetLive());
        fleet.SetOrder(["alpha", "bravo"]);

        var described = fleet.Describe(["alpha"]);

        Assert.Equal(["alpha"], described.Servers.Select(server => server.Name));
    }

    [Fact]
    public void OnlyARequestMakesTheSetWorthWritingDown()
    {
        var fleet = new FleetControl(new FleetLive());
        Assert.False(fleet.Moved);

        fleet.Restore(new FleetState(["alpha"], new Dictionary<string, string>(StringComparer.Ordinal), string.Empty, ["alpha"], FleetTargets.Empty));
        Assert.False(fleet.Moved);

        fleet.Add("bravo");
        Assert.True(fleet.Moved);
    }

    [Fact]
    public void TheSetMovingWakesTheSupervisor()
    {
        var fleet = new FleetControl(new FleetLive());
        var token = fleet.ChangeToken;
        fleet.Add("alpha");

        Assert.True(token.IsCancellationRequested);
        Assert.False(fleet.ChangeToken.IsCancellationRequested);
    }
}
