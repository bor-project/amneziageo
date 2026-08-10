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
