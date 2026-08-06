using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
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

        var saved = await ExportActions.SaveBinaryAsync(
            this,
            stream => qr.Save(stream),
            Loc.Instance.Get("QrCode_SaveTitle"),
            vm.ConfigName + ".png",
            "png",
            "PNG");
        if (saved)
        {
            vm.StatusMessage = Loc.Instance.Get("QrCode_Saved");
        }
    }

    // Standalone config-import: adds a config to the shared catalogue.
    private async void OnSectionConfigBrowse(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not ConfigViewModel vm)
        {
            return;
        }

        // One «Файл» picker for both a config text file and a QR image; content sniff decides which.
        var file = await FilePickers.OpenAsync(this, Loc.Instance.Get("MainCode_ConfigurationTitle"),
            "conf", "txt", "vpn", "png", "jpg", "jpeg", "bmp", "gif");
        if (file is null)
        {
            return;
        }

        await ReadIntoSectionConfigAsync(vm, file);
        // A system picker drops focus on the way back; the name is what the user checks first.
        SectionConfigNameBox.Focus(UiPlatform.IsTelevision ? NavigationMethod.Directional : NavigationMethod.Unspecified);
    }

    private async Task ReadIntoSectionConfigAsync(ConfigViewModel vm, PickedFile file)
    {
        try
        {
            // Read through the storage stream, not a local path: an Android picker returns a content:// URI whose
            // TryGetLocalPath is null, so the old path-based read silently dropped every pick on mobile.
            var raw = await ReadAllBytesAsync(file);
            if (LooksLikeImage(raw))
            {
                ApplyQrToSectionConfig(vm, raw);
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

    private static async Task<byte[]> ReadAllBytesAsync(PickedFile file)
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

    private static void ApplyQrToSectionConfig(ConfigViewModel vm, byte[] image)
    {
        using var stream = new MemoryStream(image);
        var text = QrCodec.Decode(stream);
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
}
