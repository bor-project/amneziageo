using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using AmneziaGeo.Windows.Ui.ViewModels;

namespace AmneziaGeo.Windows.Ui;

/// <summary>
/// The main window.
/// </summary>
public sealed partial class MainWindow : Window
{
    /// <summary>
    /// ctor
    /// </summary>
    public MainWindow()
    {
        InitializeComponent();
    }

    private async void OnAddClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm)
        {
            return;
        }

        var dialog = new AddDialog
        {
            DataContext = new AddDialogViewModel(vm.Connection, vm.ConfigNames()),
        };
        await dialog.ShowDialog(this);
    }

    private async void OnExportMemberClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control { DataContext: string configName })
        {
            return;
        }

        if (DataContext is not MainWindowViewModel vm)
        {
            return;
        }

        var export = new ExportDialogViewModel(vm.Connection, configName);
        var dialog = new ExportDialog { DataContext = export };
        _ = export.LoadAsync();
        await dialog.ShowDialog(this);
    }

    private async void OnConfigTapped(object? sender, TappedEventArgs e)
    {
        if (sender is not Control { DataContext: ConfigItemViewModel config })
        {
            return;
        }

        if (DataContext is not MainWindowViewModel vm)
        {
            return;
        }

        var dialog = new ConfigSettingsDialog
        {
            DataContext = new ConfigSettingsViewModel(vm.Connection, config.Name, config.GeoSplit, config.Rules),
        };
        await dialog.ShowDialog(this);
    }

    /// <summary>
    /// Opens the per-source context menu (delete) on a source row. The menu is built here, with the
    /// command assigned directly from the row's view model, because a MenuItem hosted in a flyout popup
    /// does not reliably inherit the row's DataContext for a {Binding}-based command in Avalonia 11.
    /// </summary>
    private void OnSourceRowClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control { DataContext: SourceItemViewModel source } target)
        {
            return;
        }

        var delete = new MenuItem
        {
            Header = "Удалить базу",
            Command = source.RemoveCommand,
        };
        var flyout = new MenuFlyout
        {
            Placement = PlacementMode.BottomEdgeAlignedLeft,
        };
        flyout.Items.Add(delete);
        flyout.ShowAt(target);
    }

}
