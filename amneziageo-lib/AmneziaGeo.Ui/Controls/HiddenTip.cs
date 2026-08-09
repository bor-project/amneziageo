using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;

namespace AmneziaGeo.Ui.Controls;

/// <summary>
/// Подсказки кнопок, которые пропадают или уходят под строку: указатель их не покидает, поэтому подсказку
/// снимает сам контрол.
/// </summary>
internal static class HiddenTip
{
    /// <summary>
    /// Следит за кнопками: скрытая кнопка закрывает свою подсказку.
    /// </summary>
    public static void Watch(IEnumerable<Control> controls)
    {
        foreach (var control in controls)
        {
            control.PropertyChanged += OnControlPropertyChanged;
        }
    }

    /// <summary>
    /// Закрывает подсказки перечисленных контролов.
    /// </summary>
    public static void Drop(IEnumerable<Control> controls)
    {
        foreach (var control in controls)
        {
            ToolTip.SetIsOpen(control, false);
        }
    }

    private static void OnControlPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == Visual.IsVisibleProperty && sender is Control control && !control.IsVisible)
        {
            ToolTip.SetIsOpen(control, false);
        }
    }
}
