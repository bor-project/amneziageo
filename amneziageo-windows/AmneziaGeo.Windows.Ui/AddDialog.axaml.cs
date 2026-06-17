using System.IO;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using AmneziaGeo.Windows.Ui.Services;
using AmneziaGeo.Windows.Ui.ViewModels;

namespace AmneziaGeo.Windows.Ui;

/// <summary>
/// Dialog to add a configuration or balancer, or restore a backup.
/// </summary>
public sealed partial class AddDialog : Window
{
    /// <summary>
    /// ctor
    /// </summary>
    public AddDialog()
    {
        InitializeComponent();
    }

    private async void OnBrowseConfig(object? sender, RoutedEventArgs e)
    {
        var file = await PickFileAsync("Конфигурация", "conf");
        if (file is not null && DataContext is AddDialogViewModel vm)
        {
            vm.ConfigPath = file;
            if (string.IsNullOrWhiteSpace(vm.ConfigName))
            {
                vm.ConfigName = Path.GetFileNameWithoutExtension(file);
            }
        }
    }

    private async void OnImportQrImage(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not AddDialogViewModel vm)
        {
            return;
        }

        var path = await PickFileAsync("QR-картинка", "png", "jpg", "jpeg", "bmp");
        if (path is null)
        {
            return;
        }

        try
        {
            using var bitmap = new Bitmap(path);
            ApplyQrFromBitmap(vm, bitmap);
        }
        catch (Exception ex)
        {
            vm.StatusMessage = ex.Message;
        }
    }

    private async void OnScanCamera(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not AddDialogViewModel vm)
        {
            return;
        }

        var scan = new ScanDialogViewModel();
        var dialog = new ScanDialog { DataContext = scan };
        var ok = await dialog.ShowDialog<bool>(this);
        if (ok && scan.Result is not null)
        {
            ApplyImported(vm, scan.Result);
        }
    }

    private async void OnImportQrClipboard(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not AddDialogViewModel vm)
        {
            return;
        }

        var clipboard = GetTopLevel(this)?.Clipboard;
        if (clipboard is null)
        {
            return;
        }

        try
        {
            // The string-format clipboard API is the portable way to pull raw image bytes by platform
            // format name; the typed replacement does not expose arbitrary image formats here.
#pragma warning disable CS0618
            var formats = await clipboard.GetFormatsAsync();
            byte[]? bytes = null;
            foreach (var format in new[] { "PNG", "image/png", "public.png", "image/bmp", "Bitmap", "DeviceIndependentBitmap" })
            {
                if (formats.Contains(format) && await clipboard.GetDataAsync(format) is byte[] data && data.Length > 0)
                {
                    bytes = data;
                    break;
                }
            }
#pragma warning restore CS0618

            if (bytes is null)
            {
                vm.StatusMessage = "В буфере обмена нет картинки — используйте файл или камеру";
                return;
            }

            using var stream = new MemoryStream(bytes);
            using var bitmap = new Bitmap(stream);
            ApplyQrFromBitmap(vm, bitmap);
        }
        catch (Exception ex)
        {
            vm.StatusMessage = ex.Message;
        }
    }

    private static void ApplyQrFromBitmap(AddDialogViewModel vm, Bitmap bitmap)
    {
        var text = QrCodec.Decode(bitmap);
        if (text is null)
        {
            vm.StatusMessage = "QR-код не найден на картинке";
            return;
        }

        var imported = VpnLinkCodec.TryDecodeQr(text);
        if (imported is null)
        {
            vm.StatusMessage = "QR распознан, но это не конфигурация";
            return;
        }

        ApplyImported(vm, imported);
    }

    private static void ApplyImported(AddDialogViewModel vm, VpnLinkCodec.Imported imported)
    {
        vm.ImportText = imported.ConfText;
        if (string.IsNullOrWhiteSpace(vm.ConfigName) && !string.IsNullOrWhiteSpace(imported.Name))
        {
            vm.ConfigName = imported.Name!;
        }

        vm.StatusMessage = "QR распознан — нажмите OK для импорта";
    }

    private async void OnBrowseRestore(object? sender, RoutedEventArgs e)
    {
        var file = await PickFileAsync("Бэкап", "agbackup", "zip");
        if (file is not null && DataContext is AddDialogViewModel vm)
        {
            vm.RestorePath = file;
        }
    }

    private async void OnOk(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not AddDialogViewModel vm)
        {
            return;
        }

        if (vm.SelectedTabIndex == 2)
        {
            if (vm.TryStartRestore())
            {
                Close(true);
            }

            return;
        }

        if (await vm.ConfirmAsync())
        {
            Close(true);
        }
    }

    private void OnCancel(object? sender, RoutedEventArgs e)
    {
        Close(false);
    }

    private async Task<string?> PickFileAsync(string title, params string[] extensions)
    {
        var patterns = extensions.Select(ext => $"*.{ext}").ToList();
        var options = new FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType(title) { Patterns = patterns }],
        };

        var files = await StorageProvider.OpenFilePickerAsync(options);
        return files.Count > 0 ? files[0].TryGetLocalPath() : null;
    }
}
