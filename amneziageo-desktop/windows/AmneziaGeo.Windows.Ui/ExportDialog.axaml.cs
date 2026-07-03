using System.IO;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using AmneziaGeo.Windows.Ui.ViewModels;
using AmneziaGeo.Localization;

namespace AmneziaGeo.Windows.Ui;

/// <summary>
/// Dialog that exports a config as a raw .conf or an Amnezia vpn:// link, with a QR code, for copy / save.
/// </summary>
public sealed partial class ExportDialog : Window
{
    /// <summary>
    /// ctor
    /// </summary>
    public ExportDialog()
    {
        InitializeComponent();
    }

    private async void OnCopy(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not ExportDialogViewModel vm)
        {
            return;
        }

        var clipboard = GetTopLevel(this)?.Clipboard;
        if (clipboard is not null)
        {
            await clipboard.SetTextAsync(vm.Payload);
            vm.StatusMessage = Loc.Instance.Get("ExportCode_CopiedToClipboard");
        }
    }

    private async void OnSave(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not ExportDialogViewModel vm)
        {
            return;
        }

        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = Loc.Instance.Get("ExportCode_SaveConfigTitle"),
            SuggestedFileName = vm.SuggestedFileName,
        });
        if (file is null)
        {
            return;
        }

        await using var stream = await file.OpenWriteAsync();
        await using var writer = new StreamWriter(stream);
        await writer.WriteAsync(vm.Payload);
        vm.StatusMessage = Loc.Instance.Get("ExportCode_Saved");
    }

    private void OnClose(object? sender, RoutedEventArgs e)
    {
        Close();
    }
}
