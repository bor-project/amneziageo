using System.Collections.ObjectModel;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AmneziaGeo.Ui.ViewModels;

/// <summary>
/// Один способ в наборе «Добавить» / «Экспорт»: значок, подпись и действие.
/// </summary>
internal sealed class ActionOption
{
    /// <summary>
    /// ctor
    /// </summary>
    public ActionOption(string text, Geometry icon, Action run)
    {
        Text = text;
        Icon = icon;
        Run = run;
    }

    /// <summary>
    /// Подпись строки.
    /// </summary>
    public string Text { get; }

    /// <summary>
    /// Значок строки.
    /// </summary>
    public Geometry Icon { get; }

    /// <summary>
    /// Действие строки.
    /// </summary>
    public Action Run { get; }
}

/// <summary>
/// Набор способов, показанный шторкой снизу. Живёт в оболочке, поверх всего экрана.
/// </summary>
internal sealed partial class ActionSheetViewModel : ViewModelBase
{
    [ObservableProperty]
    private bool _isOpen;

    [ObservableProperty]
    private string _title = string.Empty;

    [ObservableProperty]
    private string _subtitle = string.Empty;

    /// <summary>
    /// Строки набора.
    /// </summary>
    public ObservableCollection<ActionOption> Options { get; } = [];

    /// <summary>
    /// Выносит набор способов на экран.
    /// </summary>
    public void Show(string title, string subtitle, IEnumerable<ActionOption> options)
    {
        Title = title;
        Subtitle = subtitle;
        Options.Clear();
        foreach (var option in options)
        {
            Options.Add(option);
        }

        IsOpen = true;
    }

    /// <summary>
    /// Убирает шторку.
    /// </summary>
    [RelayCommand]
    public void Close()
    {
        IsOpen = false;
    }

    // Выполняет способ, убрав шторку: действие открывает свой экран или системный выбор файла.
    [RelayCommand]
    private void Pick(ActionOption? option)
    {
        Close();
        option?.Run();
    }
}
