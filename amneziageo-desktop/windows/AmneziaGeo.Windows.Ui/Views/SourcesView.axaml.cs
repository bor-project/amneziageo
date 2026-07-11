using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using AmneziaGeo.Localization;
using AmneziaGeo.Windows.Ui.ViewModels;

namespace AmneziaGeo.Windows.Ui.Views;

/// <summary>
/// Geo sources screen view.
/// </summary>
internal sealed partial class SourcesView : UserControl
{
    /// <summary>
    /// ctor
    /// </summary>
    public SourcesView()
    {
        InitializeComponent();
    }

    /// <summary>
    /// Opens the per-source action menu (update / delete) on a right-click of a source row.
    /// </summary>
    private void OnSourceRowContext(object? sender, ContextRequestedEventArgs e)
    {
        if (sender is not Control { DataContext: SourceItemViewModel source } target)
        {
            return;
        }

        ShowSourceMenu(target, source, atPointer: true);
        e.Handled = true;
    }

    /// <summary>
    /// Opens the same per-source action menu from the row's "⋮" (kebab) button, anchored to the button.
    /// </summary>
    private void OnSourceKebab(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control { DataContext: SourceItemViewModel source } button)
        {
            return;
        }

        ShowSourceMenu(button, source, atPointer: false);
        e.Handled = true;
    }

    // Double-click a source row to edit it (same as the menu's "Изменить"); ignored while already editing.
    private void OnSourceRowDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (sender is not Control { DataContext: SourceItemViewModel source } || source.IsEditing)
        {
            return;
        }

        source.BeginEditCommand.Execute(null);
        e.Handled = true;
    }

    // Builds the per-source action menu (update / edit / delete). Commands assigned directly from the row's
    // VM because flyout MenuItems do not reliably inherit the row's DataContext in Avalonia 11.
    private static void ShowSourceMenu(Control target, SourceItemViewModel source, bool atPointer)
    {
        var update = new MenuItem
        {
            Header = Loc.Instance.Get("MainCode_UpdateDatabase"),
            Command = source.UpdateCommand,
        };
        var edit = new MenuItem
        {
            Header = Loc.Instance.Get("MainCode_EditDatabase"),
            Command = source.BeginEditCommand,
        };
        var delete = new MenuItem
        {
            Header = Loc.Instance.Get("MainCode_DeleteDatabase"),
            Command = source.RemoveCommand,
        };
        var flyout = new MenuFlyout();
        flyout.Items.Add(update);
        flyout.Items.Add(edit);
        flyout.Items.Add(delete);
        flyout.ShowAt(target, showAtPointer: atPointer);
    }
}
