using AmneziaGeo.Ui.Services;

using Xunit;

namespace AmneziaGeo.Tests;

/// <summary>
/// The rows of a long list are built in portions sized by how long the last one took to lay out: a quick device
/// gets larger ones, and no device gets one so small that the list takes many times longer to come.
/// </summary>
public sealed class RowPaceTests
{
    [Fact]
    public void ThePace_StartsWithItsSmallestPortion()
    {
        var bench = new Bench();

        Assert.Equal(RowPace.StartRows, bench.Pace.Rows);
        Assert.Equal(RowPace.MinRows, bench.Pace.Rows);
    }

    [Fact]
    public void APortionThatTookItsTime_KeepsItsSize()
    {
        var bench = new Bench();
        bench.Portion(1);
        var rows = bench.Pace.Rows;

        bench.Portion(RowPace.PortionMs);

        Assert.Equal(RowPace.StartRows * 2, rows);
        Assert.Equal(rows, bench.Pace.Rows);
    }

    [Fact]
    public void AQuickDevice_GetsLargerPortions_TwiceAtAStepAtMost()
    {
        var bench = new Bench();

        bench.Portion(1);

        Assert.Equal(RowPace.StartRows * 2, bench.Pace.Rows);

        bench.Portion(1);

        Assert.Equal(RowPace.StartRows * 4, bench.Pace.Rows);
    }

    [Fact]
    public void APortionThatTookTwiceItsTime_HalvesTheNextOne()
    {
        var bench = new Bench();
        bench.Portion(1);
        bench.Portion(1);

        bench.Portion(RowPace.PortionMs * 2);

        Assert.Equal(RowPace.StartRows * 2, bench.Pace.Rows);
    }

    [Fact]
    public void AHeldPortion_HalvesTheNextOneAtMost()
    {
        var bench = new Bench();
        bench.Portion(1);
        bench.Portion(1);

        bench.Portion(5000);

        Assert.Equal(RowPace.StartRows * 2, bench.Pace.Rows);
    }

    [Fact]
    public void APortion_StaysWithinItsBounds()
    {
        var quick = new Bench();
        var slow = new Bench();
        slow.Portion(1);

        for (var step = 0; step < 20; step++)
        {
            quick.Portion(0);
            slow.Portion(5000);
        }

        Assert.Equal(RowPace.MaxRows, quick.Pace.Rows);
        Assert.Equal(RowPace.MinRows, slow.Pace.Rows);
    }

    [Fact]
    public void TheLastRowsOfAList_AreSizedByWhatWasBuilt()
    {
        var bench = new Bench();
        bench.Portion(1);
        bench.Portion(1);

        bench.Portion(RowPace.PortionMs / 2, built: 20);

        Assert.Equal(40, bench.Pace.Rows);
    }

    [Fact]
    public void ALayoutWithNoPortionBehindIt_ChangesNothing()
    {
        var bench = new Bench();
        bench.Portion(1);
        var rows = bench.Pace.Rows;

        bench.Now += 900;
        bench.Pace.Laid();

        Assert.Equal(RowPace.StartRows * 2, rows);
        Assert.Equal(rows, bench.Pace.Rows);
    }

    [Fact]
    public void APortionThatBuiltNothing_ChangesNothing()
    {
        var bench = new Bench();
        bench.Portion(1);
        var rows = bench.Pace.Rows;

        bench.Portion(900, built: 0);

        Assert.Equal(RowPace.StartRows * 2, rows);
        Assert.Equal(rows, bench.Pace.Rows);
    }

    [Fact]
    public void APortion_AsksForTheRowsThePaceNames()
    {
        var bench = new Bench();
        bench.Portion(1);

        var asked = 0;
        bench.Pace.Feed(rows => asked = rows);

        Assert.Equal(RowPace.StartRows * 2, asked);
    }

    // A pace on a clock moved by hand.
    private sealed class Bench
    {
        public Bench()
        {
            Pace = new RowPace(() => Now);
        }

        public RowPace Pace { get; }

        public long Now { get; set; } = 1000;

        // Builds a portion and lays it out in the given time; unless told otherwise the portion comes out whole.
        public void Portion(int tookMs, int? built = null)
        {
            Pace.Feed(rows => built ?? rows);
            Now += tookMs;
            Pace.Laid();
        }
    }
}
