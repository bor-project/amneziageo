using System.Globalization;
using AmneziaGeo.Ui.Services;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Xunit;

namespace AmneziaGeo.Tests;

/// <summary>
/// The action of a notice banner stands after the text in the wide layout and under it, at the right edge, in the
/// narrow one.
/// </summary>
public sealed class NoticeRowTests
{
    private const double Width = 300;

    [Fact]
    public void InTheNarrowLayout_TheActionStandsUnderTheText()
    {
        var banner = new Banner(compact: true);

        banner.Lay(Width);

        Assert.Equal(banner.Text.Bounds.Bottom + 6, banner.Action.Bounds.Y);
        Assert.Equal(Width, banner.Action.Bounds.Right);
        Assert.Equal(banner.Close.Bounds.X, banner.Text.Bounds.Right);
    }

    [Fact]
    public void InTheWideLayout_TheActionStandsAfterTheText()
    {
        var banner = new Banner(compact: false);

        banner.Lay(Width);

        Assert.Equal(banner.Text.Bounds.Right + 10, banner.Action.Bounds.X);
        Assert.Equal(banner.Close.Bounds.X, banner.Action.Bounds.Right);
        Assert.True(banner.Action.Bounds.Bottom <= banner.Text.Bounds.Bottom);
        Assert.Equal(30, banner.Root.DesiredSize.Height);
    }

    // The grid of a banner as the markup lays it: the mark, the text, the action and the close button.
    private sealed class Banner
    {
        /// <summary>
        /// ctor
        /// </summary>
        public Banner(bool compact)
        {
            Grid.SetColumn(Text, 1);
            Grid.SetColumn(Close, 3);
            Grid.SetRow(Action, Laid<int>(compact, "row"));
            Grid.SetColumn(Action, Laid<int>(compact, "col2"));
            Grid.SetColumnSpan(Action, Laid<int>(compact, "span4"));
            Action.Margin = Laid<Thickness>(compact, "gapTop6OrLeft10");
            Root.Children.AddRange([new Border { Width = 17, Height = 8 }, Text, Action, Close]);
        }

        public Grid Root { get; } = new()
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,Auto"),
            RowDefinitions = new RowDefinitions("Auto,Auto"),
        };

        public Border Text { get; } = new() { Height = 30 };

        public Border Action { get; } = new()
        {
            Width = 80,
            Height = 16,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
        };

        public Border Close { get; } = new() { Width = 20, Height = 11, VerticalAlignment = VerticalAlignment.Top };

        public void Lay(double width)
        {
            Root.Measure(new Size(width, 200));
            Root.Arrange(new Rect(0, 0, width, Root.DesiredSize.Height));
        }

        // The value the markup binds for the layout.
        private static T Laid<T>(bool compact, string parameter) =>
            (T)CompactConverter.Instance.Convert(compact, typeof(T), parameter, CultureInfo.InvariantCulture)!;
    }
}
