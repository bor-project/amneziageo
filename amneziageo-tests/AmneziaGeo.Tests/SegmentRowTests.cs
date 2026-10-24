using AmneziaGeo.Ui.Controls;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Xunit;

namespace AmneziaGeo.Tests;

public sealed class SegmentRowTests
{
    [Fact]
    public void SegmentsThatFit_StandInARowOfEqualShares()
    {
        var first = Segment(60);
        var second = Segment(80);
        var row = Row(first, second);

        Lay(row, 200);

        Assert.Equal(0, first.Bounds.X);
        Assert.Equal(100, second.Bounds.X);
        Assert.Equal(0, second.Bounds.Y);
        Assert.Equal(36, row.DesiredSize.Height);
    }

    [Fact]
    public void ASegmentThatJustFitsItsShare_KeepsTheRow()
    {
        var second = Segment(100);
        var row = Row(Segment(60), second);

        Lay(row, 200);

        Assert.Equal(100, second.Bounds.X);
        Assert.Equal(0, second.Bounds.Y);
    }

    [Fact]
    public void ASegmentWiderThanItsShare_PutsTheSegmentsInAColumn()
    {
        var first = Segment(60);
        var second = Segment(120);
        var row = Row(first, second);

        Lay(row, 200);

        Assert.Equal(0, first.Bounds.Y);
        Assert.Equal(0, second.Bounds.X);
        Assert.Equal(36, second.Bounds.Y);
        Assert.Equal(72, row.DesiredSize.Height);
    }

    [Fact]
    public void ASegmentInAColumn_TakesTheWholeWidth()
    {
        var wide = new Shrinking(300);
        var row = Row(Segment(60), wide);

        Lay(row, 200);

        Assert.Equal(0, wide.Bounds.X);
        Assert.Equal(200, wide.Bounds.Width);
        Assert.Equal(36, wide.Bounds.Y);
    }

    [Fact]
    public void AColumn_ComesBackToARowWhenTheRowWidens()
    {
        var second = Segment(120);
        var row = Row(Segment(60), second);

        Lay(row, 200);
        Lay(row, 300);

        Assert.Equal(150, second.Bounds.X);
        Assert.Equal(0, second.Bounds.Y);
        Assert.Equal(36, row.DesiredSize.Height);
    }

    [Fact]
    public void AHiddenSegment_TakesNoShare()
    {
        var hidden = Segment(60);
        hidden.IsVisible = false;
        var second = Segment(80);
        var row = Row(Segment(60), hidden, second);

        Lay(row, 200);

        Assert.Equal(100, second.Bounds.X);
        Assert.Equal(0, second.Bounds.Y);
    }

    private static SegmentRow Row(params Control[] segments)
    {
        var row = new SegmentRow();
        row.Children.AddRange(segments);
        return row;
    }

    // Сегмент заданной ширины в левом верхнем углу своего места.
    private static Control Segment(double width)
    {
        return Corner(new Border { Width = width, Height = 36 });
    }

    // Ставит содержимое в левый верхний угол отведённого места.
    private static Control Corner(Control control)
    {
        control.HorizontalAlignment = HorizontalAlignment.Left;
        control.VerticalAlignment = VerticalAlignment.Top;
        return control;
    }

    private static void Lay(SegmentRow row, double width)
    {
        row.Measure(new Size(width, 100));
        row.Arrange(new Rect(0, 0, width, row.DesiredSize.Height));
    }

    // Содержимое, которое сжимается до отведённой ширины, как текст с многоточием.
    private sealed class Shrinking(double natural) : Control
    {
        protected override Size MeasureOverride(Size availableSize)
        {
            return new Size(Math.Min(natural, availableSize.Width), 36);
        }
    }
}
