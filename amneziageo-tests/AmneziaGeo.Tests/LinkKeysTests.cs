using AmneziaGeo.Ipc;

using Xunit;

namespace AmneziaGeo.Tests;

/// <summary>
/// A session that renews its keys on schedule shows the interval, and the rate only from the one that counts as
/// re-establishing.
/// </summary>
public sealed class LinkKeysTests
{
    [Fact]
    public void TheIntervalBetweenTheLastTwoHandshakes_IsKeptWithTheReading()
    {
        var now = 0L;
        var meter = new LinkMeter(() => now);

        Assert.Equal(-1, meter.Sample(0, 0, 1_000).RekeySeconds);

        now = 103_000;
        Assert.Equal(103, meter.Sample(0, 0, 1_103).RekeySeconds);

        now = 110_000;
        Assert.Equal(103, meter.Sample(0, 0, 1_103).RekeySeconds);
    }

    [Fact]
    public void APauseLongerThanASilentSessionLasts_IsNotTakenForAnInterval()
    {
        var now = 0L;
        var meter = new LinkMeter(() => now);
        meter.Sample(0, 0, 1_000);
        now = 103_000;
        meter.Sample(0, 0, 1_103);

        now = 9_000_000;
        var reading = meter.Sample(0, 0, 10_000);

        Assert.Equal(103, reading.RekeySeconds);
    }

    [Fact]
    public void AStoppedTunnel_LeavesNoIntervalBehind()
    {
        var now = 0L;
        var meter = new LinkMeter(() => now);
        meter.Sample(0, 0, 1_000);
        now = 103_000;
        meter.Sample(0, 0, 1_103);

        meter.Reset();

        Assert.Equal(-1, meter.Sample(0, 0, 2_000).RekeySeconds);
    }

    [Theory]
    [InlineData(103, 1, 3, "a new key every 103 s")]
    [InlineData(-1, 1, 3, "")]
    [InlineData(15, 4, 3, "a new key every 15 s, handshakes 4/min")]
    [InlineData(-1, 6, 6, "handshakes 6/min")]
    public void TheKeysOfASession_AreNamedByTheirIntervalAndByTheRateOnlyFromTheOneThatCounts(int rekeySeconds, int perMinute, int churnPerMinute, string expected)
    {
        Assert.Equal(expected, LinkHealth.KeysText(rekeySeconds, perMinute, churnPerMinute));
    }

    [Fact]
    public void TheLinkLineOfAnOrdinarySession_NamesNoRatePerMinute()
    {
        var reading = new LinkReading(74_000, 1_955_000, 1, 8, RekeySeconds: 103);

        var line = reading.Describe(LinkHealth.ChurnPerMinute);

        Assert.Equal("receives 74 kbit/s, sends 1955 kbit/s, a new key every 103 s, loses 8%", line);
    }

    [Fact]
    public void TheLinkLineOfASessionThatFoundNothingToEcho_SaysSo()
    {
        var line = new LinkReading(0, 0, 0).Describe(LinkHealth.ChurnPerMinute);

        Assert.Equal("receives 0 kbit/s, sends 0 kbit/s, loses nothing that answers", line);
    }

    [Fact]
    public void TheHandshakeLegOfAnOrdinarySession_ShowsTheIntervalAndNoRate()
    {
        var options = new ChannelProbeOptions("srv", Connected: true, HandshakeAgeSeconds: 47, RekeysPerMinute: 1, RekeySeconds: 103);

        var leg = ChannelProbe.Handshake(options);

        Assert.Equal(-1, leg.RekeysPerMinute);
        Assert.Equal("age 47 s, a new key every 103 s", leg.Describe());
    }

    [Fact]
    public void TheHandshakeLegOfASessionBeingReestablished_NamesTheRate()
    {
        var options = new ChannelProbeOptions("srv", Connected: true, HandshakeAgeSeconds: 3, RekeysPerMinute: 4, Churning: true, RekeySeconds: 15);

        var leg = ChannelProbe.Handshake(options);

        Assert.Equal(LegState.Bad, leg.State);
        Assert.Equal(4, leg.RekeysPerMinute);
        Assert.Equal("age 3 s, a new key every 15 s, 4 rekey(s) per minute", leg.Describe());
    }

    [Fact]
    public void ARateBelowTheConfigsOwnSchedule_IsNotNamed()
    {
        var options = new ChannelProbeOptions("srv", Connected: true, HandshakeAgeSeconds: 5, RekeysPerMinute: 4, RekeySeconds: 15, ChurnPerMinute: 6);

        Assert.Equal(-1, ChannelProbe.Handshake(options).RekeysPerMinute);
    }

    [Fact]
    public void TheInterval_SurvivesTheTrip()
    {
        var report = new CheckReport(
            1_700_000_000_000,
            "srv",
            [new CheckLeg(CheckLegs.Handshake, LegState.Ok, AgeSeconds: 47, RekeySeconds: 103)],
            CheckVerdicts.Healthy,
            ["39"],
            string.Empty);

        var back = CheckReport.Parse(report.ToPayload());

        Assert.Equal(103, back.Legs[0].RekeySeconds);
        Assert.Equal(47, back.Legs[0].AgeSeconds);
    }
}
