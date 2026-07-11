using System;
using System.Globalization;
using Avalonia.Data.Converters;

namespace AmneziaGeo.Windows.Ui.Services;

/// <summary>
/// Subtracts the converter parameter from a bound double.
/// </summary>
internal sealed class SubtractConverter : IValueConverter
{
    /// <summary>
    /// Shared instance for XAML binding.
    /// </summary>
    public static readonly SubtractConverter Instance = new();

    /// <inheritdoc/>
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is double v
            && parameter is not null
            && double.TryParse(parameter.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var amount))
        {
            return Math.Max(0, v - amount);
        }

        return value;
    }

    /// <inheritdoc/>
    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return Avalonia.Data.BindingOperations.DoNothing;
    }
}
