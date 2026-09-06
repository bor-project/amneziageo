using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;

namespace AmneziaGeo.Ui.Controls;

/// <summary>
/// Слой заголовка над всей строкой шапки: содержимое стоит по центру окна, а когда крайние колонки не
/// оставляют ему середины, сдвигается в свободное место.
/// </summary>
internal sealed class HeaderCenter : Panel
{
    // Место крайних колонок на прошлой раскладке.
    private double _left = -1;

    private double _right = -1;

    public static readonly StyledProperty<double> GapProperty =
        AvaloniaProperty.Register<HeaderCenter, double>(nameof(Gap), 8d);

    /// <summary>
    /// ctor
    /// </summary>
    static HeaderCenter()
    {
        AffectsArrange<HeaderCenter>(GapProperty);
    }

    /// <summary>
    /// Зазор между содержимым и крайними колонками.
    /// </summary>
    public double Gap
    {
        get => GetValue(GapProperty);
        set => SetValue(GapProperty, value);
    }

    /// <summary>
    /// Меряет содержимое по месту, свободному от крайних колонок.
    /// </summary>
    protected override Size MeasureOverride(Size availableSize)
    {
        var room = Room(availableSize.Width);
        var offered = new Size(room, availableSize.Height);
        var width = 0d;
        var height = 0d;
        foreach (var child in Children)
        {
            child.Measure(offered);
            width = Math.Max(width, child.DesiredSize.Width);
            height = Math.Max(height, child.DesiredSize.Height);
        }

        return new Size(width, height);
    }

    /// <summary>
    /// Ставит содержимое по центру строки и зажимает его между крайними колонками.
    /// </summary>
    protected override Size ArrangeOverride(Size finalSize)
    {
        var left = Inset(0);
        var right = Inset(1);
        if (Math.Abs(left - _left) > 0.5 || Math.Abs(right - _right) > 0.5)
        {
            _left = left;
            _right = right;
            Dispatcher.UIThread.Post(InvalidateMeasure, DispatcherPriority.Render);
        }

        var room = Math.Max(0, finalSize.Width - left - right);
        foreach (var child in Children)
        {
            var width = Math.Min(child.DesiredSize.Width, room);
            var centred = (finalSize.Width - width) / 2;
            child.Arrange(new Rect(Math.Clamp(centred, left, left + room - width), 0, width, finalSize.Height));
        }

        return finalSize;
    }

    // Ширина, свободная от крайних колонок.
    private double Room(double width)
    {
        if (double.IsInfinity(width))
        {
            return width;
        }

        return Math.Max(0, width - Inset(0) - Inset(1));
    }

    // Место, занятое крайней колонкой: 0 - левой, 1 - правой.
    private double Inset(int side)
    {
        if (Parent is not Grid grid || grid.ColumnDefinitions.Count < 2)
        {
            return 0;
        }

        var column = side == 0 ? grid.ColumnDefinitions[0] : grid.ColumnDefinitions[^1];
        return column.ActualWidth + Gap;
    }
}
