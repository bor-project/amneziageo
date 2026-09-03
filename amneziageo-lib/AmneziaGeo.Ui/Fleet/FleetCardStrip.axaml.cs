using AmneziaGeo.Ipc.Fleet;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;

namespace AmneziaGeo.Ui.Fleet;

/// <summary>
/// Полоса режима на карточке сервера: поле «Туннель» и место в цепочке.
/// </summary>
internal sealed partial class FleetCardStrip : UserControl
{
    /// <summary>
    /// ctor
    /// </summary>
    public FleetCardStrip()
    {
        InitializeComponent();
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }

    // Список мест строится на открытии: их столько же, сколько серверов в цепочке.
    private void OnPickSlot(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control anchor || DataContext is not FleetConfigItemViewModel card)
        {
            return;
        }

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
