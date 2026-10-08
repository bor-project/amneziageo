using System;
using Avalonia;
using Avalonia.Controls;

namespace AmneziaGeo.Ui.Controls;

/// <summary>
/// Строка сведений: первый ребёнок слева, последний у правого края; когда в одну строку они не помещаются,
/// последний уходит на строку ниже, тоже к правому краю.
/// </summary>
internal sealed class TailWrapRow : Panel
{
    public static readonly StyledProperty<double> SpacingProperty =
        AvaloniaProperty.Register<TailWrapRow, double>(nameof(Spacing), 10d);

    // Запас на округление ширины при измерении.
    private const double Epsilon = 0.5;

    // Ушёл ли последний ребёнок на строку ниже на прошлом замере.
    private bool _wrapped;

    /// <summary>
    /// ctor
    /// </summary>
    static TailWrapRow()
    {
        AffectsMeasure<TailWrapRow>(SpacingProperty);
    }

    /// <summary>
    /// Промежуток между детьми в одной строке.
    /// </summary>
    public double Spacing
    {
        get => GetValue(SpacingProperty);
        set => SetValue(SpacingProperty, value);
    }

    /// <summary>
    /// Меряет детей без ограничения по ширине; когда вместе они шире строки, меряет каждого на свою строку.
    /// </summary>
    protected override Size MeasureOverride(Size availableSize)
    {
        _wrapped = false;
        if (Head is not { } head)
        {
            return default;
        }

        var free = new Size(double.PositiveInfinity, availableSize.Height);
        head.Measure(free);
        if (Tail is not { } tail)
        {
            return head.DesiredSize;
        }

        tail.Measure(free);
        var width = head.DesiredSize.Width + Spacing + tail.DesiredSize.Width;
        _wrapped = !double.IsInfinity(availableSize.Width) && width > availableSize.Width + Epsilon;
        if (!_wrapped)
        {
            return new Size(width, Math.Max(head.DesiredSize.Height, tail.DesiredSize.Height));
        }

        head.Measure(availableSize);
        tail.Measure(availableSize);

        return new Size(Math.Max(head.DesiredSize.Width, tail.DesiredSize.Width), Line(head) + Line(tail));
    }

    /// <summary>
    /// Ставит последнего ребёнка к правому краю: в строку первого или на строку под ним.
    /// </summary>
    protected override Size ArrangeOverride(Size finalSize)
    {
        if (Head is not { } head)
        {
            return finalSize;
        }

        if (Tail is not { } tail)
        {
            head.Arrange(new Rect(finalSize));

            return finalSize;
        }

        var width = Math.Min(tail.DesiredSize.Width, finalSize.Width);
        if (!_wrapped)
        {
            head.Arrange(new Rect(0, 0, Math.Min(head.DesiredSize.Width, finalSize.Width), finalSize.Height));
            tail.Arrange(new Rect(finalSize.Width - width, 0, width, finalSize.Height));

            return finalSize;
        }

        var top = Math.Min(Line(head), finalSize.Height);
        head.Arrange(new Rect(0, 0, finalSize.Width, top));
        tail.Arrange(new Rect(finalSize.Width - width, top, width, finalSize.Height - top));

        return finalSize;
    }

    // Первый ребёнок строки.
    private Control? Head => Children.Count > 0 ? Children[0] : null;

    // Последний ребёнок строки, когда он не единственный и виден.
    private Control? Tail => Children.Count > 1 && Children[Children.Count - 1].IsVisible ? Children[Children.Count - 1] : null;

    // Высота строки ребёнка: не ниже наименьшей высоты панели.
    private double Line(Control child) => Math.Max(child.DesiredSize.Height, MinHeight);
}
