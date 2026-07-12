using System;
using System.Globalization;
using Avalonia.Data.Converters;

namespace AmneziaGeo.Windows.Ui.Services;

/// <summary>
/// Two columns when the bound width meets the parameter threshold, otherwise one; drives a responsive grid.
/// </summary>
internal sealed class WidthBreakpointConverter : IValueConverter
{
    /// <summary>
    /// Shared instance for XAML binding.
    /// </summary>
    public static readonly WidthBreakpointConverter Instance = new();

    /// <inheritdoc/>
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is double width
            && parameter is not null
            && double.TryParse(parameter.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var threshold))
        {
            return width >= threshold ? 2 : 1;
        }

        return 1;
    }

    /// <inheritdoc/>
    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return Avalonia.Data.BindingOperations.DoNothing;
    }
}
