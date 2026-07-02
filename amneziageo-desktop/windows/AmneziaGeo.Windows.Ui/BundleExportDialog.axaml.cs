using System.IO;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using AmneziaGeo.Windows.Ui.ViewModels;

namespace AmneziaGeo.Windows.Ui;

/// <summary>
/// Modal dialog for the selective bundle export (#91): the user checks which profiles, configs, and routing
/// lists to bundle (phase 1, the tree), exports, then copies/saves the resulting JSON (phase 2). Owns its
/// own clipboard/file IO, like <see cref="QrDialog"/> and <see cref="ExportDialog"/>.
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
            vm.StatusMessage = "Скопировано в буфер обмена.";
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
            Title = "Сохранить бандл",
            SuggestedFileName = vm.SuggestedFileName,
        });
        if (file is null)
        {
            return;
        }

        await using var stream = await file.OpenWriteAsync();
        await using var writer = new StreamWriter(stream);
        await writer.WriteAsync(vm.Payload);
        vm.StatusMessage = "Сохранено.";
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
