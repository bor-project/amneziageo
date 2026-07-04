using System;
using System.Globalization;
using Avalonia.Data.Converters;
using AmneziaGeo.Localization;

namespace AmneziaGeo.Windows.Ui.Localization;

/// <summary>
/// Formats an hour count with a localized unit suffix.
/// </summary>
public sealed class HoursConverter : IValueConverter
{
    /// <summary>
    /// Shared instance for XAML binding.
    /// </summary>
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
