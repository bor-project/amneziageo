using AmneziaGeo.Ipc.Fleet;
using AmneziaGeo.Windows.App.Fleet;
using Xunit;

namespace AmneziaGeo.Tests.Fleet;

/// <summary>
/// The place a server holds in the set is its priority: the first carries the machine, the rest are the reserve
/// in order, and one out of the chain is only ever named. Whatever moves the set closes the gap behind it, so
/// the places stay a run of numbers with nothing missing from it.
/// </summary>
public sealed class FleetSlotTests
{
    [Fact]
    public void TheFirstServerToJoinCarriesTheMachine()
    {
        var fleet = new FleetControl(new FleetLive());

        var described = fleet.Describe(["alpha"]);

        Assert.Equal(TunnelRoles.Lead, described.Servers[0].Slot);
        Assert.Equal(TunnelRoles.Primary, described.Servers[0].Role);
        Assert.Equal("alpha", fleet.Primary);
    }

    [Fact]
    public void AServerTheChainHasNotMetJoinsTheEndOfTheReserve()
    {
        var fleet = new FleetControl(new FleetLive());
        fleet.SetOrder(["alpha", "bravo"]);

        var described = fleet.Describe(["alpha", "bravo", "charlie"]);

        Assert.Equal([1, 2, 3], described.Servers.Select(server => server.Slot));
        Assert.Equal(["alpha", "bravo", "charlie"], fleet.Order);
    }

    [Fact]
    public void TakingTheHeadShiftsTheRestDownWithoutAGap()
    {
        var fleet = new FleetControl(new FleetLive());
        fleet.SetOrder(["alpha", "bravo", "charlie"]);

        Assert.True(fleet.Place("charlie", TunnelRoles.Lead));

        Assert.Equal(["charlie", "alpha", "bravo"], fleet.Order);
        Assert.Equal("charlie", fleet.Primary);
        Assert.Equal(TunnelRoles.Reserve, fleet.RoleOf("alpha"));
    }

    [Fact]
    public void APlaceInTheReserveIsTakenAndTheRestCloseTheGap()
    {
        var fleet = new FleetControl(new FleetLive());
        fleet.SetOrder(["alpha", "bravo", "charlie"]);

        Assert.True(fleet.Place("charlie", 2));

        Assert.Equal(["alpha", "charlie", "bravo"], fleet.Order);
        Assert.Equal("alpha", fleet.Primary);
    }

    [Fact]
    public void AServerOutOfTheChainStandsLastAndHoldsNoPlace()
    {
        var fleet = new FleetControl(new FleetLive());
        fleet.SetOrder(["alpha", "bravo", "charlie"]);

        Assert.True(fleet.Place("alpha", TunnelRoles.Aside));

        Assert.Equal(["bravo", "charlie", "alpha"], fleet.Order);
        Assert.Equal("bravo", fleet.Primary);
        Assert.Equal(TunnelRoles.Neutral, fleet.RoleOf("alpha"));
        Assert.Equal(TunnelRoles.Aside, Slot(fleet, "alpha"));
    }

    [Fact]
    public void AServerBackInTheChainJoinsTheEndOfTheReserve()
    {
        var fleet = new FleetControl(new FleetLive());
        fleet.SetOrder(["alpha", "bravo", "charlie"]);
        fleet.Place("alpha", TunnelRoles.Aside);

        Assert.True(fleet.SetRole("alpha", TunnelRoles.Reserve));

        Assert.Equal(["bravo", "charlie", "alpha"], fleet.Order);
        Assert.Equal(3, Slot(fleet, "alpha"));
    }

    [Fact]
    public void AStruckServerLeavesTheMachineToTheOneBehindIt()
    {
        var fleet = new FleetControl(new FleetLive());
        fleet.SetOrder(["alpha", "bravo", "charlie"]);
        fleet.Add("alpha");
        fleet.Add("bravo");

        Assert.True(fleet.Forget("alpha"));

        Assert.Equal(["bravo", "charlie"], fleet.Order);
        Assert.Equal("bravo", fleet.Primary);
        Assert.Equal("bravo", fleet.Carrier);
    }

    [Fact]
    public void APlacePastTheChainStandsAtItsEnd()
    {
        var fleet = new FleetControl(new FleetLive());
        fleet.SetOrder(["alpha", "bravo", "charlie"]);

        Assert.True(fleet.Place("alpha", 9));

        Assert.Equal(["bravo", "charlie", "alpha"], fleet.Order);
        Assert.Equal(3, Slot(fleet, "alpha"));
    }

    [Fact]
    public void TheSamePlaceTwiceMovesNothing()
    {
        var fleet = new FleetControl(new FleetLive());
        fleet.SetOrder(["alpha", "bravo"]);

        Assert.False(fleet.Place("alpha", TunnelRoles.Lead));
        Assert.True(fleet.Place("bravo", TunnelRoles.Aside));
        Assert.False(fleet.Place("bravo", TunnelRoles.Aside));
    }

    [Fact]
    public void ARenamedServerKeepsThePlaceItHeld()
    {
        var fleet = new FleetControl(new FleetLive());
        fleet.SetOrder(["alpha", "bravo", "charlie"]);

        Assert.True(fleet.Rename("bravo", "delta"));

        Assert.Equal(["alpha", "delta", "charlie"], fleet.Order);
        Assert.Equal(2, Slot(fleet, "delta"));
    }

    // The place a server holds, as the window is told it.
    private static int Slot(FleetControl fleet, string name)
    {
        return fleet.Describe([.. fleet.Order]).Servers.First(server => server.Name == name).Slot;
    }
}
