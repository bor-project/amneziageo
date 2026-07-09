using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace AmneziaGeo.Windows.Installer;

/// <summary>
/// Bool → Visibility that reserves the element's layout slot when off (false → Hidden, not Collapsed).
/// Used for the progress percentage label so the bar above it does not shift down while the percentage
/// is hidden during indeterminate phases (#151).
/// </summary>
public sealed class BoolToVisibilityHiddenConverter : IValueConverter
{
    /// <inheritdoc/>
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? Visibility.Visible : Visibility.Hidden;

    /// <inheritdoc/>
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is Visibility.Visible;
}
