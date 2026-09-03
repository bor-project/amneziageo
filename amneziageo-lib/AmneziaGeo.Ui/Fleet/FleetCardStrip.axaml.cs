using Avalonia.Controls;
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

    private void OnPickSlot(object? sender, RoutedEventArgs e)
    {
        if (sender is Control anchor && DataContext is FleetConfigItemViewModel card)
        {
            TunnelSlotMenu.Present(anchor, card);
        }
    }
}
