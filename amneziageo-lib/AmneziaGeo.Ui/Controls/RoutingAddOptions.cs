using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using AmneziaGeo.Localization;
using AmneziaGeo.Ui.Services;
using AmneziaGeo.Ui.ViewModels;

namespace AmneziaGeo.Ui.Controls;

/// <summary>
/// Способы добавления списка маршрутизации у кнопки «Добавить»: один набор и в разделе, и над списком широкой
/// раскладки.
/// </summary>
internal static class RoutingAddOptions
{
    /// <summary>
    /// Выносит способы на экран. Выбранный открывает черновик списка.
    /// </summary>
    public static void Present(Control? anchor, Visual owner, RoutingViewModel vm)
    {
        var options = new List<ActionOption>
        {
            new(Loc.Instance.Get("Main_FileButton"), Glyphs.File, () => ImportFromFile(owner, vm)),
            new(Loc.Instance.Get("Main_PasteButton"), Glyphs.Paste, () => ImportFromClipboard(owner, vm)),
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
            anchor,
            vm.Sheet,
            Loc.Instance.Get("Main_AddListTitle"),
            string.Empty,
            options);
    }

    // Черновик открывается по выбранному файлу - отменённый выбор не оставляет пустую форму.
    private static async void ImportFromFile(Visual owner, RoutingViewModel vm)
    {
        // Список экспортируется картинкой QR, поэтому та же картинка принимается назад.
        var file = await FilePickers.OpenAsync(
            owner,
            Loc.Instance.Get("MainCode_RoutingListTitle"),
            "txt", "conf", "png", "jpg", "jpeg", "bmp", "gif");
        if (file is null)
        {
            return;
        }

        vm.BeginImportDraft();
        try
        {
            // Read through the storage stream, not a local path: an Android picker returns a content:// URI whose
            // TryGetLocalPath is null.
            var raw = await file.ReadAllBytesAsync();
            if (!FileContent.LooksLikeImage(raw))
            {
                vm.ApplyImportText(new UTF8Encoding(false, false).GetString(raw).TrimStart('﻿'));
                return;
            }

            using var picture = new MemoryStream(raw);
            if (QrCodec.Decode(picture) is not { } scanned)
            {
                if (vm.RoutingEditor is { } withoutCode)
                {
                    withoutCode.StatusMessage = Loc.Instance.Get("MainCode_QrNotFound");
                }

                return;
            }

            vm.ApplyImportText(scanned);
        }
        catch (Exception ex)
        {
            if (vm.RoutingEditor is { } editor)
            {
                editor.StatusMessage = ex.Message;
            }
        }
    }

    // Черновик открывается текстом из буфера обмена.
    private static async void ImportFromClipboard(Visual owner, RoutingViewModel vm)
    {
        var clipboard = TopLevel.GetTopLevel(owner)?.Clipboard;
        if (clipboard is null)
        {
            return;
        }

        var text = await clipboard.TryGetTextAsync();
        if (string.IsNullOrWhiteSpace(text))
        {
            vm.BeginImportDraft();
            if (vm.RoutingEditor is { } editor)
            {
                editor.StatusMessage = Loc.Instance.Get("MainCode_ClipboardNoText");
            }

            return;
        }

        vm.BeginImportDraft();
        vm.ApplyImportText(text);
    }
}
