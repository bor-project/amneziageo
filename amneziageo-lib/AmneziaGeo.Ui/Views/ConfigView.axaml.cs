using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using AmneziaGeo.Localization;
using AmneziaGeo.Ui.Services;
using AmneziaGeo.Ui.ViewModels;

namespace AmneziaGeo.Ui.Views;

/// <summary>
/// Config screen view.
/// </summary>
internal sealed partial class ConfigView : UserControl
{
    /// <summary>
    /// ctor
    /// </summary>
    public ConfigView()
    {
        InitializeComponent();
    }

    // Copy the export payload (vpn link or .conf text) to the clipboard.
    private async void OnCopyExport(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is not ExportDialogViewModel vm)
        {
            return;
        }

        if (TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
        {
            await clipboard.SetTextAsync(vm.Payload);
            vm.StatusMessage = Loc.Instance.Get("QrCode_CopiedToClipboard");
        }
    }

    // Save the rendered QR as a PNG.
    private async void OnDownloadQr(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is not ExportDialogViewModel vm || vm.QrImage is not { } qr)
        {
            return;
        }

        if (TopLevel.GetTopLevel(this) is not { } top)
        {
            return;
        }

        var file = await top.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = Loc.Instance.Get("QrCode_SaveTitle"),
            SuggestedFileName = vm.ConfigName + ".png",
            DefaultExtension = "png",
            FileTypeChoices = [new FilePickerFileType("PNG") { Patterns = ["*.png"] }],
        });
        if (file is null)
        {
            return;
        }

        await using var stream = await file.OpenWriteAsync();
        qr.Save(stream);
        vm.StatusMessage = Loc.Instance.Get("QrCode_Saved");
    }

    // Standalone config-import: adds a config to the shared catalogue without a profile.
    private async void OnSectionConfigBrowse(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not ConfigViewModel vm)
        {
            return;
        }

        // One «Файл» picker for both a config text file and a QR image; extension decides which.
        var path = await PickFileAsync(Loc.Instance.Get("MainCode_ConfigurationTitle"),
            "conf", "txt", "vpn", "png", "jpg", "jpeg", "bmp", "gif");
        if (path is null)
        {
            return;
        }

        try
        {
            var ext = Path.GetExtension(path).TrimStart('.').ToLowerInvariant();
            if (ext is "png" or "jpg" or "jpeg" or "bmp" or "gif")
            {
                using var bitmap = new Bitmap(path);
                ApplyQrToSectionConfig(vm, bitmap);
                return;
            }

            var raw = File.ReadAllText(path);
            if (VpnLinkCodec.TryDecode(raw) is not { } imported)
            {
                vm.SectionConfigText = raw;
                vm.SectionConfigStatus = Loc.Instance.Get("MainVm_ConfigNotRecognized");
                vm.ImportMethod = ConfigImportMethod.Manual;
                return;
            }

            vm.SectionConfigText = imported.ConfText;
            vm.SectionConfigStatus = string.Empty;
            vm.ImportMethod = ConfigImportMethod.Manual;
            if (vm.SectionConfigNameIsDefault)
            {
                var name = !string.IsNullOrWhiteSpace(imported.Name)
                    ? imported.Name!
                    : Path.GetFileNameWithoutExtension(path);
                if (!string.IsNullOrWhiteSpace(name))
                {
                    vm.SectionConfigName = name;
                }
            }
        }
        catch (Exception ex)
        {
            vm.SectionConfigStatus = ex.Message;
        }
    }

    private static void ApplyQrToSectionConfig(ConfigViewModel vm, Bitmap bitmap)
    {
        var text = QrCodec.Decode(bitmap);
        if (text is null)
        {
            vm.SectionConfigStatus = Loc.Instance.Get("MainCode_QrNotFound");
            return;
        }

        var imported = VpnLinkCodec.TryDecodeQr(text);
        if (imported is null)
        {
            vm.SectionConfigStatus = Loc.Instance.Get("MainCode_QrNotConfig");
            return;
        }

        vm.SectionConfigText = imported.ConfText;
        if (vm.SectionConfigNameIsDefault && !string.IsNullOrWhiteSpace(imported.Name))
        {
            vm.SectionConfigName = imported.Name!;
        }

        vm.SectionConfigStatus = string.Empty;
        vm.ImportMethod = ConfigImportMethod.Manual;
    }

    private async Task<string?> PickFileAsync(string title, params string[] extensions)
    {
        if (TopLevel.GetTopLevel(this) is not { } top)
        {
            return null;
        }

        var patterns = extensions.Select(ext => $"*.{ext}").ToList();
        var options = new FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType(title) { Patterns = patterns }],
        };

        var files = await top.StorageProvider.OpenFilePickerAsync(options);
        return files.Count > 0 ? files[0].TryGetLocalPath() : null;
    }
}
