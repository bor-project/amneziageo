using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Markup.Xaml.MarkupExtensions;
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
            // Иконка меню в акценте, как в шторке; свой цвет темы PathIcon мимо палитры приложения.
            var icon = new PathIcon { Data = option.Icon, Width = 14, Height = 14 };
            icon[!TemplatedControl.ForegroundProperty] = new DynamicResourceExtension("AgAccentBrush");
            var item = new MenuItem
            {
                Header = option.Text,
                Icon = icon,
            };
            var run = option.Run;
            item.Click += (_, _) => run();
            menu.Items.Add(item);
        }

        menu.ShowAt(anchor);
    }
}
