using AmneziaGeo.Ui.Controls;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Xunit;

namespace AmneziaGeo.Tests;

public sealed class TailWrapRowTests
{
    [Fact]
    public void ATailThatFits_StandsAtTheRightEdgeOfTheSameLine()
    {
        var tail = new Border { Width = 40, Height = 10 };
        var row = Row(tail);

        Lay(row, 200);

        Assert.Equal(200, tail.Bounds.Right);
        Assert.Equal(0, tail.Bounds.Y);
        Assert.Equal(10, row.DesiredSize.Height);
    }

    [Fact]
    public void ATailThatJustFits_StaysOnTheLine()
    {
        var tail = new Border { Width = 40, Height = 10 };
        var row = Row(tail);

        Lay(row, 150);

        Assert.Equal(150, tail.Bounds.Right);
        Assert.Equal(0, tail.Bounds.Y);
    }

    [Fact]
    public void ATailThatDoesNotFit_GoesToTheLineBelow_AtTheRightEdge()
    {
        var tail = new Border { Width = 40, Height = 10 };
        var row = Row(tail);

        Lay(row, 130);

        Assert.Equal(130, tail.Bounds.Right);
        Assert.Equal(8, tail.Bounds.Y);
        Assert.Equal(18, row.DesiredSize.Height);
    }

    [Fact]
    public void ATailWiderThanTheLine_TakesTheWholeLineBelow()
    {
        var tail = new Shrinking(300);
        var row = Row(tail);

        Lay(row, 130);

        Assert.Equal(0, tail.Bounds.X);
        Assert.Equal(130, tail.Bounds.Width);
        Assert.Equal(8, tail.Bounds.Y);
    }

    [Fact]
    public void ATailBelow_ComesBackWhenTheRowWidens()
    {
        var tail = new Border { Width = 40, Height = 10 };
        var row = Row(tail);

        Lay(row, 130);
        Lay(row, 200);

        Assert.Equal(200, tail.Bounds.Right);
        Assert.Equal(0, tail.Bounds.Y);
        Assert.Equal(10, row.DesiredSize.Height);
    }

    [Fact]
    public void EveryLine_KeepsTheLeastHeightOfTheRow()
    {
        var tail = new Border { Width = 40, Height = 10 };
        var row = Row(tail);
        row.MinHeight = 20;

        Lay(row, 130);

        Assert.Equal(40, row.DesiredSize.Height);
        Assert.Equal(20, tail.Bounds.Y);
    }

    [Fact]
    public void ARowWithoutATail_GivesTheHeadTheLine()
    {
        var head = Corner(new Border { Width = 100, Height = 8 });
        var row = new TailWrapRow { Children = { head } };

        Lay(row, 200);

        Assert.Equal(0, head.Bounds.X);
        Assert.Equal(8, row.DesiredSize.Height);
    }

    private static TailWrapRow Row(Control tail)
    {
        return new TailWrapRow { Spacing = 10, Children = { Corner(new Border { Width = 100, Height = 8 }), Corner(tail) } };
    }

    // Ставит содержимое в левый верхний угол отведённого места.
    private static Control Corner(Control control)
    {
        control.HorizontalAlignment = HorizontalAlignment.Left;
        control.VerticalAlignment = VerticalAlignment.Top;
        return control;
    }

    private static void Lay(TailWrapRow row, double width)
    {
        row.Measure(new Size(width, 100));
        row.Arrange(new Rect(0, 0, width, row.DesiredSize.Height));
    }

    // Содержимое, которое сжимается до отведённой ширины, как текст с многоточием.
    private sealed class Shrinking(double natural) : Control
    {
        protected override Size MeasureOverride(Size availableSize)
        {
            return new Size(Math.Min(natural, availableSize.Width), 10);
        }
    }
}
