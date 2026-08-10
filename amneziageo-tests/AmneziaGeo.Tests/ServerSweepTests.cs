using AmneziaGeo.Ipc;

using Xunit;

namespace AmneziaGeo.Tests;

/// <summary>
/// The sweep answers one question - which server to be on right now - and its answer has to survive the trip to
/// the window and to the journal. The numbers below are the ones the support thread saw on 9 August.
/// </summary>
public sealed class ServerSweepTests
{
    private static SweepRow Row(string name, int rtt, int loss, bool live = false)
    {
        return new SweepRow(name, ChannelVerdict.StateFor(loss), rtt, 0, loss, live);
    }

    [Fact]
    public void TheCleanestServer_IsTheOneToBeOn()
    {
        var (key, args, best) = SweepVerdict.Decide(
            [Row("bor", 40, 0), Row("myvpn", 20, 10), Row("shared", 30, 0)],
            gateway: null,
            inTunnel: false);

        Assert.Equal(CheckVerdicts.SweepBest, key);
        Assert.Equal("shared", best);
        Assert.Equal("shared", args[0]);
        Assert.Equal("30", args[1]);
        Assert.Equal("0", args[2]);
    }

    [Fact]
    public void ALossyServerThatIsUp_IsToldToStepAside()
    {
        var (key, args, best) = SweepVerdict.Decide(
            [Row("myvpn", 55, 25, live: true), Row("bor", 40, 0)],
            gateway: null,
            inTunnel: false);

        Assert.Equal(CheckVerdicts.SweepSwitch, key);
        Assert.Equal("bor", best);
        Assert.Equal("myvpn", args[0]);
        Assert.Equal("bor", args[1]);
        Assert.Equal("40", args[2]);
        Assert.Equal("0", args[3]);
        Assert.Contains("change the server", CheckPhrase.English(key, args), StringComparison.Ordinal);
    }

    [Fact]
    public void AServerThatIsUpAndBarelyBehind_IsLeftAlone()
    {
        var (key, _, best) = SweepVerdict.Decide(
            [Row("myvpn", 55, 2, live: true), Row("bor", 40, 0)],
            gateway: null,
            inTunnel: false);

        Assert.Equal(CheckVerdicts.SweepBest, key);
        Assert.Equal("bor", best);
    }

    [Fact]
    public void AServerThatIsUpAndSilent_IsToldToStepAside()
    {
        var (key, _, _) = SweepVerdict.Decide(
            [new SweepRow("myvpn", LegState.Unknown, Live: true), Row("bor", 40, 0)],
            gateway: null,
            inTunnel: false);

        Assert.Equal(CheckVerdicts.SweepSwitch, key);
    }

    [Fact]
    public void ProbesThatRodeTheTunnel_SayNoServerWasCompared()
    {
        var (key, args, best) = SweepVerdict.Decide(
            [Row("myvpn", 55, 25, live: true), Row("bor", 40, 0)],
            gateway: null,
            inTunnel: true);

        Assert.Equal(CheckVerdicts.SweepInTunnel, key);
        Assert.Equal("bor", best);
        Assert.Equal("bor", args[0]);
    }

    [Fact]
    public void ALossyGateway_IsBlamedBeforeAnyServer()
    {
        var (key, args, best) = SweepVerdict.Decide(
            [Row("bor", 40, 20), Row("myvpn", 55, 25)],
            new CheckLeg(CheckLegs.Gateway, LegState.Bad, RttMs: 3, LossPercent: 22),
            inTunnel: false);

        Assert.Equal(CheckVerdicts.LocalLoss, key);
        Assert.Equal("22", args[0]);
        Assert.Equal("bor", best);
    }

    [Fact]
    public void ASweepNothingAnswered_NamesNoServer()
    {
        var (key, _, best) = SweepVerdict.Decide(
            [new SweepRow("bor", LegState.Unknown), new SweepRow("myvpn", LegState.Unknown)],
            gateway: null,
            inTunnel: false);

        Assert.Equal(CheckVerdicts.SweepSilent, key);
        Assert.Equal(string.Empty, best);
    }

    [Fact]
    public void ASweepWithNothingSaved_SaysSo()
    {
        var (key, _, _) = SweepVerdict.Decide([], gateway: null, inTunnel: false);

        Assert.Equal(CheckVerdicts.SweepEmpty, key);
    }

    [Fact]
    public void ThePayload_SurvivesTheTrip()
    {
        var report = new SweepReport(
            1_700_000_000_000,
            [
                new SweepRow("bor (2)", LegState.Ok, 40, 3, 0, Best: true, Note: "46.8.237.222"),
                new SweepRow("myvpn", LegState.Weak, 55, 8, 25, Live: true),
            ],
            new CheckLeg(CheckLegs.Gateway, LegState.Ok, RttMs: 2, JitterMs: 1, LossPercent: 0, Note: "192.168.1.1"),
            CheckVerdicts.SweepSwitch,
            ["myvpn", "bor (2)", "40", "0"],
            "bor (2)");

        var back = SweepReport.Parse(report.ToPayload());

        Assert.Equal(2, back.Servers.Count);
        Assert.Equal("bor (2)", back.Servers[0].Config);
        Assert.True(back.Servers[0].Best);
        Assert.False(back.Servers[0].Live);
        Assert.Equal("46.8.237.222", back.Servers[0].Note);
        Assert.True(back.Servers[1].Live);
        Assert.Equal(25, back.Servers[1].LossPercent);
        Assert.Equal(2, back.Gateway?.RttMs);
        Assert.Equal(CheckVerdicts.SweepSwitch, back.VerdictKey);
        Assert.Equal("bor (2)", back.Best);
        Assert.Equal("myvpn", back.VerdictArgs[0]);
    }

    [Fact]
    public void TheRenderedSweep_MarksTheBestAndCarriesTheVerdict()
    {
        var report = new SweepReport(
            1_700_000_000_000,
            [
                new SweepRow("bor", LegState.Ok, 40, 3, 0, Best: true),
                new SweepRow("myvpn", LegState.Weak, 55, 8, 25, Live: true),
            ],
            null,
            CheckVerdicts.SweepSwitch,
            ["myvpn", "bor", "40", "0"],
            "bor");

        var text = report.Render();

        Assert.Contains("* bor", text, StringComparison.Ordinal);
        Assert.Contains("running now", text, StringComparison.Ordinal);
        Assert.Contains("loss 25%", text, StringComparison.Ordinal);
        Assert.Contains("answers better", text, StringComparison.Ordinal);
    }
}
