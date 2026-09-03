using System;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Data.Converters;
using Avalonia.Layout;

namespace AmneziaGeo.Ui.Services;

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
            "row2" => compact ? 2 : 0,
            "col" => compact ? 0 : 1,
            "col2" => compact ? 0 : 2,
            "span2" => compact ? 2 : 1,
            "span3" => compact ? 3 : 1,
            "alignRL" => compact ? HorizontalAlignment.Left : HorizontalAlignment.Right,
            "alignLC" => compact ? HorizontalAlignment.Center : HorizontalAlignment.Left,
            "stretchL" => compact ? HorizontalAlignment.Stretch : HorizontalAlignment.Left,
            "stretchR" => compact ? HorizontalAlignment.Stretch : HorizontalAlignment.Right,
            "stretchC" => compact ? HorizontalAlignment.Stretch : HorizontalAlignment.Center,
            "w100" => compact ? double.NaN : 100d,
            "w110" => compact ? double.NaN : 110d,
            "w130" => compact ? double.NaN : 130d,
            "w160" => compact ? double.NaN : 160d,
            "w170" => compact ? double.NaN : 170d,
            "w180" => compact ? double.NaN : 180d,
            "w240" => compact ? double.NaN : 240d,
            "w290" => compact ? double.NaN : 290d,
            "w340" => compact ? double.NaN : 340d,
            "w300" => compact ? double.NaN : 300d,
            // Width floors that lift in compact so a narrow card cannot be overflowed.
            "minW130" => compact ? 0d : 130d,
            "minW220" => compact ? 0d : 220d,
            // Width caps that lift in compact so the segment track / catalogue combo fills the row.
            "maxW260" => compact ? double.PositiveInfinity : 260d,
            "maxW480" => compact ? double.PositiveInfinity : 480d,
            // Insets a narrow screen gives back sideways; the vertical ones keep the section off the pane edges.
            "paneInset" => compact ? new Thickness(10, 12, 10, 12) : new Thickness(18, 12, 18, 12),
            "shellHome" => compact ? new Thickness(6, 3, 6, 3) : new Thickness(12, 3, 12, 3),
            "shellHead" => compact ? new Thickness(6, 3, 6, 0) : new Thickness(12, 3, 12, 0),
            "shellBody" => compact ? new Thickness(6, 1, 6, 3) : new Thickness(12, 1, 12, 3),
            // Top gap under the tabs when the catalogue combo drops to its own row in compact.
            "gapTop14" => compact ? new Thickness(0, 14, 0, 0) : new Thickness(0),
            // Подпись отходит от своей строки только на большом экране: узкому окну эта высота дорога.
            "gap6" => compact ? 0d : 6d,
            "inputMargin" => compact ? new Thickness(0) : new Thickness(0, 0, 8, 0),
            // Inset of a list inside its frame; without the frame the rows stand at the edge.
            "boxPad" => compact ? new Thickness(0) : new Thickness(8),
            // Frame of a section card; a narrow screen keeps the section and drops the frame around it.
            "cardEdge" => compact ? new Thickness(0) : new Thickness(1),
            "cardRadius" => compact ? new CornerRadius(0) : new CornerRadius(12),
            "cardPad" => compact ? new Thickness(0) : new Thickness(16),
            // Gap between side-by-side field blocks; it drops when they stack in compact.
            "fieldGap" => compact ? new Thickness(0, 0, 0, 10) : new Thickness(0, 0, 12, 10),
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
