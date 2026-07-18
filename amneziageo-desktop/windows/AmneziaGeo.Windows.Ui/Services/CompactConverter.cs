using System;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Data.Converters;
using Avalonia.Layout;

namespace AmneziaGeo.Windows.Ui.Services;

/// <summary>
/// Maps the compact-layout flag to a per-parameter layout value, so a row restacks for the narrow window.
/// </summary>
internal sealed class CompactConverter : IValueConverter
{
    /// <summary>
    /// Shared instance for XAML binding.
    /// </summary>
    public static readonly CompactConverter Instance = new();

    /// <inheritdoc/>
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var compact = value is true;
        return (parameter as string) switch
        {
            "row" => compact ? 1 : 0,
            "col" => compact ? 0 : 1,
            "col2" => compact ? 0 : 2,
            "span2" => compact ? 2 : 1,
            "span3" => compact ? 3 : 1,
            "alignRL" => compact ? HorizontalAlignment.Left : HorizontalAlignment.Right,
            "stretchL" => compact ? HorizontalAlignment.Stretch : HorizontalAlignment.Left,
            "stretchR" => compact ? HorizontalAlignment.Stretch : HorizontalAlignment.Right,
            "w100" => compact ? double.NaN : 100d,
            "w110" => compact ? double.NaN : 110d,
            "w130" => compact ? double.NaN : 130d,
            "w170" => compact ? double.NaN : 170d,
            "w180" => compact ? double.NaN : 180d,
            "inputMargin" => compact ? new Thickness(0) : new Thickness(0, 0, 8, 0),
            // Column widths for an even-split row: a fixed control becomes a star column in compact so it
            // shares the width, and the spacer/other-content column collapses.
            "colAutoStar" => compact ? new GridLength(1, GridUnitType.Star) : GridLength.Auto,
            "colStarZero" => compact ? new GridLength(0) : new GridLength(1, GridUnitType.Star),
            _ => BindingOperations.DoNothing,
        };
    }

    /// <inheritdoc/>
    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return BindingOperations.DoNothing;
    }
}
