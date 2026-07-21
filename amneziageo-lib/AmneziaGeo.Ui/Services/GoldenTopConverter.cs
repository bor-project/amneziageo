using System;
using System.Globalization;
using Avalonia;
using Avalonia.Data;
using Avalonia.Data.Converters;

namespace AmneziaGeo.Ui.Services;

/// <summary>
/// Maps a bound height to a top margin that seats an element's centre on the golden section, so the home
/// control sits in the upper golden ratio instead of the exact middle. The parameter is the element's
/// half-height, subtracted so its centre - not its top - lands on the line.
/// </summary>
internal sealed class GoldenTopConverter : IValueConverter
{
    /// <summary>
    /// Shared instance for XAML binding.
    /// </summary>
    public static readonly GoldenTopConverter Instance = new();

    // 1 - 1/phi: the minor golden section from the top.
    private const double GoldenMinor = 0.38196601125010515;

    /// <inheritdoc/>
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is double height && height > 0)
        {
            var half = parameter is not null
                && double.TryParse(parameter.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var p)
                ? p
                : 0;
            return new Thickness(0, Math.Max(0, height * GoldenMinor - half), 0, 0);
        }

        return new Thickness(0);
    }

    /// <inheritdoc/>
    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return BindingOperations.DoNothing;
    }
}
