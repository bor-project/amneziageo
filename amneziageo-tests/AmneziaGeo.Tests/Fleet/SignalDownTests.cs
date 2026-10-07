using AmneziaGeo.Windows.App;
using AmneziaGeo.Windows.App.Fleet;
using Xunit;

namespace AmneziaGeo.Tests.Fleet;

/// <summary>
/// The server of a tunnel asks the machine to disconnect: a machine on one tunnel leaves it to the supervisor of
/// that tunnel, a set of tunnels goes down as a whole and remembers what stood.
/// </summary>
public sealed class SignalDownTests
{
    [Fact]
    public void TheOnlyTunnel_IsLeftToItsOwnSupervisor()
    {
        Assert.False(new TunnelDutyRoster().ServerAskedDown("main"));
    }

    [Fact]
    public void ASetOfTunnels_GoesDownAsAWhole()
    {
        var fleet = new FleetControl(new FleetLive());
        fleet.Add("main");
        fleet.Add("home");

        var taken = fleet.ServerAskedDown("home");

        Assert.True(taken);
        Assert.Empty(fleet.Wanted);
        Assert.Equal(["main", "home"], fleet.Resume);
    }

    [Fact]
    public void ASetThatDoesNotStand_StaysAsItIs()
    {
        var fleet = new FleetControl(new FleetLive());

        Assert.True(fleet.ServerAskedDown("home"));
        Assert.Empty(fleet.Wanted);
        Assert.Empty(fleet.Resume);
    }
}
