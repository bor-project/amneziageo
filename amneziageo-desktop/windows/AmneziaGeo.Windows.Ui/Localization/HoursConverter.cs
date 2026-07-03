using System;
using System.Globalization;
using Avalonia.Data.Converters;
using AmneziaGeo.Localization;

namespace AmneziaGeo.Windows.Ui.Localization;

/// <summary>
/// Formats an hour count for the geo settings combos as "{n} {localized-hours-unit}" (e.g. "24 h" / "24 ч").
/// Used from the two interval/validity combo item templates via {x:Static l:HoursConverter.Instance}. The
/// suffix follows the current UI language; a live language switch is reflected the next time the item renders.
/// </summary>
public sealed class HoursConverter : IValueConverter
{
    /// <summary>Shared instance for XAML binding (no need for a Resources declaration).</summary>
    public static readonly HoursConverter Instance = new();

    /// <inheritdoc/>
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var n = value?.ToString() ?? string.Empty;
        return $"{n} {Loc.Instance.Get("Unit_Hours")}";
    }

    /// <inheritdoc/>
    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return Avalonia.Data.BindingOperations.DoNothing;
    }
}
