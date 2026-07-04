using System;
using System.Collections.Generic;
using System.Globalization;
using Avalonia.Data.Converters;

namespace AmneziaGeo.Windows.Ui.Services;

/// <summary>
/// Multi-value converter that returns true when the two bound values are equal.
/// </summary>
internal sealed class EqualityConverter : IMultiValueConverter
{
    /// <summary>
    /// Shared instance for XAML.
    /// </summary>
    public static readonly EqualityConverter Instance = new();

    /// <inheritdoc/>
    public object Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture)
    {
        if (values.Count < 2)
        {
            return false;
        }

        return Equals(values[0], values[1]);
    }
}
