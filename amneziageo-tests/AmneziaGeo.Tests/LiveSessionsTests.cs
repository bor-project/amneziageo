using AmneziaGeo.Ipc;

using Xunit;

namespace AmneziaGeo.Tests;

/// <summary>
/// What the relay holds, carried between two processes as text. The head reads it to name the destination the
/// check times the tunnel against, so a row that loses its bytes on the way loses the comparison with it.
/// </summary>
public sealed class LiveSessionsTests
{
    [Fact]
    public void TheSnapshot_SurvivesTheTrip()
    {
        var report = new SessionReport(
            1_700_000_000_000,
            [
                new LiveSession("iptv.example", "proxy", 900_000, 4_000_000, 1, 120, 0, "org.videolan.vlc"),
                new LiveSession("ads.example", "block", 0, 0, 0, 30, 12),
            ],
            7,
            2,
            1_400_000);

        var back = SessionReport.Parse(report.ToPayload());

        Assert.Equal(2, back.Sessions.Count);
        Assert.Equal("iptv.example", back.Sessions[0].Host);
        Assert.Equal("org.videolan.vlc", back.Sessions[0].App);
        Assert.Equal(4_000_000, back.Sessions[0].BitsPerSecond);
        Assert.Equal("block", back.Sessions[1].Verdict);
        Assert.Equal(7, back.Held);
        Assert.Equal(2, back.Undecided);
        Assert.Equal(1_400_000, back.TotalBytes);
        Assert.Equal(1_700_000_000_000, back.UnixMs);
    }

    [Fact]
    public void TheBusiestDestination_IsTheOneTheTunnelIsTimedAgainst()
    {
        var report = new SessionReport(
            1,
            [
                new LiveSession("small.example", "proxy", 1_000),
                new LiveSession("iptv.example", "proxy", 900_000),
            ]);

        Assert.Equal("iptv.example", report.Busiest?.Host);
        Assert.Null(SessionReport.Empty.Busiest);
    }

    [Fact]
    public void ADestinationWithoutByteCounts_KeepsItsVerdictAndClock()
    {
        var report = new SessionReport(
            5,
            [new LiveSession("10.1.2.3", LiveSession.Undecided, IdleSeconds: 12)],
            40,
            7);

        var back = SessionReport.Parse(report.ToPayload());

        Assert.Equal(LiveSession.Undecided, back.Sessions[0].Verdict);
        Assert.Equal(0, back.Sessions[0].Bytes);
        Assert.Equal(-1, back.Sessions[0].BitsPerSecond);
        Assert.Equal(-1, back.Sessions[0].AgeSeconds);
        Assert.Equal(12, back.Sessions[0].IdleSeconds);
        Assert.False(back.Sessions[0].Stalled);
        Assert.Equal(40, back.Held);
        Assert.Equal(7, back.Undecided);
    }

    [Fact]
    public void WhySettledIt_AndWhatIsLeftOnIt_SurviveTheTrip()
    {
        var report = new SessionReport(
            9,
            [
                new LiveSession("142.250.185.78", "proxy", IdleSeconds: 3, Name: "youtube.com",
                    Path: LiveSession.PathTunnel, Reason: LiveSession.ReasonName, LeftSeconds: 297),
                new LiveSession("10.0.1.1/32", "proxy", Path: LiveSession.PathTunnel,
                    Reason: LiveSession.ReasonService),
            ]);

        var back = SessionReport.Parse(report.ToPayload());

        Assert.Equal(LiveSession.ReasonName, back.Sessions[0].Reason);
        Assert.Equal("youtube.com", back.Sessions[0].Name);
        Assert.Equal(LiveSession.PathTunnel, back.Sessions[0].Route);
        Assert.Equal(297, back.Sessions[0].LeftSeconds);
        Assert.Equal(LiveSession.ReasonService, back.Sessions[1].Reason);
        Assert.Equal(-1, back.Sessions[1].LeftSeconds);
    }

    [Fact]
    public void TheModeAndTheWayCounts_SurviveTheTrip()
    {
        var report = new SessionReport(
            11,
            [new LiveSession("1.2.3.4", "direct", Path: LiveSession.PathDirect, Reason: LiveSession.ReasonRange)],
            140,
            38,
            0,
            96,
            38,
            6,
            SessionReport.ModeSplit,
            "Обход блокировок");

        var back = SessionReport.Parse(report.ToPayload());

        Assert.Equal(SessionReport.ModeSplit, back.Mode);
        Assert.Equal("Обход блокировок", back.List);
        Assert.Equal(96, back.Tunnel);
        Assert.Equal(38, back.Direct);
        Assert.Equal(6, back.Block);
        Assert.Equal(140, back.Held);
        Assert.Equal(38, back.Undecided);
    }

    [Fact]
    public void AReportWithoutTheModeRow_StillCarriesItsDestinations()
    {
        var back = SessionReport.Parse("session\t1.2.3.4\tproxy\tidle=4\nheld\t2\t1\t0\t77");

        Assert.Single(back.Sessions);
        Assert.Equal(2, back.Held);
        Assert.Equal(77, back.UnixMs);
        Assert.Equal(string.Empty, back.Mode);
        Assert.Equal(0, back.Tunnel);
    }

    [Fact]
    public void AHeldConnectionCarryingNothing_ReadsAsStalled()
    {
        Assert.True(new LiveSession("iptv.example", "proxy", 900_000, 0, 1, 300, LiveSession.StallSeconds).Stalled);
        Assert.False(new LiveSession("iptv.example", "proxy", 900_000, 0, 0, 300, 300).Stalled);
        Assert.Equal(
            1,
            new SessionReport(
                1,
                [
                    new LiveSession("held.example", "proxy", 1, 0, 1, 60, 60),
                    new LiveSession("idle.example", "proxy", 1, 0, 0, 60, 60),
                ]).Stalled);
    }
}
