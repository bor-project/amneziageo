using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace AmneziaGeo.Ui.Fleet;

/// <summary>
/// Полоса режима на карточке сервера: поле «Туннель» и обязанности.
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
}
