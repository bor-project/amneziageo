using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input.Platform;
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
    Clipboard,
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
            new(Loc.Instance.Get("Main_PasteButton"), Glyphs.Paste, () => ImportFromClipboard(owner, vm, started)),
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

    // Вставка из буфера: что пришло - адрес подписки, конфигурация или ссылка на неё - решает содержимое.
    private static async void ImportFromClipboard(Visual owner, ConfigViewModel vm, Action<ConfigAddSource> started)
    {
        var text = await ReadClipboardAsync(owner);
        vm.BeginImportDraft();
        if (string.IsNullOrWhiteSpace(text))
        {
            vm.SectionConfigStatus = Loc.Instance.Get("MainCode_ClipboardNoText");
        }
        else
        {
            vm.ApplyImportText(text!);
        }

        started(ConfigAddSource.Clipboard);
    }

    private static async Task<string?> ReadClipboardAsync(Visual owner)
    {
        if (TopLevel.GetTopLevel(owner)?.Clipboard is not { } clipboard)
        {
            return null;
        }

        try
        {
            return await clipboard.TryGetTextAsync();
        }
        catch (Exception)
        {
            // Не всякий буфер обмена отдаёт текст.
            return null;
        }
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
            vm.ApplyImportText(text, Path.GetFileNameWithoutExtension(file.Name));
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

        if (!vm.ApplyScannedText(text))
        {
            vm.SectionConfigStatus = Loc.Instance.Get("MainCode_QrNotConfig");
        }
    }
}
