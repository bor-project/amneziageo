using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using AmneziaGeo.Decl;
using AmneziaGeo.Localization;
using AmneziaGeo.Ui.Services;
using AmneziaGeo.Ui.ViewModels;

namespace AmneziaGeo.Ui.Views;

/// <summary>
/// Config screen view.
/// </summary>
internal sealed partial class ConfigView : UserControl
{
    private readonly HeaderReflow _header;

    /// <summary>
    /// ctor
    /// </summary>
    public ConfigView()
    {
        InitializeComponent();
        _header = new HeaderReflow(HeaderGrid, HeaderTabs, PickerHost, Picker, PickerLabelFloat, PickerLabelInline,
            () => (DataContext as ConfigViewModel)?.IsCompact ?? false);
        DataContextChanged += (_, _) => _header.Apply();
    }

    // Copy the export payload (vpn link or .conf text) to the clipboard.
    private async void OnCopyExport(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is not ExportDialogViewModel vm)
        {
            return;
        }

        if (await ExportActions.CopyToClipboardAsync(this, vm.Payload))
        {
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

        // One «Файл» picker for both a config text file and a QR image; content sniff decides which.
        var file = await PickFileAsync(Loc.Instance.Get("MainCode_ConfigurationTitle"),
            "conf", "txt", "vpn", "png", "jpg", "jpeg", "bmp", "gif");
        if (file is null)
        {
            return;
        }

        try
        {
            // Read through the storage stream, not a local path: an Android picker returns a content:// URI whose
            // TryGetLocalPath is null, so the old path-based read silently dropped every pick on mobile.
            var raw = await ReadAllBytesAsync(file);
            if (LooksLikeImage(raw))
            {
                using var stream = new MemoryStream(raw);
                using var bitmap = new Bitmap(stream);
                ApplyQrToSectionConfig(vm, bitmap);
                return;
            }

            var text = new UTF8Encoding(false, false).GetString(raw).TrimStart('﻿');
            if (VpnLinkCodec.TryDecode(text) is not { } imported)
            {
                vm.SectionConfigText = text;
                vm.SectionConfigStatus = Loc.Instance.Get("MainVm_ConfigNotRecognized");
                vm.ImportMethod = ConfigImportMethod.Manual;
                return;
            }

            vm.SeedSectionNameFromConfig(imported, Path.GetFileNameWithoutExtension(file.Name));
            vm.SectionConfigText = imported.ConfText;
            vm.SectionConfigStatus = string.Empty;
            vm.ImportMethod = ConfigImportMethod.Manual;
        }
        catch (Exception ex)
        {
            vm.SectionConfigStatus = ex.Message;
        }
    }

    private static async Task<byte[]> ReadAllBytesAsync(IStorageFile file)
    {
        await using var stream = await file.OpenReadAsync();
        using var memory = new MemoryStream();
        await stream.CopyToAsync(memory);
        return memory.ToArray();
    }

    // PNG / JPEG / BMP / GIF magic bytes.
    private static bool LooksLikeImage(byte[] raw)
    {
        if (raw.Length < 4)
        {
            return false;
        }

        if (raw[0] == 0x89 && raw[1] == 0x50 && raw[2] == 0x4E && raw[3] == 0x47)
        {
            return true;
        }

        if (raw[0] == 0xFF && raw[1] == 0xD8)
        {
            return true;
        }

        if (raw[0] == 0x42 && raw[1] == 0x4D)
        {
            return true;
        }

        if (raw[0] == 0x47 && raw[1] == 0x49 && raw[2] == 0x46)
        {
            return true;
        }

        return false;
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

        vm.SeedSectionNameFromConfig(imported);
        vm.SectionConfigText = imported.ConfText;
        vm.SectionConfigStatus = string.Empty;
        vm.ImportMethod = ConfigImportMethod.Manual;
    }

    private async Task<IStorageFile?> PickFileAsync(string title, params string[] extensions)
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
        return files.Count > 0 ? files[0] : null;
    }
}
