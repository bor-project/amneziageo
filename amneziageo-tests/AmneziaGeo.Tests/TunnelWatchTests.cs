using AmneziaGeo.Ipc;
using Xunit;

namespace AmneziaGeo.Tests;

/// <summary>
/// The head follows the tunnel process: a session that went with it is raised again while the window is on the screen.
/// </summary>
public sealed class TunnelWatchTests
{
    private const int Limit = 3;
    private const long WindowMs = 120_000;

    [Fact]
    public void ASessionWithAProcess_IsLeftAlone()
    {
        var watch = new TunnelWatch(Limit, WindowMs);

        Assert.Null(watch.Look(active: true, running: true, wanted: true, shown: true, now: 0));
        Assert.Null(watch.Look(active: false, running: false, wanted: true, shown: true, now: 0));
    }

    [Fact]
    public void AConnectWhoseProcessIsYetToCome_IsLeftAlone()
    {
        var watch = new TunnelWatch(Limit, WindowMs);
        watch.Asked(running: false);

        Assert.Null(watch.Look(active: true, running: false, wanted: true, shown: true, now: 0));
    }

    [Fact]
    public void ASessionGoneWithItsProcess_IsRaisedWhileTheWindowIsOnTheScreen()
    {
        var watch = new TunnelWatch(Limit, WindowMs);
        watch.Heard();

        Assert.Equal(TunnelGoneStep.Raise, watch.Look(active: true, running: false, wanted: true, shown: true, now: 0));
        Assert.Null(watch.Look(active: true, running: false, wanted: true, shown: true, now: 3_000));
    }

    [Fact]
    public void AProcessSeenByALook_CountsAsSeen()
    {
        var watch = new TunnelWatch(Limit, WindowMs);
        watch.Asked(running: false);
        watch.Look(active: true, running: true, wanted: true, shown: true, now: 0);

        Assert.Equal(TunnelGoneStep.Raise, watch.Look(active: true, running: false, wanted: true, shown: true, now: 3_000));
    }

    [Fact]
    public void ASessionNobodyWants_ShowsAsDown()
    {
        var watch = new TunnelWatch(Limit, WindowMs);
        watch.Heard();

        Assert.Equal(TunnelGoneStep.Down, watch.Look(active: true, running: false, wanted: false, shown: true, now: 0));
        Assert.False(watch.Due(active: false, wanted: true, now: 1_000));
    }

    [Fact]
    public void ASessionGoneBehindAHiddenWindow_IsRaisedOnceTheWindowIsBack()
    {
        var watch = new TunnelWatch(Limit, WindowMs);
        watch.Heard();

        Assert.Equal(TunnelGoneStep.Owed, watch.Look(active: true, running: false, wanted: true, shown: false, now: 0));
        Assert.True(watch.Due(active: false, wanted: true, now: 60_000));
        Assert.False(watch.Due(active: false, wanted: true, now: 61_000));
    }

    [Fact]
    public void AnOwedSession_IsDroppedByAConnectAndByADisconnect()
    {
        var asked = new TunnelWatch(Limit, WindowMs);
        asked.Heard();
        asked.Look(active: true, running: false, wanted: true, shown: false, now: 0);
        asked.Asked(running: false);

        var dropped = new TunnelWatch(Limit, WindowMs);
        dropped.Heard();
        dropped.Look(active: true, running: false, wanted: true, shown: false, now: 0);
        dropped.Dropped();

        Assert.False(asked.Due(active: false, wanted: true, now: 1_000));
        Assert.False(dropped.Due(active: false, wanted: true, now: 1_000));
    }

    [Fact]
    public void AnOwedSession_IsNotRaisedOverALiveOneOrOneNobodyWants()
    {
        var live = new TunnelWatch(Limit, WindowMs);
        live.Heard();
        live.Look(active: true, running: false, wanted: true, shown: false, now: 0);

        var unwanted = new TunnelWatch(Limit, WindowMs);
        unwanted.Heard();
        unwanted.Look(active: true, running: false, wanted: true, shown: false, now: 0);

        Assert.False(live.Due(active: true, wanted: true, now: 1_000));
        Assert.False(unwanted.Due(active: false, wanted: false, now: 1_000));
    }

    [Fact]
    public void Raising_StopsAfterTheLimitAndComesBackAfterTheWindow()
    {
        var watch = new TunnelWatch(Limit, WindowMs);
        var steps = new List<TunnelGoneStep?>();
        foreach (var now in new long[] { 0, 10_000, 20_000, 30_000, 125_000 })
        {
            watch.Heard();
            steps.Add(watch.Look(active: true, running: false, wanted: true, shown: true, now: now));
        }

        Assert.Equal(
            [TunnelGoneStep.Raise, TunnelGoneStep.Raise, TunnelGoneStep.Raise, TunnelGoneStep.GiveUp, TunnelGoneStep.Raise],
            steps);
    }
}
