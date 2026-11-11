using AmneziaGeo.Localization;
using AmneziaGeo.Ui.Services;
using AmneziaGeo.Ui.ViewModels;

namespace AmneziaGeo.Ui.Controls;

/// <summary>
/// Диалог перемещения карточки.
/// </summary>
internal static class CardMenu
{
    public static void Present(ActionSheetViewModel? sheet, string title, Action reorder)
    {
        sheet?.Show(title, string.Empty,
        [
            new ActionOption(Loc.Instance.Get("Main_CardReorder"), Glyphs.Reorder, reorder),
        ]);
    }
}
