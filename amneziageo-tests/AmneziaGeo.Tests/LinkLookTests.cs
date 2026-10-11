using AmneziaGeo.Ipc;
using AmneziaGeo.Windows.App;
using AmneziaGeo.Windows.App.Fleet;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AmneziaGeo.Tests;

/// <summary>
/// The Windows agent answers the question about the link with a row for every tunnel that is measured, the one
/// it runs alone or each of the set, and the question itself keeps their echoes at the watched pace.
/// </summary>
public sealed class LinkLookTests
{
    private long _now;

    [Fact]
    public void WithNothingRunning_TheAnswerIsEmpty()
    {
        var ack = Checks(new AgentControl(), new FleetLive()).LookAtLink();

        Assert.True(ack.Ok);
        Assert.Equal(string.Empty, ack.Message);
    }

    [Fact]
    public void TheTunnelTheAgentRunsAlone_IsAnsweredAndWatched()
    {
        var control = new AgentControl();
        var probe = Measured();
        control.SetTarget("home");
        control.SetRunning(true);
        control.SetProbe(probe);

        var ack = Checks(control, new FleetLive()).LookAtLink();

        Assert.Equal(("home", probe.Now), LinkNow.TryParse(ack.Message));
        Assert.True(probe.Watched);
    }

    [Fact]
    public void EveryTunnelOfTheSet_IsAnsweredAndWatched()
    {
        var live = new FleetLive();
        var home = Measured();
        var office = Measured();
        live.Publish("home", Member(home));
        live.Publish("office", Member(office));

        var ack = Checks(new AgentControl(), live).LookAtLink();

        var rows = ack.Message.Split('\n').Select(LinkNow.TryParse).ToList();
        Assert.Equal(["home", "office"], rows.Select(row => row!.Value.Config).Order().ToList());
        Assert.True(home.Watched);
        Assert.True(office.Watched);
    }

    [Fact]
    public void ATunnelOfTheSetNothingMeasuresYet_HasNoRow()
    {
        var live = new FleetLive();
        live.Publish("home", new AgentControl());

        var ack = Checks(new AgentControl(), live).LookAtLink();

        Assert.Equal(string.Empty, ack.Message);
    }

    [Fact]
    public void OnceTheSessionIsOver_NothingOfItIsShown()
    {
        var control = Member(Measured());

        control.SetProbe(null);

        Assert.Null(control.Watch());
        Assert.Equal(LinkNow.Unknown, control.Now);
    }

    // The checks of an agent that runs the tunnel given, or the set given.
    private static CheckService Checks(AgentControl control, FleetLive live)
    {
        return new CheckService(control, null!, null!, null!, live, null!, NullLogger<CheckService>.Instance);
    }

    private static AgentControl Member(LinkLossProbe probe)
    {
        var control = new AgentControl();
        control.SetProbe(probe);
        return control;
    }

    // A probe a few echoes have come back to.
    private LinkLossProbe Measured()
    {
        var probe = new LinkLossProbe(["10.0.0.1"], clock: () => _now);
        for (var i = 0; i < 5; i++)
        {
            _now += LinkLossProbe.WatchedIntervalMs;
            probe.Record(40);
        }

        return probe;
    }
}
