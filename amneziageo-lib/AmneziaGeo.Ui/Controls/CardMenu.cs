using Avalonia.Controls;
using AmneziaGeo.Localization;
using AmneziaGeo.Ui.Services;
using AmneziaGeo.Ui.ViewModels;

namespace AmneziaGeo.Ui.Controls;

/// <summary>
/// Меню карточки каталога: открывается долгим нажатием центральной кнопки на телевизоре.
/// </summary>
internal static class CardMenu
{
    /// <summary>
    /// Выносит действия карточки на экран.
    /// </summary>
    public static void Present(Control anchor, ActionSheetViewModel? sheet, string title, Action open, Action reorder)
    {
        if (sheet is null)
        {
            return;
        }

        ActionOptions.Present(
            anchor,
            sheet,
            title,
            string.Empty,
            [
                new ActionOption(Loc.Instance.Get("Main_CardSettingsLink"), Glyphs.Gear, open),
                new ActionOption(Loc.Instance.Get("Main_CardReorder"), Glyphs.Reorder, reorder),
            ]);
    }
}
