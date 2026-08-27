using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;

namespace AmneziaGeo.Ui.Controls;

/// <summary>
/// Жесты, общие для карточек каталога.
/// </summary>
internal static class CardGesture
{
    /// <summary>
    /// Двойной щелчок левой кнопкой по телу карточки: открывает её настройки. Нажатие на контрол карточки
    /// жест не берёт - там щелчок принадлежит самому контролу.
    /// </summary>
    public static bool OpensSettings(Button body, PointerPressedEventArgs e)
    {
        return e.ClickCount == 2
            && e.Pointer.Type == PointerType.Mouse
            && e.GetCurrentPoint(body).Properties.PointerUpdateKind == PointerUpdateKind.LeftButtonPressed
            && e.Source is Visual source
            && ReferenceEquals(source.FindAncestorOfType<Button>(includeSelf: true), body);
    }

    /// <summary>
    /// Шаг карточки по каталогу: поперёк на один, вдоль на строку.
    /// </summary>
    public static int Step(Key key, int columns)
    {
        return key switch
        {
            Key.Left => -1,
            Key.Right => 1,
            Key.Up => -columns,
            Key.Down => columns,
            _ => 0,
        };
    }
}
