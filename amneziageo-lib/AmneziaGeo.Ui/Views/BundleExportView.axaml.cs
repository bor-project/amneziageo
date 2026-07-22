using Avalonia.Controls;
using Avalonia.Interactivity;
using AmneziaGeo.Localization;
using AmneziaGeo.Ui.Services;
using AmneziaGeo.Ui.ViewModels;

namespace AmneziaGeo.Ui.Views;

/// <summary>
/// Inline selective bundle export: check profiles, configs and routing lists, then copy / save the JSON.
/// </summary>
internal sealed partial class BundleExportView : UserControl
{
    /// <summary>
    /// ctor
    /// </summary>
    public BundleExportView()
    {
        InitializeComponent();
    }

    private async void OnCopy(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not BundleExportViewModel vm)
        {
            return;
        }

        if (await ExportActions.CopyToClipboardAsync(this, vm.Payload))
        {
            vm.StatusMessage = Loc.Instance.Get("BundleExportCode_CopiedToClipboard");
        }
    }

    private async void OnSaveFile(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not BundleExportViewModel vm)
        {
            return;
        }

        if (await ExportActions.SaveTextAsync(this, vm.Payload, Loc.Instance.Get("BundleExportCode_SaveBundleTitle"), vm.SuggestedFileName))
        {
            vm.StatusMessage = Loc.Instance.Get("BundleExportCode_Saved");
        }
    }
}
