using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using AmneziaGeo.Decl;
using AmneziaGeo.Localization;
using AmneziaGeo.Ui.Services;
using AmneziaGeo.Ui.ViewModels;

namespace AmneziaGeo.Ui.Controls;

/// <summary>
/// Откуда взята добавляемая конфигурация.
/// </summary>
internal enum ConfigAddSource
{
    File,
    Camera,
    Manual,
}

/// <summary>
/// Способы добавления конфигурации у кнопки «Добавить»: один набор и на главном экране, и в настройках.
/// Способ выбирается до черновика, поэтому форма создания открывается уже с источником.
/// </summary>
internal static class ConfigAddOptions
{
    /// <summary>
    /// Выносит способы на экран. Выбранный открывает черновик и сообщается вызвавшему.
    /// </summary>
    public static void Present(Control? anchor, Visual owner, ConfigViewModel vm, Action<ConfigAddSource> started)
    {
        var options = new List<ActionOption>
        {
            new(Loc.Instance.Get("Main_FileButton"), Glyphs.File, () => ImportFromFile(owner, vm, started)),
        };
        if (vm.CameraScanAvailable)
        {
            options.Add(new ActionOption(
                Loc.Instance.Get("Main_CameraButton"),
                Glyphs.Qr,
                () =>
                {
                    vm.BeginCameraImportCommand.Execute(null);
                    started(ConfigAddSource.Camera);
                }));
        }

        options.Add(new ActionOption(
            Loc.Instance.Get("Main_CreateManuallyButton"),
            Glyphs.Pencil,
            () =>
            {
                vm.BeginManualImportCommand.Execute(null);
                started(ConfigAddSource.Manual);
            }));

        ActionOptions.Present(
            anchor,
            vm.Sheet,
            Loc.Instance.Get("Main_AddConfigTitle"),
            string.Empty,
            options);
    }

    // Черновик открывается по выбранному файлу - отменённый выбор не оставляет пустую форму.
    private static async void ImportFromFile(Visual owner, ConfigViewModel vm, Action<ConfigAddSource> started)
    {
        // Один выбор «Файл» и под текст конфигурации, и под картинку с QR; что пришло, решает содержимое.
        var file = await FilePickers.OpenAsync(
            owner,
            Loc.Instance.Get("MainCode_ConfigurationTitle"),
            "conf", "txt", "vpn", "png", "jpg", "jpeg", "bmp", "gif");
        if (file is null)
        {
            return;
        }

        vm.BeginImportDraft();
        await ReadIntoDraftAsync(vm, file);
        started(ConfigAddSource.File);
    }

    // Read through the storage stream, not a local path: an Android picker returns a content:// URI whose
    // TryGetLocalPath is null, so a path-based read silently drops every pick on mobile.
    private static async Task ReadIntoDraftAsync(ConfigViewModel vm, PickedFile file)
    {
        try
        {
            var raw = await file.ReadAllBytesAsync();
            if (FileContent.LooksLikeImage(raw))
            {
                ApplyQrToDraft(vm, raw);
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

    private static void ApplyQrToDraft(ConfigViewModel vm, byte[] image)
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
