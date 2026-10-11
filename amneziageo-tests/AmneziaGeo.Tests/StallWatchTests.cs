using AmneziaGeo.Ui.Services;

using Xunit;

namespace AmneziaGeo.Tests;

/// <summary>
/// The window watches itself for three seconds after an input and names the pauses in which it did not answer.
/// </summary>
public sealed class StallWatchTests
{
    [Fact]
    public void AnInputTheInterfaceAnswersAtOnce_SaysNothing()
    {
        var bench = new Bench();

        bench.Watch.Input("a tap");
        bench.Idle(5000);

        Assert.Empty(bench.Said);
    }

    [Fact]
    public void AnInputThatHoldsTheInterface_IsNamedWithItsPlacesAndItsTime()
    {
        var bench = new Bench { Place = "settings/config" };

        bench.Watch.Input("a tap");
        bench.Place = "settings/routing";
        bench.Busy(1840);
        bench.Idle(6000);

        Assert.Equal(["window: the interface was busy for 1840 ms after a tap from settings/config to settings/routing"], bench.Said);
    }

    [Fact]
    public void APauseThatComesLater_IsCountedWithoutTheStepItWasAskedAt()
    {
        var bench = new Bench { Place = "settings/logs" };

        bench.Watch.Input("a key press");
        bench.Idle(1000);
        bench.Busy(700);
        bench.Idle(6000);

        Assert.Equal(["window: the interface was busy for 650 ms after a key press on settings/logs"], bench.Said);
    }

    [Fact]
    public void SeveralPauses_GiveTheLongestAndTheirNumber()
    {
        var bench = new Bench { Place = "settings/routing" };

        bench.Watch.Input("a tap");
        bench.Busy(400);
        bench.Idle(1000);
        bench.Busy(950);
        bench.Idle(6000);

        Assert.Equal(["window: the interface was busy for 900 ms after a tap on settings/routing, pauses in 3 s: 2"], bench.Said);
    }

    [Fact]
    public void APauseBelowTheMark_IsNotOne()
    {
        var bench = new Bench();

        bench.Watch.Input("a tap");
        bench.Busy(StallWatch.SlowMs - StallWatch.BeatMs);
        bench.Idle(1000);
        bench.Busy(StallWatch.SlowMs + StallWatch.BeatMs - 1);
        bench.Idle(6000);

        Assert.Empty(bench.Said);
    }

    [Fact]
    public void AnInputWhileAWatchIsOn_StartsNoSecondOne()
    {
        var bench = new Bench();

        bench.Watch.Input("a tap");
        bench.Idle(100);
        bench.Watch.Input("a tap");
        bench.Busy(600);
        bench.Idle(6000);

        Assert.Single(bench.Said);
        Assert.Equal(0, bench.Waiting);
    }

    [Fact]
    public void AfterAWatchEnded_TheNextInputIsWatchedAgain()
    {
        var bench = new Bench { Place = "home" };
        bench.Watch.Input("a tap");
        bench.Idle(5000);

        bench.Watch.Input("a key press");
        bench.Busy(300);
        bench.Idle(10000);

        Assert.Equal(["window: the interface was busy for 300 ms after a key press on home"], bench.Said);
    }

    [Fact]
    public void AWatch_AsksTheInterfaceNoLongerThanItsThreeSeconds()
    {
        var bench = new Bench();

        bench.Watch.Input("a tap");
        bench.Idle(60000);

        Assert.Equal(0, bench.Waiting);
        Assert.InRange(bench.Asked, StallWatch.WatchMs / StallWatch.BeatMs, StallWatch.WatchMs / StallWatch.BeatMs + 2);
    }

    // The interface thread of the test: what the watch asks to be run later runs when the thread is free.
    private sealed class Bench
    {
        private readonly List<(Action Run, long Due)> _queue = [];
        private long _now;

        /// <summary>
        /// ctor
        /// </summary>
        public Bench()
        {
            Watch = new StallWatch(() => _now, (run, delay) => _queue.Add((run, _now + delay)), () => Place, Said.Add);
        }

        public StallWatch Watch { get; }

        public string Place { get; set; } = "home";

        public List<string> Said { get; } = [];

        public int Waiting => _queue.Count;

        public int Asked { get; private set; }

        public void Busy(long ms)
        {
            _now += ms;
        }

        public void Idle(long until)
        {
            while (_queue.Count > 0)
            {
                var next = _queue.MinBy(item => item.Due);
                if (next.Due > until)
                {
                    break;
                }

                _queue.Remove(next);
                _now = Math.Max(_now, next.Due);
                Asked++;
                next.Run();
            }

            _now = Math.Max(_now, until);
        }
    }
}
