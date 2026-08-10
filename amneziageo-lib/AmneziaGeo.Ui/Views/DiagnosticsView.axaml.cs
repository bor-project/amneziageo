using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using AmneziaGeo.Ui.Controls;

namespace AmneziaGeo.Ui.Views;

/// <summary>
/// Diagnostics screen view: the log pane and the runtime-configuration pane.
/// </summary>
internal sealed partial class DiagnosticsView : UserControl
{
    /// <summary>
    /// ctor
    /// </summary>
    public DiagnosticsView()
    {
        InitializeComponent();
    }

    // Steps from the tabs into the pane under them.
    private void OnTabsKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Handled || e.Key is not Key.Down)
        {
            return;
        }

        e.Handled = PaneFocus.FocusFirst(Body);
    }

    // Returns to the tabs from the pane's top row.
    private void OnBodyKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Handled || e.Key is not Key.Up)
        {
            return;
        }

        if (TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement() is not Visual focused
            || PaneFocus.HasNeighbour(Body, focused, NavigationDirection.Up))
        {
            return;
        }

        e.Handled = PaneFocus.FocusFirst(PaneTabs);
    }
}
