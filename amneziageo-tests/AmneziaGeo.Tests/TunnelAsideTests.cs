using System.Net;
using AmneziaGeo.Ipc;
using Xunit;

namespace AmneziaGeo.Tests;

/// <summary>
/// A rule of the routing list can take the tunnel network, and then the far end of a healthy tunnel is routed beside
/// it: the echo to the peer and the download from the server's service inside the tunnel go out the physical path
/// and die there. Those legs are left out with the reason, and no verdict about the server is drawn from them.
/// </summary>
public sealed class TunnelAsideTests
{
    private const string ServerService = "https://10.8.0.1:51820/api/speed/down?bytes=25000000";

    // A connected tunnel whose system routes the addresses given into the tunnel, and every other beside it.
    private static ChannelProbeOptions Connected(params string[] carried) => new(
        "srv",
        true,
        TunnelTargets: ["10.8.0.1", "10.8.0.2"],
        TunnelSpeedUrl: ServerService,
        Carried: (address, _) => Task.FromResult(carried.Contains(address.ToString())));

    [Fact]
    public async Task AFarEndRoutedBesideTheTunnel_LeavesThePeerEchoOutWithTheReason()
    {
        var leg = await ChannelProbe.PeerAsync(Connected(), CancellationToken.None);

        Assert.Equal(LegState.Skipped, leg.State);
        Assert.Equal("10.8.0.1 is routed past the tunnel on this machine, so this echo says nothing about the server", leg.Note);
        Assert.Null(leg.Trace);
    }

    [Fact]
    public async Task AServiceRoutedBesideTheTunnel_LeavesTheTunnelDownloadOutWithTheReason()
    {
        var leg = await ChannelProbe.TunnelAsync(Connected(), CancellationToken.None);

        Assert.Equal(LegState.Skipped, leg.State);
        Assert.Equal("10.8.0.1 is routed past the tunnel on this machine, so this download would not ride it", leg.Note);
        Assert.Equal(-1, leg.BitsPerSecond);
    }

    [Fact]
    public async Task OneTargetTheSystemRoutesIntoTheTunnel_KeepsTheLegMeasured()
    {
        Assert.Null(await ChannelProbe.AsideAsync(Connected("10.8.0.2"), ["10.8.0.1", "10.8.0.2"], CancellationToken.None));
    }

    [Fact]
    public async Task WhereNothingSaysHowTheSystemRoutes_TheLegIsMeasuredAsBefore()
    {
        var options = new ChannelProbeOptions("srv", true, TunnelTargets: ["10.8.0.1"]);

        Assert.Null(await ChannelProbe.AsideAsync(options, ["10.8.0.1"], CancellationToken.None));
    }

    [Fact]
    public async Task AServiceNamedByAName_IsNotJudgedByItsRoute()
    {
        Assert.Null(await ChannelProbe.AsideAsync(Connected(), ["speed.example"], CancellationToken.None));
    }

    [Fact]
    public void BothLegsLeftOut_BlameNeitherTheServerNorTheTunnel()
    {
        var legs = new List<CheckLeg>
        {
            new(CheckLegs.Gateway, LegState.Ok, RttMs: 0, LossPercent: 0, MaxPacketBytes: 1392),
            new(CheckLegs.Endpoint, LegState.Ok, RttMs: 20, LossPercent: 0),
            new(CheckLegs.Handshake, LegState.Ok, AgeSeconds: 0),
            new(CheckLegs.Peer, LegState.Skipped, Note: "10.8.0.1 is routed past the tunnel on this machine, so this echo says nothing about the server"),
            new(CheckLegs.Beyond, LegState.Skipped, Note: "the routing list carries only what it names, so this echo says nothing about the path past the exit"),
            new(CheckLegs.Tunnel, LegState.Skipped, Note: "10.8.0.1 is routed past the tunnel on this machine, so this download would not ride it"),
            new(CheckLegs.Direct, LegState.Ok, BitsPerSecond: 262_550_000),
        };

        var (key, args, culprit) = ChannelVerdict.Decide(legs, true);

        Assert.Equal(CheckVerdicts.HealthyOutsideTunnel, key);
        Assert.Equal(string.Empty, culprit);
        Assert.DoesNotContain("the fault is the server", CheckPhrase.English(key, args), StringComparison.Ordinal);
    }
}
