using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using AmneziaGeo.Localization;
using AmneziaGeo.Ui.Services;
using AmneziaGeo.Ui.ViewModels;

namespace AmneziaGeo.Ui.Views;

/// <summary>
/// Inline selective bundle export: check configs and routing lists, then copy / save the JSON.
/// </summary>
internal sealed partial class BundleExportView : UserControl
{
    /// <summary>
    /// ctor
    /// </summary>
    public BundleExportView()
    {
        InitializeComponent();
        StatusText.PropertyChanged += OnStatusChanged;
    }

    // Carries the panel to the result line as it appears: a remote scrolls only what it moves onto.
    private void OnStatusChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == TextBlock.TextProperty && StatusText.Text is { Length: > 0 })
        {
            Dispatcher.UIThread.Post(() => StatusText.BringIntoView(), DispatcherPriority.Background);
        }
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

    // Hands the bundle to another application.
    private async void OnSendFile(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not BundleExportViewModel vm)
        {
            return;
        }

        await ExportActions.SendTextAsync(vm.Payload, vm.SuggestedFileName);
    }
}
