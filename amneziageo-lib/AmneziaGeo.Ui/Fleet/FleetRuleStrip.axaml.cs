using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace AmneziaGeo.Ui.Fleet;

/// <summary>
/// Полоса режима под правилом: два списка - куда правило едет и куда уходит.
/// </summary>
internal sealed partial class FleetRuleStrip : UserControl
{
    /// <summary>
    /// ctor
    /// </summary>
    public FleetRuleStrip()
    {
        InitializeComponent();
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }
}
