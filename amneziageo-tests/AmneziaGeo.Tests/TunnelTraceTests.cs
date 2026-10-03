using AmneziaGeo.Ipc;

using Xunit;

namespace AmneziaGeo.Tests;

/// <summary>
/// The check counts its own probes to tell where a silent tunnel loses them, and names the server only where
/// they left and nothing came back.
/// </summary>
public sealed class TunnelTraceTests
{
    private const int Probes = 8;
    private const int Payload = 1000;

    private static TunnelTrace Trace(long handed, long sent, long received, long returned = 0)
    {
        return new TunnelTrace(Probes, Payload, handed, returned, sent, received);
    }

    // The ladder of a tunnel that passes nothing while its session lives and the same download beside it runs.
    private static List<CheckLeg> DeadTunnel(TunnelTrace? trace, CheckLeg? beyond = null)
    {
        var legs = new List<CheckLeg>
        {
            new(CheckLegs.Gateway, LegState.Ok, RttMs: 1, LossPercent: 0, MaxPacketBytes: 1472),
            new(CheckLegs.Endpoint, LegState.Ok, RttMs: 54, LossPercent: 0),
            new(CheckLegs.Handshake, LegState.Ok, AgeSeconds: 12, RekeySeconds: 103),
            new(CheckLegs.Peer, LegState.Unknown, Note: "nothing inside the tunnel answered an echo", Trace: trace),
        };
        if (beyond is not null)
        {
            legs.Add(beyond);
        }

        legs.Add(new CheckLeg(CheckLegs.Tunnel, LegState.Bad, BitsPerSecond: 0, Note: "the download never started, against 10.8.0.1"));
        legs.Add(new CheckLeg(CheckLegs.Direct, LegState.Ok, BitsPerSecond: 63_540_000));
        return legs;
    }

    [Fact]
    public void ProbesTheSystemNeverGaveTheAdapter_AreLostBeforeTheTunnel()
    {
        Assert.Equal(TunnelSteps.NotHanded, Trace(handed: 0, sent: 0, received: 0).Step);
        Assert.Equal(TunnelSteps.NotHanded, Trace(handed: 5, sent: 9_000, received: 0).Step);
    }

    [Fact]
    public void ProbesTheTunnelTookAndDidNotSend_AreLostInTheTunnel()
    {
        Assert.Equal(TunnelSteps.NotSent, Trace(handed: 8, sent: 148, received: 0).Step);
    }

    [Fact]
    public void ProbesThatLeftAndBroughtNothingBack_AreUnanswered()
    {
        Assert.Equal(TunnelSteps.Unanswered, Trace(handed: 8, sent: 8_640, received: 124).Step);
    }

    [Fact]
    public void AnswersThatCameIntoTheTunnel_AreLostOnTheWayToTheProgram()
    {
        Assert.Equal(TunnelSteps.NotDelivered, Trace(handed: 8, sent: 8_640, received: 8_640, returned: 8).Step);
    }

    [Fact]
    public void WhatATunnelBusyWithOtherTrafficReceives_IsNotTakenForAnswers()
    {
        var busy = new TunnelTrace(Probes, Payload, 8, 40, 9_000, 60_000, Quiet: 50_000);
        var uncounted = new TunnelTrace(Probes, Payload, 8, 8, 8_640, 8_640, Quiet: -1);

        Assert.Equal(string.Empty, busy.Step);
        Assert.Equal(string.Empty, uncounted.Step);
        Assert.Equal(string.Empty, busy.Describe());
    }

    [Fact]
    public void TheSecondBeforeTheBurst_IsWhatTheTunnelHeardWithNothingSent()
    {
        Assert.Equal(92, TunnelTrace.Heard(new TunnelCounters(1, 1, 500, 1_000), new TunnelCounters(1, 1, 532, 1_092)));
        Assert.Equal(-1, TunnelTrace.Heard(new TunnelCounters(), new TunnelCounters(1, 1, 532, 1_092)));
    }

    [Fact]
    public void WhereTheAdapterIsNotCounted_TheEngineStillNamesTheStep()
    {
        Assert.Equal(TunnelSteps.Unanswered, Trace(handed: -1, sent: 8_640, received: 0).Step);
        Assert.Equal(TunnelSteps.NotDelivered, Trace(handed: -1, sent: 8_640, received: 8_640).Step);
    }

    [Fact]
    public void WhereTheEngineIsNotCounted_NothingIsNamed()
    {
        Assert.Equal(string.Empty, Trace(handed: 8, sent: -1, received: -1).Step);
    }

    [Fact]
    public void TheBurst_IsWhatTheCountersMovedBy()
    {
        var trace = TunnelTrace.Between(
            new TunnelCounters(100, 90, 519_000, 1_800_000),
            new TunnelCounters(108, 90, 527_640, 1_800_092),
            Probes,
            Payload);

        Assert.Equal(8, trace.Handed);
        Assert.Equal(0, trace.Returned);
        Assert.Equal(8_640, trace.Sent);
        Assert.Equal(92, trace.Received);
        Assert.Equal(TunnelSteps.Unanswered, trace.Step);
    }

    [Fact]
    public void ACounterUnknownOnEitherSide_StaysUnknown()
    {
        var trace = TunnelTrace.Between(new TunnelCounters(-1, -1, 1_000, 2_000), new TunnelCounters(5, 5, 9_640, 2_000), Probes, Payload);

        Assert.Equal(-1, trace.Handed);
        Assert.Equal(8_640, trace.Sent);
    }

    [Fact]
    public void PacketsThatNeverReachTheTunnel_AreBlamedOnThisDeviceNotOnTheServer()
    {
        var (key, args, culprit) = ChannelVerdict.Decide(DeadTunnel(Trace(handed: 0, sent: 0, received: 0)), connected: true);

        Assert.Equal(CheckVerdicts.TunnelNotFed, key);
        Assert.Equal(CheckLegs.Peer, culprit);
        Assert.Equal(new[] { "0", "8" }, args);
        Assert.Contains("on this device", CheckPhrase.English(key, args), StringComparison.Ordinal);
        Assert.DoesNotContain("the fault is the server", CheckPhrase.English(key, args), StringComparison.Ordinal);
    }

    [Fact]
    public void PacketsTheTunnelDoesNotSend_AreBlamedOnThisDevice()
    {
        var (key, args, _) = ChannelVerdict.Decide(DeadTunnel(Trace(handed: 8, sent: 148, received: 0)), connected: true);

        Assert.Equal(CheckVerdicts.TunnelNotSent, key);
        Assert.Equal(new[] { "148 B", "7.8 KB" }, args);
        Assert.Contains("not the server", CheckPhrase.English(key, args), StringComparison.Ordinal);
    }

    [Fact]
    public void AnswersThatDieOnTheWayToTheProgram_AreBlamedOnThisDevice()
    {
        var (key, args, culprit) = ChannelVerdict.Decide(DeadTunnel(Trace(handed: 8, sent: 8_640, received: 8_640, returned: 8)), connected: true);

        Assert.Equal(CheckVerdicts.TunnelNotDelivered, key);
        Assert.Equal(CheckLegs.Peer, culprit);
        Assert.Contains("on this device", CheckPhrase.English(key, args), StringComparison.Ordinal);
    }

    [Fact]
    public void ATunnelThatSendsAndHearsNothing_IsTheOneCaseTheServerIsNamedIn()
    {
        var (key, args, culprit) = ChannelVerdict.Decide(DeadTunnel(Trace(handed: 8, sent: 8_640, received: 124)), connected: true);

        Assert.Equal(CheckVerdicts.TunnelUnanswered, key);
        Assert.Equal(CheckLegs.Tunnel, culprit);
        Assert.Equal(new[] { "8.4 KB" }, args);
        Assert.Contains("the fault is the server", CheckPhrase.English(key, args), StringComparison.Ordinal);
    }

    [Fact]
    public void ADeadTunnelNothingWasCountedIn_IsNotBlamedOnTheServer()
    {
        var (key, args, _) = ChannelVerdict.Decide(DeadTunnel(null), connected: true);

        Assert.Equal(CheckVerdicts.TunnelSilent, key);
        Assert.DoesNotContain("the fault is the server", CheckPhrase.English(key, args), StringComparison.Ordinal);
    }

    [Fact]
    public void ATunnelThatCarriesPastTheExit_KeepsTheVerdictOfItsDownload()
    {
        var beyond = new CheckLeg(CheckLegs.Beyond, LegState.Ok, RttMs: 20, LossPercent: 0, Note: "1.1.1.1");

        var (key, _, _) = ChannelVerdict.Decide(DeadTunnel(null, beyond), connected: true);

        Assert.Equal(CheckVerdicts.TunnelBehindDirect, key);
    }

    [Fact]
    public void ALostBurstOnATunnelThatCarries_IsNotBlamedOnThisDevice()
    {
        var lost = Trace(handed: 0, sent: 0, received: 0);
        var beyond = new CheckLeg(CheckLegs.Beyond, LegState.Ok, RttMs: 20, LossPercent: 0, Note: "1.1.1.1");
        var carrying = new List<CheckLeg>
        {
            new(CheckLegs.Gateway, LegState.Ok, RttMs: 1, LossPercent: 0, MaxPacketBytes: 1472),
            new(CheckLegs.Endpoint, LegState.Ok, RttMs: 54, LossPercent: 0),
            new(CheckLegs.Handshake, LegState.Ok, AgeSeconds: 12, RekeySeconds: 103),
            new(CheckLegs.Peer, LegState.Unknown, Note: "nothing inside the tunnel answered an echo", Trace: lost),
            new(CheckLegs.Tunnel, LegState.Ok, BitsPerSecond: 300_000_000),
        };

        Assert.Equal(CheckVerdicts.Healthy, ChannelVerdict.Decide(carrying, connected: true).Key);
        Assert.Equal(CheckVerdicts.TunnelBehindDirect, ChannelVerdict.Decide(DeadTunnel(lost, beyond), connected: true).Key);
    }

    [Fact]
    public void TheLostStep_IsSaidInTheLegAndSurvivesTheTrip()
    {
        var leg = new CheckLeg(CheckLegs.Peer, LegState.Unknown, Note: "nothing inside the tunnel answered an echo",
            Trace: Trace(handed: 8, sent: 8_640, received: 124));

        var back = CheckLeg.TryParse(leg.ToRow());

        Assert.NotNull(back);
        Assert.Equal(leg.Trace, back.Trace);
        Assert.Equal("nothing inside the tunnel answered an echo, the tunnel sent 8.4 KB and got no answer", back.Describe());
    }

    [Fact]
    public void TheTotalsOfTheSession_AreShownWithTheHandshake()
    {
        var options = new ChannelProbeOptions("srv", Connected: true, HandshakeAgeSeconds: 12, RxBytes: 1_887_436, TxBytes: 531_456);

        var leg = ChannelProbe.Handshake(options);

        Assert.Equal("age 12 s, rx 1.8 MB, tx 519 KB", leg.Describe());
    }
}
