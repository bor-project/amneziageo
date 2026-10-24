using System;
using Avalonia;
using Avalonia.Controls;

namespace AmneziaGeo.Ui.Controls;

/// <summary>
/// Сегменты переключателя: в ряд равными долями; когда подпись сегмента в свою долю не помещается, все сегменты
/// встают столбиком во всю ширину.
/// </summary>
internal sealed class SegmentRow : Panel
{
    // Запас на округление ширины при измерении.
    private const double Epsilon = 0.5;

    // Стояли ли сегменты столбиком на прошлом замере.
    private bool _stacked;

    /// <summary>
    /// Меряет сегменты без ограничения по ширине; когда самый широкий шире своей доли, меряет каждого во всю ширину.
    /// </summary>
    protected override Size MeasureOverride(Size availableSize)
    {
        _stacked = false;
        var segments = Segments();
        if (segments.Count == 0)
        {
            return default;
        }

        var widest = 0d;
        var tallest = 0d;
        foreach (var segment in segments)
        {
            segment.Measure(new Size(double.PositiveInfinity, availableSize.Height));
            widest = Math.Max(widest, segment.DesiredSize.Width);
            tallest = Math.Max(tallest, segment.DesiredSize.Height);
        }

        _stacked = !double.IsInfinity(availableSize.Width) && widest > availableSize.Width / segments.Count + Epsilon;
        if (!_stacked)
        {
            return new Size(widest * segments.Count, tallest);
        }

        var width = 0d;
        var height = 0d;
        foreach (var segment in segments)
        {
            segment.Measure(new Size(availableSize.Width, double.PositiveInfinity));
            width = Math.Max(width, segment.DesiredSize.Width);
            height += segment.DesiredSize.Height;
        }

        return new Size(width, height);
    }

    /// <summary>
    /// Ставит сегменты равными долями в ряд или друг под другом во всю ширину.
    /// </summary>
    protected override Size ArrangeOverride(Size finalSize)
    {
        var segments = Segments();
        if (segments.Count == 0)
        {
            return finalSize;
        }

        if (!_stacked)
        {
            var share = finalSize.Width / segments.Count;
            for (var i = 0; i < segments.Count; i++)
            {
                segments[i].Arrange(new Rect(share * i, 0, share, finalSize.Height));
            }

            return finalSize;
        }

        var top = 0d;
        foreach (var segment in segments)
        {
            segment.Arrange(new Rect(0, top, finalSize.Width, segment.DesiredSize.Height));
            top += segment.DesiredSize.Height;
        }

        return finalSize;
    }

    // Видимые сегменты по порядку.
    private List<Control> Segments() => Children.Where(child => child.IsVisible).ToList();
}
