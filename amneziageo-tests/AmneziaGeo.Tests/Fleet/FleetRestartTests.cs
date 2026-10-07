using AmneziaGeo.Windows.App;
using AmneziaGeo.Windows.App.Fleet;
using Xunit;

namespace AmneziaGeo.Tests.Fleet;

/// <summary>
/// Saved settings a tunnel of the set takes on a reconnect are told for the whole set, whichever server is selected.
/// </summary>
public sealed class FleetRestartTests
{
    [Fact]
    public void ASetThatStands_AsksForNoReconnect()
    {
        var live = new FleetLive();
        live.Publish("main", Raised());
        live.Publish("home", Raised());

        Assert.False(live.RestartRequired);
    }

    [Fact]
    public void AnEditedServerBesideTheSelectedOne_AsksForAReconnect()
    {
        var live = new FleetLive();
        live.Publish("main", Raised());
        var home = Raised();
        live.Publish("home", home);

        home.SetRestartRequired();

        Assert.True(live.RestartRequired);
    }

    [Fact]
    public void TheServerRaisedAnew_AsksNoMore()
    {
        var live = new FleetLive();
        live.Publish("main", Raised());
        var home = Raised();
        live.Publish("home", home);
        home.SetRestartRequired();

        live.Publish("home", Raised());

        Assert.False(live.RestartRequired);
    }

    [Fact]
    public void TheServerTakenDown_AsksNoMore()
    {
        var live = new FleetLive();
        live.Publish("main", Raised());
        var home = Raised();
        live.Publish("home", home);
        home.SetRestartRequired();

        live.Drop("home");

        Assert.False(live.RestartRequired);
    }

    private static AgentControl Raised()
    {
        var control = new AgentControl();
        control.SetRunning(true);
        return control;
    }
}
