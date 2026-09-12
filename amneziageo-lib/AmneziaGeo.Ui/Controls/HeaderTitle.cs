using System;
using Avalonia;
using Avalonia.Controls;

namespace AmneziaGeo.Ui.Controls;

/// <summary>
/// Заголовок между крайними колонками шапки: содержимое стоит по центру, а когда целиком не помещается,
/// не показывается.
/// </summary>
internal sealed class HeaderTitle : Panel
{
    // Поместилось ли содержимое целиком на прошлом замере.
    private bool _fits = true;

    /// <summary>
    /// Меряет содержимое без ограничения по ширине и сверяет его с оставшимся местом.
    /// </summary>
    protected override Size MeasureOverride(Size availableSize)
    {
        var offered = new Size(double.PositiveInfinity, availableSize.Height);
        var width = 0d;
        var height = 0d;
        foreach (var child in Children)
        {
            child.Measure(offered);
            width = Math.Max(width, child.DesiredSize.Width);
            height = Math.Max(height, child.DesiredSize.Height);
        }

        _fits = double.IsInfinity(availableSize.Width) || width <= availableSize.Width + 0.5;
        return new Size(_fits ? width : 0, height);
    }

    /// <summary>
    /// Ставит содержимое по центру и гасит его, когда места не хватило.
    /// </summary>
    protected override Size ArrangeOverride(Size finalSize)
    {
        foreach (var child in Children)
        {
            child.Opacity = _fits ? 1 : 0;
            var width = Math.Min(child.DesiredSize.Width, finalSize.Width);
            child.Arrange(new Rect((finalSize.Width - width) / 2, 0, width, finalSize.Height));
        }

        return finalSize;
    }
}
