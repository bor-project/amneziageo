using System.IO;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using AmneziaGeo.Windows.Ui.ViewModels;
using AmneziaGeo.Localization;

namespace AmneziaGeo.Windows.Ui;

/// <summary>
/// Modal dialog for the selective bundle export: check profiles, configs, and routing lists to
/// bundle, then copy/save the resulting JSON.
/// </summary>
public sealed partial class BundleExportDialog : Window
{
    /// <summary>
    /// ctor
    /// </summary>
    public BundleExportDialog()
    {
        InitializeComponent();
    }

    private async void OnCopy(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not BundleExportDialogViewModel vm)
        {
            return;
        }

        var clipboard = GetTopLevel(this)?.Clipboard;
        if (clipboard is not null)
        {
            await clipboard.SetTextAsync(vm.Payload);
            vm.StatusMessage = Loc.Instance.Get("BundleExportCode_CopiedToClipboard");
        }
    }

    private async void OnSaveFile(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not BundleExportDialogViewModel vm)
        {
            return;
        }

        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = Loc.Instance.Get("BundleExportCode_SaveBundleTitle"),
            SuggestedFileName = vm.SuggestedFileName,
        });
        if (file is null)
        {
            return;
        }

        await using var stream = await file.OpenWriteAsync();
        await using var writer = new StreamWriter(stream);
        await writer.WriteAsync(vm.Payload);
        vm.StatusMessage = Loc.Instance.Get("BundleExportCode_Saved");
    }

    private void OnDone(object? sender, RoutedEventArgs e)
    {
        Close(true);
    }

    private void OnCancel(object? sender, RoutedEventArgs e)
    {
        Close(false);
    }
}
