using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using AmneziaGeo.Ui.Services;
using AmneziaGeo.Ui.ViewModels;

namespace AmneziaGeo.Ui.Controls;

/// <summary>
/// Показ набора способов: на телефоне шторкой снизу, на десктопе выпадающим списком у кнопки.
/// </summary>
internal static class ActionOptions
{
    /// <summary>
    /// Выносит набор способов на экран.
    /// </summary>
    public static void Present(
        Control? anchor,
        ActionSheetViewModel sheet,
        string title,
        string subtitle,
        IReadOnlyList<ActionOption> options)
    {
        if (options.Count == 0)
        {
            return;
        }

        if (UiPlatform.UsesActionSheets || anchor is null)
        {
            sheet.Show(title, subtitle, options);
            return;
        }

        var menu = new MenuFlyout { Placement = PlacementMode.BottomEdgeAlignedLeft };
        foreach (var option in options)
        {
            var item = new MenuItem
            {
                Header = option.Text,
                Icon = new PathIcon { Data = option.Icon, Width = 14, Height = 14 },
            };
            var run = option.Run;
            item.Click += (_, _) => run();
            menu.Items.Add(item);
        }

        menu.ShowAt(anchor);
    }
}
