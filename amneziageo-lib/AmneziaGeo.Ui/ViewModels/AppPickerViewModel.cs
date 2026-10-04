using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AmneziaGeo.Ui.ViewModels;

/// <summary>
/// Окно выбора приложений: строки, поиск и показ системных программ.
/// </summary>
public sealed partial class AppPickerViewModel : ObservableObject
{
    private readonly List<AppPickerRow> _rows = [];

    /// <summary>
    /// Строки, которые видны сейчас.
    /// </summary>
    public ObservableCollection<AppPickerRow> Shown { get; } = [];

    /// <summary>
    /// Текст поиска.
    /// </summary>
    [ObservableProperty]
    private string _query = string.Empty;

    /// <summary>
    /// Показаны ли системные программы без значка запуска.
    /// </summary>
    [ObservableProperty]
    private bool _showSystem;

    /// <summary>
    /// Есть ли среди программ системные без значка запуска.
    /// </summary>
    [ObservableProperty]
    private bool _hasSystem;

    /// <summary>
    /// Пакеты всех строк окна.
    /// </summary>
    public IReadOnlyList<string> Offered => [.. _rows.Select(row => row.Package)];

    /// <summary>
    /// Пакеты отмеченных строк.
    /// </summary>
    public IReadOnlyList<string> Picked => [.. _rows.Where(row => row.Picked).Select(row => row.Package)];

    /// <summary>
    /// Кладёт программы в окно.
    /// </summary>
    public void Load(IEnumerable<AppPickerRow> rows)
    {
        _rows.Clear();
        _rows.AddRange(rows);
        HasSystem = _rows.Any(row => row.IsSystem);
        Refill();
    }

    partial void OnQueryChanged(string value) => Refill();

    partial void OnShowSystemChanged(bool value) => Refill();

    // Собирает видимые строки заново.
    private void Refill()
    {
        var query = Query.Trim().ToLowerInvariant();
        Shown.Clear();
        foreach (var row in _rows.Where(row => Shows(row, query)))
        {
            Shown.Add(row);
        }
    }

    // Видна ли строка при этом поиске.
    private bool Shows(AppPickerRow row, string query)
    {
        if (query.Length > 0)
        {
            return row.Haystack.Contains(query, StringComparison.Ordinal);
        }

        return ShowSystem || !row.IsSystem || row.Picked;
    }
}
