using Avalonia.Input;

namespace AmneziaGeo.Ui.Controls;

/// <summary>
/// Жесты, общие для карточек каталога.
/// </summary>
internal static class CardGesture
{
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
