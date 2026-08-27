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
        var fleet = new FleetControl();
        fleet.Add("alpha");
        fleet.Add("bravo");

        Assert.Equal("alpha", fleet.Carrier);
        Assert.Equal(TunnelDuties.Sole, fleet.For("alpha"));
        Assert.Equal(TunnelDuties.None, fleet.For("bravo"));
    }

    [Fact]
    public void PrimaryCarriesWhereverItStandsInTheSet()
    {
        var fleet = new FleetControl();
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
        var fleet = new FleetControl();
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
        var fleet = new FleetControl();
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
        var fleet = new FleetControl();
        fleet.Add("alpha");
        fleet.Add("bravo");
        fleet.Remove("alpha");

        Assert.Equal("bravo", fleet.Carrier);
        Assert.Equal(TunnelDuties.Sole, fleet.For("bravo"));
    }

    [Fact]
    public void AskingTwiceLeavesTheSetAlone()
    {
        var fleet = new FleetControl();

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
        var fleet = new FleetControl();
        fleet.Add("alpha");
        fleet.Add("bravo");

        Assert.Equal(["alpha", "bravo"], fleet.Standing("alpha"));
        Assert.Equal(["alpha", "bravo"], fleet.Standing("bravo"));
        Assert.Equal(["alpha", "bravo", "charlie"], fleet.Standing("charlie"));
    }

    [Fact]
    public void TheModeStandsBackUpOnWhatItStored()
    {
        var fleet = new FleetControl();
        var stored = new FleetState(
            ["alpha", "bravo", "charlie"],
            new Dictionary<string, string> { ["charlie"] = TunnelRoles.Neutral },
            "bravo",
            ["bravo", "charlie"]);

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
        var fleet = new FleetControl();
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
        var fleet = new FleetControl();
        fleet.SetOrder(["alpha", "bravo"]);
        fleet.Add("bravo");
        fleet.SetRole("bravo", TunnelRoles.Primary);

        var stood = new FleetControl();
        stood.Restore(fleet.Snapshot());

        Assert.Equal(fleet.Order, stood.Order);
        Assert.Equal(fleet.Wanted, stood.Wanted);
        Assert.Equal(fleet.Primary, stood.Primary);
        Assert.Equal(fleet.Carrier, stood.Carrier);
    }

    [Fact]
    public void TheSetMovingWakesTheSupervisor()
    {
        var fleet = new FleetControl();
        var token = fleet.ChangeToken;
        fleet.Add("alpha");

        Assert.True(token.IsCancellationRequested);
        Assert.False(fleet.ChangeToken.IsCancellationRequested);
    }
}
