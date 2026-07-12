using System;
using System.Globalization;
using Avalonia;
using Avalonia.Data.Converters;

namespace AmneziaGeo.Windows.Ui.Services;

/// <summary>
/// Half-gutter between two reflowing cards: a right/bottom gap on the first, a left/top gap on the second,
/// switched by the current column count so the gap follows the axis (horizontal at two columns, vertical at one).
/// Parameter "first" or "second" selects the card.
/// </summary>
internal sealed class GutterMarginConverter : IValueConverter
{
    /// <summary>
    /// Shared instance for XAML binding.
    /// </summary>
    public static readonly GutterMarginConverter Instance = new();

    private const double Gap = 7;

    /// <inheritdoc/>
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var twoColumns = value is int columns && columns >= 2;
        var second = string.Equals(parameter?.ToString(), "second", StringComparison.Ordinal);

        return twoColumns
            ? (second ? new Thickness(Gap, 0, 0, 0) : new Thickness(0, 0, Gap, 0))
            : (second ? new Thickness(0, Gap, 0, 0) : new Thickness(0, 0, 0, Gap));
    }

    /// <inheritdoc/>
    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return Avalonia.Data.BindingOperations.DoNothing;
    }
}
