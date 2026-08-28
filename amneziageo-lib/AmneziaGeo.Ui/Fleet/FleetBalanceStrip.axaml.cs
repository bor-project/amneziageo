using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace AmneziaGeo.Ui.Fleet;

/// <summary>
/// Числа балансировщика под переключателем режима.
/// </summary>
internal sealed partial class FleetBalanceStrip : UserControl
{
    /// <summary>
    /// ctor
    /// </summary>
    public FleetBalanceStrip()
    {
        InitializeComponent();
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }
}
