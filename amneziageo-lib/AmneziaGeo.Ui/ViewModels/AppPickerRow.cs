using CommunityToolkit.Mvvm.ComponentModel;

namespace AmneziaGeo.Ui.ViewModels;

/// <summary>
/// Строка окна выбора приложений.
/// </summary>
public sealed partial class AppPickerRow : ObservableObject
{
    /// <summary>
    /// ctor
    /// </summary>
    public AppPickerRow(string label, string package, bool picked)
    {
        Label = label;
        Package = package;
        Haystack = $"{label} {package}".ToLowerInvariant();
        _picked = picked;
    }

    /// <summary>
    /// Имя приложения.
    /// </summary>
    public string Label { get; }

    /// <summary>
    /// Имя пакета.
    /// </summary>
    public string Package { get; }

    /// <summary>
    /// Строка для поиска.
    /// </summary>
    public string Haystack { get; }

    /// <summary>
    /// Отмечено ли приложение.
    /// </summary>
    [ObservableProperty]
    private bool _picked;
}
