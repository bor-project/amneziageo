using System.IO;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using AmneziaGeo.Windows.Ui.ViewModels;

namespace AmneziaGeo.Windows.Ui;

/// <summary>
/// Generic dialog that shows a ready payload as a QR code, with copy / save. Used to share a routing list
/// (its name + rules) without keeping a live QR in the editor (that stole input focus).
/// </summary>
public sealed partial class QrDialog : Window
{
    /// <summary>
    /// ctor
    /// </summary>
    public QrDialog()
    {
        InitializeComponent();
    }

    private async void OnCopy(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not QrDialogViewModel vm)
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

    private async void OnSave(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not QrDialogViewModel vm)
        {
            return;
        }

        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Сохранить",
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

    private void OnClose(object? sender, RoutedEventArgs e)
    {
        Close();
    }
}
