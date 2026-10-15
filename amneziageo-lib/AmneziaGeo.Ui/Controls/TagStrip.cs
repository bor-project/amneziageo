using Avalonia;
using Avalonia.Controls;

namespace AmneziaGeo.Ui.Controls;

/// <summary>
/// Ряд плашек: что не влезло по ширине, переносится на следующую строку.
/// </summary>
internal sealed class TagStrip : Panel
{
    public static readonly StyledProperty<double> SpacingProperty =
        AvaloniaProperty.Register<TagStrip, double>(nameof(Spacing), 6d);

    public static readonly StyledProperty<double> RowSpacingProperty =
        AvaloniaProperty.Register<TagStrip, double>(nameof(RowSpacing), 4d);

    /// <summary>
    /// ctor
    /// </summary>
    static TagStrip()
    {
        AffectsMeasure<TagStrip>(SpacingProperty, RowSpacingProperty);
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
    /// Промежуток между строками.
    /// </summary>
    public double RowSpacing
    {
        get => GetValue(RowSpacingProperty);
        set => SetValue(RowSpacingProperty, value);
    }

    /// <summary>
    /// Меряет плашки по строкам: плашка, которой не хватило места в строке, начинает следующую.
    /// </summary>
    protected override Size MeasureOverride(Size availableSize)
    {
        var widest = 0d;
        var height = 0d;
        foreach (var row in Rows(availableSize.Width, true))
        {
            widest = Math.Max(widest, row.Width);
            height += height > 0 ? RowSpacing + row.Height : row.Height;
        }

        return new Size(Math.Min(widest, availableSize.Width), height);
    }

    /// <summary>
    /// Ставит плашки по строкам; плашка шире всей строки стоит в своей, сжатая до её ширины.
    /// </summary>
    protected override Size ArrangeOverride(Size finalSize)
    {
        var y = 0d;
        foreach (var row in Rows(finalSize.Width, false))
        {
            var x = 0d;
            foreach (var child in row.Children)
            {
                child.Arrange(new Rect(x, y, child.DesiredSize.Width, row.Height));
                x += child.DesiredSize.Width + Spacing;
            }

            y += row.Height + RowSpacing;
        }

        return finalSize;
    }

    // Делит плашки на строки по ширине.
    private List<(List<Control> Children, double Width, double Height)> Rows(double width, bool measure)
    {
        var rows = new List<(List<Control> Children, double Width, double Height)>();
        var children = new List<Control>();
        var taken = 0d;
        var height = 0d;
        foreach (var child in Children)
        {
            if (measure)
            {
                child.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                if (child.DesiredSize.Width > width && !double.IsInfinity(width))
                {
                    child.Measure(new Size(width, double.PositiveInfinity));
                }
            }

            var size = child.DesiredSize;
            if (children.Count > 0 && taken + Spacing + size.Width > width + Epsilon)
            {
                rows.Add((children, taken, height));
                children = [];
                taken = 0;
                height = 0;
            }

            taken += children.Count > 0 ? Spacing + size.Width : size.Width;
            height = Math.Max(height, size.Height);
            children.Add(child);
        }

        if (children.Count > 0)
        {
            rows.Add((children, taken, height));
        }

        return rows;
    }

    // Запас на округление ширины при измерении.
    private const double Epsilon = 0.5;
}
