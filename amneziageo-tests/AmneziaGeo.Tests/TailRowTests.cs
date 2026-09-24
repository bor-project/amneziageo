using AmneziaGeo.Ui.Controls;
using Avalonia;
using Avalonia.Controls;
using Xunit;

namespace AmneziaGeo.Tests;

public sealed class TailRowTests
{
    [Fact]
    public void TheTail_FollowsTheRow()
    {
        var tail = new Border { Width = 40, Height = 10 };
        var row = Row(tail);

        Lay(row, 200);

        Assert.True(tail.IsVisible);
        Assert.Equal(100, tail.Bounds.X);
    }

    [Fact]
    public void ATailThatDoesNotFitWhole_Hides()
    {
        var tail = new Border { Width = 40, Height = 10 };
        var row = Row(tail);

        Lay(row, 120);

        Assert.False(tail.IsVisible);
        Assert.Equal(10, row.DesiredSize.Height);
    }

    [Fact]
    public void AHiddenTail_ComesBackWhenTheRowWidens()
    {
        var tail = new Border { Width = 40, Height = 10 };
        var row = Row(tail);

        Lay(row, 120);
        Lay(row, 140);

        Assert.True(tail.IsVisible);
        Assert.Equal(100, tail.Bounds.X);
    }

    private static TailRow Row(Control tail)
    {
        return new TailRow { Children = { new Border { Width = 100, Height = 8 }, tail } };
    }

    // Меряет и ставит строку дважды: смена видимости хвоста просит ещё один проход.
    private static void Lay(TailRow row, double width)
    {
        for (var pass = 0; pass < 2; pass++)
        {
            row.Measure(new Size(width, 20));
            row.Arrange(new Rect(0, 0, width, 20));
        }
    }
}
