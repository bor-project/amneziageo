using AmneziaGeo.Ipc;
using Xunit;

namespace AmneziaGeo.Tests;

/// <summary>
/// What the screen is shown of a running tunnel: the share lost over its last echoes and the best time of the
/// latest ones, counted over the outage that is on or over the link since it came back from the last one, and
/// the pace the echoes go at while the screen shows them.
/// </summary>
public sealed class LinkNowTests
{
    // How long an echo that never comes back is waited for.
    private const int TimeoutMs = 1_500;

    private long _now;

    [Fact]
    public void BeforeAnythingIsMeasured_NothingIsShown()
    {
        var probe = Probe();

        Assert.Equal(LinkNow.Unknown, probe.Now);
    }

    [Fact]
    public void TheFirstEchoes_ShowTheTimeAndNoShareYet()
    {
        var probe = Probe();

        Echo(probe, 3, true);

        Assert.Equal(LinkHealth.LossUnknown, probe.Now.LossPercent);
        Assert.Equal(40, probe.Now.RttMs);
    }

    [Fact]
    public void TheShownShare_IsTakenOverTheLastThirtyEchoes()
    {
        var probe = Probe();
        Echo(probe, 5, true);
        Echo(probe, 1, false);

        Echo(probe, 29, true);
        Assert.Equal(3, probe.Now.LossPercent);

        Echo(probe, 1, true);
        Assert.Equal(0, probe.Now.LossPercent);
    }

    [Fact]
    public void ASingleLostEcho_IsLossAndNoOutage()
    {
        var probe = Probe();

        Echo(probe, 10, true);
        Echo(probe, 1, false);
        Echo(probe, 3, true);

        Assert.Equal(7, probe.Now.LossPercent);
        Assert.Equal(1, probe.Now.LossStreak);
    }

    [Fact]
    public void AShortSilence_IsLossOfTheLinkAndNoOutageYet()
    {
        var probe = Probe();

        Echo(probe, 6, true);
        Echo(probe, 2, false);

        Assert.Equal(25, probe.Now.LossPercent);
        Assert.Equal(2, probe.Now.LossStreak);
        Assert.Equal(40, probe.Now.RttMs);
    }

    [Fact]
    public void ASilenceLongEnough_IsShownAsTheOutageItIs()
    {
        var probe = Probe();

        Echo(probe, 6, true);
        Echo(probe, 3, false);

        Assert.Equal(100, probe.Now.LossPercent);
        Assert.Equal(3, probe.Now.LossStreak);
    }

    [Fact]
    public void WhileAnOutageIsOn_NoTimeIsShown()
    {
        var probe = Probe();

        Echo(probe, 4, true, 20);
        Echo(probe, 3, false);

        Assert.Equal(-1, probe.Now.RttMs);
        Assert.Equal(20, probe.RttMs);
    }

    [Fact]
    public void ASilenceOfALinkNobodyWatches_IsAnOutageFromItsSecondEcho()
    {
        var probe = Probe();

        Echo(probe, 6, true, 40, LinkLossProbe.IntervalMs);
        Echo(probe, LinkLossProbe.OutageEchoes, false, 40, LinkLossProbe.IntervalMs);

        Assert.Equal(100, probe.Now.LossPercent);
    }

    [Fact]
    public void AnOutageTheLinkCameBackFrom_IsNotShownAsLoss()
    {
        var probe = Probe();

        Echo(probe, 10, true);
        Echo(probe, 5, false);
        Echo(probe, LinkLossProbe.BackEchoes, true);

        Assert.Equal(0, probe.Now.LossPercent);
        Assert.Equal(0, probe.Now.LossStreak);
        Assert.Equal(27, probe.Percent);
    }

    [Fact]
    public void UntilTheLinkAnswersSteadilyAgain_TheOutageStaysOnTheScreen()
    {
        var probe = Probe();

        Echo(probe, 10, true);
        Echo(probe, 5, false);
        Echo(probe, LinkLossProbe.BackEchoes - 1, true);

        Assert.Equal(71, probe.Now.LossPercent);
        Assert.Equal(5, probe.Now.LossStreak);
    }

    [Fact]
    public void AComebackThatStumbles_IsCountedFromTheOneThatHolds()
    {
        var probe = Probe();

        Echo(probe, 4, true);
        Echo(probe, 3, false);
        Echo(probe, 1, true);
        Echo(probe, 1, false);
        Echo(probe, LinkLossProbe.BackEchoes, true);

        Assert.Equal(0, probe.Now.LossPercent);
        Assert.Equal(0, probe.Now.LossStreak);
    }

    [Fact]
    public void ALinkThatLosesEchoesInShortRuns_KeepsItsLossesOnTheScreen()
    {
        var probe = Probe();

        for (var round = 0; round < 8; round++)
        {
            Echo(probe, 2, false);
            Echo(probe, 1, true);
        }

        Assert.Equal(66, probe.Now.LossPercent);
        Assert.Equal(2, probe.Now.LossStreak);
    }

    [Fact]
    public void AnOutageTheLinkNeverComesBackFrom_StaysOnTheScreen()
    {
        var probe = Probe();

        for (var round = 0; round < 6; round++)
        {
            Echo(probe, 3, false);
            Echo(probe, 1, true);
        }

        Assert.Equal(75, probe.Now.LossPercent);
        Assert.Equal(3, probe.Now.LossStreak);
    }

    [Fact]
    public void LossAfterTheLinkCameBack_IsShownAgain()
    {
        var probe = Probe();
        Echo(probe, 6, true);
        Echo(probe, 4, false);
        Echo(probe, LinkLossProbe.BackEchoes, true);

        Echo(probe, 1, false);

        Assert.Equal(25, probe.Now.LossPercent);
    }

    [Fact]
    public void TheShownTime_IsTheBestOfTheLastTenEchoes()
    {
        var probe = Probe();
        Echo(probe, 1, true, 20);

        Echo(probe, 10, true, 60);

        Assert.Equal(60, probe.Now.RttMs);
        Assert.Equal(20, probe.RttMs);
    }

    [Fact]
    public void AfterAnOutage_TheShownTimeIsThatOfTheLinkThatCameBack()
    {
        var probe = Probe();
        Echo(probe, 4, true, 20);
        Echo(probe, 3, false);

        Echo(probe, LinkLossProbe.BackEchoes, true, 60);

        Assert.Equal(60, probe.Now.RttMs);
        Assert.Equal(20, probe.RttMs);
    }

    [Fact]
    public void AtThePaceOfALinkNobodyWatches_TheShownShareIsThatOfTheMinute()
    {
        var probe = Probe();

        Echo(probe, 6, true, 40, LinkLossProbe.IntervalMs);
        Echo(probe, 1, false, 40, LinkLossProbe.IntervalMs);
        Echo(probe, 5, true, 40, LinkLossProbe.IntervalMs);

        Assert.Equal(8, probe.Percent);
        Assert.Equal(8, probe.Now.LossPercent);
    }

    [Fact]
    public void AStoppedTunnel_LeavesNothingOnTheScreen()
    {
        var probe = Probe();
        Echo(probe, 6, true);
        Echo(probe, 3, false);

        probe.Reset();

        Assert.Equal(LinkNow.Unknown, probe.Now);
    }

    [Fact]
    public void AnOutageOfTheSessionBefore_IsNotCarriedIntoTheNextOne()
    {
        var probe = Probe();
        Echo(probe, 6, true);
        Echo(probe, 3, false);
        probe.Reset();

        Echo(probe, LinkLossProbe.BackEchoes, true);

        Assert.Equal(LinkHealth.LossUnknown, probe.Now.LossPercent);
    }

    [Fact]
    public async Task ALinkTheScreenShows_IsEchoedEverySecondForAsLongAsItIsLookedAt()
    {
        var sent = new List<long>();
        using var stop = new CancellationTokenSource();
        var probe = default(LinkLossProbe);
        probe = new LinkLossProbe(
            ["10.0.0.1"],
            clock: () => _now,
            echo: (_, _, _) =>
            {
                sent.Add(_now);
                if (sent.Count == 3)
                {
                    probe!.Watch();
                }

                if (sent.Count == 10)
                {
                    stop.Cancel();
                }

                return Task.FromResult(40);
            },
            rest: (pause, ct) =>
            {
                ct.ThrowIfCancellationRequested();
                _now += pause;
                return Task.CompletedTask;
            });

        await probe.RunAsync(stop.Token);

        Assert.Equal([1000, 6000, 11000, 12000, 13000, 14000, 15000, 20000, 25000, 30000], sent);
    }

    [Fact]
    public void ALook_KeepsTheLinkWatchedForAWhileAndNoLonger()
    {
        var probe = Probe();
        Assert.False(probe.Watched);

        probe.Watch();
        _now += LinkLossProbe.WatchMs - 1;
        Assert.True(probe.Watched);

        _now += 1;
        Assert.False(probe.Watched);
    }

    [Fact]
    public async Task ALookAtTheLink_CutsALongPauseShort()
    {
        var first = new TaskCompletionSource();
        var second = new TaskCompletionSource();
        var echoes = 0;
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var probe = new LinkLossProbe(["10.0.0.1"], intervalMs: 60_000, echo: (_, _, _) =>
        {
            (Interlocked.Increment(ref echoes) == 1 ? first : second).TrySetResult();
            return Task.FromResult(40);
        });
        var run = probe.RunAsync(stop.Token);
        await first.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await Task.Delay(100);

        probe.Watch();
        var woken = await Task.WhenAny(second.Task, Task.Delay(TimeSpan.FromSeconds(10))) == second.Task;
        stop.Cancel();
        await run;

        Assert.True(woken);
    }

    [Fact]
    public void TheReadingOfATunnel_SurvivesItsProtocolRow()
    {
        var row = new LinkNow(7, 43, 2).ToRow("home");

        Assert.Equal(("home", new LinkNow(7, 43, 2)), LinkNow.TryParse(row));
    }

    [Fact]
    public void AReadingNothingWasMeasuredFor_SurvivesItsRowToo()
    {
        var row = LinkNow.Unknown.ToRow("home");

        Assert.Equal(("home", LinkNow.Unknown), LinkNow.TryParse(row));
    }

    [Fact]
    public void ARowOfAnotherKind_IsNoReading()
    {
        Assert.Null(LinkNow.TryParse("srv\thome\tok\trtt=40"));
        Assert.Null(LinkNow.TryParse(string.Empty));
    }

    [Fact]
    public void TheMeter_CarriesWhatTheScreenIsShown()
    {
        var shown = new LinkNow(3, 40, 1);

        var reading = new LinkMeter().Sample(0, 0, 0, shown: shown);

        Assert.Equal(shown, reading.Now);
    }

    [Fact]
    public void AReadingTakenWithoutAProbe_ShowsNothing()
    {
        Assert.Equal(LinkNow.Unknown, new LinkMeter().Sample(0, 0, 0).Now);
        Assert.Equal(LinkNow.Unknown, LinkReading.Empty.Now);
    }

    [Fact]
    public void WhatTheScreenIsShown_IsWorthASnapshotWhenItMoves()
    {
        var clean = new LinkReading(1000, 1000, 0, 0, 40) { Now = new LinkNow(0, 40, 0) };

        Assert.True(clean.DiffersFrom(clean with { Now = new LinkNow(12, 40, 0) }));
        Assert.True(clean.DiffersFrom(clean with { Now = new LinkNow(0, 90, 0) }));
        Assert.False(clean.DiffersFrom(clean with { Now = new LinkNow(0, 42, 0) }));
    }

    private LinkLossProbe Probe()
    {
        return new LinkLossProbe(["10.0.0.1"], clock: () => _now);
    }

    // One echo per pause, the clock moving with them and waiting out the ones that never come back; the pace of a
    // watched link unless another is given.
    private void Echo(LinkLossProbe probe, int times, bool answered, int rttMs = 40, int paceMs = LinkLossProbe.WatchedIntervalMs)
    {
        for (var i = 0; i < times; i++)
        {
            _now += paceMs + (answered ? 0 : TimeoutMs);
            probe.Record(answered ? rttMs : -1);
        }
    }
}
