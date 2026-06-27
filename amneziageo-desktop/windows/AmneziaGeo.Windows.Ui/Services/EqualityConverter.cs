using System;
using System.Collections.Generic;
using System.Globalization;
using Avalonia.Data.Converters;

namespace AmneziaGeo.Windows.Ui.Services;

/// <summary>
/// Multi-value converter that returns true when the two bound values are equal. Used to mark which
/// profile member is the default one: the row's own value is compared against the profile's
/// <c>DefaultMember</c>, so exactly one radio reads as selected and re-evaluates when the default moves.
/// </summary>
internal sealed class EqualityConverter : IMultiValueConverter
{
    /// <summary>Shared instance referenced from XAML via <c>x:Static</c>.</summary>
    public static readonly EqualityConverter Instance = new();

    /// <inheritdoc />
    public object Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture)
    {
        if (values.Count < 2)
        {
            return false;
        }

        return Equals(values[0], values[1]);
    }
}
