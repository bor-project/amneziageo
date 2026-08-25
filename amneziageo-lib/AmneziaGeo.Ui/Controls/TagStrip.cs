using Avalonia;
using Avalonia.Controls;

namespace AmneziaGeo.Ui.Controls;

/// <summary>
/// Ряд плашек в строку: что не влезло по ширине, уходит за край и отсекается клипом.
/// </summary>
internal sealed class TagStrip : Panel
{
    public static readonly StyledProperty<double> SpacingProperty =
        AvaloniaProperty.Register<TagStrip, double>(nameof(Spacing), 6d);

    /// <summary>
    /// ctor
    /// </summary>
    static TagStrip()
    {
        AffectsMeasure<TagStrip>(SpacingProperty);
    }

    /// <summary>
    /// Промежуток между плашками.
    /// </summary>
    public double Spacing
    {
        get => GetValue(SpacingProperty);
        set => SetValue(SpacingProperty, value);
    }

    /// <summary>
    /// Меряет ряд по влезшим плашкам: хвост, которому ширины не хватило, места не занимает.
    /// </summary>
    protected override Size MeasureOverride(Size availableSize)
    {
        var taken = 0d;
        var fitted = 0d;
        var height = 0d;
        foreach (var child in Children)
        {
            child.Measure(new Size(double.PositiveInfinity, availableSize.Height));
            taken += taken > 0 ? Spacing + child.DesiredSize.Width : child.DesiredSize.Width;
            if (taken > availableSize.Width)
            {
                continue;
            }

            fitted = taken;
            height = Math.Max(height, child.DesiredSize.Height);
        }

        return new Size(fitted, height);
    }

    /// <summary>
    /// Ставит влезшие плашки в строку, остальные уводит за правый край.
    /// </summary>
    protected override Size ArrangeOverride(Size finalSize)
    {
        var x = 0d;
        foreach (var child in Children)
        {
            var width = child.DesiredSize.Width;
            var fits = x + width <= finalSize.Width + Epsilon;
            child.Arrange(fits
                ? new Rect(x, 0, width, finalSize.Height)
                : new Rect(finalSize.Width + Spacing, 0, width, finalSize.Height));
            x += width + Spacing;
        }

        return finalSize;
    }

    // Запас на округление ширины при измерении.
    private const double Epsilon = 0.5;
}
