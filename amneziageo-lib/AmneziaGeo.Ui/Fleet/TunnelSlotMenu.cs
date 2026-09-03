using AmneziaGeo.Ipc.Fleet;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;

namespace AmneziaGeo.Ui.Fleet;

/// <summary>
/// Выпадающий список мест туннеля. Строится на открытии: мест столько же, сколько серверов в цепочке.
/// </summary>
internal static class TunnelSlotMenu
{
    /// <summary>
    /// Роняет список мест у кнопки.
    /// </summary>
    public static void Present(Control anchor, FleetConfigItemViewModel card)
    {
        var menu = new MenuFlyout { Placement = PlacementMode.BottomEdgeAlignedLeft };
        foreach (var choice in card.SlotChoices)
        {
            if (choice.Slot == TunnelRoles.Aside)
            {
                menu.Items.Add(new Separator());
            }

            menu.Items.Add(new MenuItem
            {
                Header = choice.Text,
                Command = card.SetSlotCommand,
                CommandParameter = choice.Slot,
            });
        }

        menu.ShowAt(anchor);
    }
}
