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
    public void TheSetMovingWakesTheSupervisor()
    {
        var fleet = new FleetControl();
        var token = fleet.ChangeToken;
        fleet.Add("alpha");

        Assert.True(token.IsCancellationRequested);
        Assert.False(fleet.ChangeToken.IsCancellationRequested);
    }
}
