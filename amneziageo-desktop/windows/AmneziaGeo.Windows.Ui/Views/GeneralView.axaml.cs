using Avalonia.Controls;
using Avalonia.Interactivity;
using AmneziaGeo.Windows.Ui.ViewModels;

namespace AmneziaGeo.Windows.Ui.Views;

/// <summary>
/// General screen view.
/// </summary>
internal sealed partial class GeneralView : UserControl
{
    /// <summary>
    /// ctor
    /// </summary>
    public GeneralView()
    {
        InitializeComponent();
    }

    // Selective export/import operate on the whole catalogue, owned by the shell view model.
    private async void OnOpenBundleExport(object? sender, RoutedEventArgs e)
    {
        if (TopLevel.GetTopLevel(this) is not Window window || window.DataContext is not MainWindowViewModel vm)
        {
            return;
        }

        var dialogVm = new BundleExportDialogViewModel(vm.Connection, vm.Profile.Profiles, vm.Config.Configs, vm.Routing.RoutingLists);
        await dialogVm.LoadRoutingRulesAsync();
        var dialog = new BundleExportDialog { DataContext = dialogVm };
        await dialog.ShowDialog(window);
    }

    private async void OnOpenBundleImport(object? sender, RoutedEventArgs e)
    {
        if (TopLevel.GetTopLevel(this) is not Window window || window.DataContext is not MainWindowViewModel vm)
        {
            return;
        }

        var dialog = new BundleImportDialog { DataContext = new BundleImportDialogViewModel(vm.Connection) };
        await dialog.ShowDialog(window);
    }
}
