using System;
using Avalonia;
using Avalonia.Controls;

namespace AmneziaGeo.Ui.Controls;

/// <summary>
/// Строка слева направо: последний ребёнок встаёт следом за остальными, а когда целиком не помещается, прячется.
/// </summary>
internal sealed class TailRow : Panel
{
    // Запас на округление ширины при измерении.
    private const double Epsilon = 0.5;

    // Размер последнего ребёнка на замере, когда он был виден.
    private Size _tail;

    /// <summary>
    /// Меряет детей без ограничения по ширине; спрятанный хвост держит высоту прошлого замера.
    /// </summary>
    protected override Size MeasureOverride(Size availableSize)
    {
        var offered = new Size(double.PositiveInfinity, availableSize.Height);
        var width = 0d;
        var height = 0d;
        foreach (var child in Children)
        {
            child.Measure(offered);
            width += child.DesiredSize.Width;
            height = Math.Max(height, child.DesiredSize.Height);
        }

        if (Tail is { IsVisible: true } tail)
        {
            _tail = tail.DesiredSize;
        }

        return new Size(Math.Min(width, availableSize.Width), Math.Max(height, _tail.Height));
    }

    /// <summary>
    /// Ставит детей в строку, хвост показывает, только если он помещается целиком.
    /// </summary>
    protected override Size ArrangeOverride(Size finalSize)
    {
        var tail = Tail;
        var x = 0d;
        foreach (var child in Children)
        {
            if (child == tail)
            {
                continue;
            }

            child.Arrange(new Rect(x, 0, child.DesiredSize.Width, finalSize.Height));
            x += child.DesiredSize.Width;
        }

        if (tail is not null)
        {
            tail.IsVisible = x + _tail.Width <= finalSize.Width + Epsilon;
            tail.Arrange(new Rect(x, 0, _tail.Width, finalSize.Height));
        }

        return finalSize;
    }

    // Последний ребёнок строки.
    private Control? Tail => Children.Count > 0 ? Children[Children.Count - 1] : null;
}
