using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using AmneziaGeo.Decl;
using AmneziaGeo.Localization;
using AmneziaGeo.Ui.Controls;
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

    // Способы добавления: файл, живой сканер QR и ручной ввод.
    private void OnAddOptions(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not ConfigViewModel vm)
        {
            return;
        }

        var options = new List<ActionOption>
        {
            new(Loc.Instance.Get("Main_FileButton"), Glyphs.File, ImportFromFile),
        };
        if (vm.CameraScanAvailable)
        {
            options.Add(new ActionOption(
                Loc.Instance.Get("Main_CameraButton"),
                Glyphs.Qr,
                () => vm.BeginCameraImportCommand.Execute(null)));
        }

        options.Add(new ActionOption(
            Loc.Instance.Get("Main_CreateManuallyButton"),
            Glyphs.Pencil,
            () => vm.BeginManualImportCommand.Execute(null)));

        ActionOptions.Present(
            sender as Control,
            vm.Sheet,
            Loc.Instance.Get("Main_AddConfigTitle"),
            Loc.Instance.Get("Main_AddConfigSubtitle"),
            options);
    }

    // Способы экспорта: экран QR, ссылка в буфер и файл конфигурации.
    private void OnExportOptions(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not ConfigViewModel vm)
        {
            return;
        }

        var options = new List<ActionOption>
        {
            new(
                Loc.Instance.Get("Main_ShowQrButton"),
                Glyphs.Qr,
                () => vm.SelectConfigSectionCommand.Execute("export")),
            new(Loc.Instance.Get("Main_CopyLinkButton"), Glyphs.Link, CopyExportLink),
            new(Loc.Instance.Get("Main_SaveToFileButton"), Glyphs.Download, SaveExportFile),
        };

        ActionOptions.Present(
            sender as Control,
            vm.Sheet,
            Loc.Instance.Get("Main_ExportConfigTitle"),
            Loc.Instance.Get("Main_ExportConfigSubtitle"),
            options);
    }

    // Копирует ссылку vpn:// открытой конфигурации.
    private async void CopyExportLink()
    {
        if (DataContext is not ConfigViewModel { ConfigExport: { } export })
        {
            return;
        }

        export.ShowQrLinkCommand.Execute(null);
        if (await ExportActions.CopyToClipboardAsync(this, export.Payload))
        {
            export.StatusMessage = Loc.Instance.Get("QrCode_CopiedToClipboard");
        }
    }

    // Сохраняет текст конфигурации файлом.
    private async void SaveExportFile()
    {
        if (DataContext is not ConfigViewModel { ConfigExport: { } export })
        {
            return;
        }

        var saved = await ExportActions.SaveTextAsync(
            this,
            export.ConfText,
            Loc.Instance.Get("QrCode_SaveTitle"),
            export.ConfigName + ".conf");
        if (saved)
        {
            export.StatusMessage = Loc.Instance.Get("MainCode_Saved");
        }
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

    // Hands the rendered QR to another application.
    private async void OnSendQr(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is not ExportDialogViewModel vm || vm.QrImage is not { } qr)
        {
            return;
        }

        await ExportActions.SendImageAsync(qr, vm.ConfigName + ".png");
    }

    // Standalone config-import: adds a config to the shared catalogue. Черновик открывается по выбранному
    // файлу - отменённый выбор не оставляет пустую форму.
    private async void ImportFromFile()
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

        vm.BeginImportDraft();
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
            var raw = await file.ReadAllBytesAsync();
            if (FileContent.LooksLikeImage(raw))
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
