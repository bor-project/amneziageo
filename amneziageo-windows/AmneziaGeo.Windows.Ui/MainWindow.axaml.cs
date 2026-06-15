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

    private async void OnRoutingListTapped(object? sender, TappedEventArgs e)
    {
        if (sender is not Control { DataContext: RoutingListSummaryViewModel list })
        {
            return;
        }

        if (DataContext is not MainWindowViewModel vm)
        {
            return;
        }

        var dialog = new RoutingListEditorDialog
        {
            DataContext = new RoutingListEditorViewModel(vm.Connection, list.Id, list.Name),
        };
        await dialog.ShowDialog(this);
    }

    private async void OnAddRoutingListClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm)
        {
            return;
        }

        var dialog = new RoutingListEditorDialog
        {
            DataContext = new RoutingListEditorViewModel(vm.Connection),
        };
        await dialog.ShowDialog(this);
    }

    private async void OnOpenRoutingClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: RoutingListSummaryViewModel list })
        {
            return;
        }

        if (DataContext is not MainWindowViewModel vm)
        {
            return;
        }

        var dialog = new RoutingListEditorDialog
        {
            DataContext = new RoutingListEditorViewModel(vm.Connection, list.Id, list.Name),
        };
        await dialog.ShowDialog(this);
    }
}
