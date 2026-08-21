using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using AmneziaGeo.Decl;
using AmneziaGeo.Localization;

namespace AmneziaGeo.Ui.Services;

/// <summary>
/// Тип брошенного файла, распознанный по содержимому.
/// </summary>
internal enum DroppedKind
{
    VpnConfig,
    RoutingList,
    Bundle,
    Unrecognized,
}

/// <summary>
/// Результат распознавания брошенного файла.
/// </summary>
internal sealed record DroppedItem(DroppedKind Kind, VpnLinkCodec.Imported? Config, string? RoutingText);

/// <summary>
/// Определяет тип файла по содержимому (имя игнорируется) и извлекает полезную нагрузку.
/// </summary>
internal static class ImportDispatcher
{
    public static DroppedItem Classify(byte[] raw)
    {
        if (raw is null || raw.Length == 0)
        {
            return new DroppedItem(DroppedKind.Unrecognized, null, null);
        }

        if (LooksLikeImage(raw))
        {
            var fromQr = TryDecodeQrImage(raw);
            return fromQr is not null
                ? new DroppedItem(DroppedKind.VpnConfig, fromQr, null)
                : new DroppedItem(DroppedKind.Unrecognized, null, null);
        }

        var text = DecodeUtf8(raw);
        if (string.IsNullOrWhiteSpace(text))
        {
            return new DroppedItem(DroppedKind.Unrecognized, null, null);
        }

        if (text.Contains("#ageo-routing", StringComparison.OrdinalIgnoreCase))
        {
            return new DroppedItem(DroppedKind.RoutingList, null, text);
        }

        if (LooksLikeBundle(text))
        {
            return new DroppedItem(DroppedKind.Bundle, null, null);
        }

        var config = VpnLinkCodec.TryDecode(text);
        return config is not null
            ? new DroppedItem(DroppedKind.VpnConfig, config, null)
            : new DroppedItem(DroppedKind.Unrecognized, null, null);
    }

    /// <summary>
    /// Читает файл и распознаёт его содержимое.
    /// </summary>
    public static async Task<DroppedItem> ClassifyFileAsync(string path)
    {
        try
        {
            return Classify(await File.ReadAllBytesAsync(path));
        }
        catch (Exception)
        {
            return new DroppedItem(DroppedKind.Unrecognized, null, null);
        }
    }

    /// <summary>
    /// Собирает сообщение об итоге импорта.
    /// </summary>
    public static string? Notice(int imported, string importedKey, int bundle, int rejected)
    {
        var parts = new List<string>();
        if (imported > 0)
        {
            parts.Add(Loc.Instance.Get(importedKey, imported));
        }

        if (bundle > 0)
        {
            parts.Add(Loc.Instance.Get("Drop_BundleHint"));
        }

        if (rejected > 0)
        {
            parts.Add(Loc.Instance.Get("Drop_Skipped", rejected));
        }

        return parts.Count == 0 ? null : string.Join(" · ", parts);
    }

    private static bool LooksLikeImage(byte[] raw)
    {
        if (raw.Length < 4)
        {
            return false;
        }

        // PNG.
        if (raw[0] == 0x89 && raw[1] == 0x50 && raw[2] == 0x4E && raw[3] == 0x47)
        {
            return true;
        }

        // JPEG.
        if (raw[0] == 0xFF && raw[1] == 0xD8)
        {
            return true;
        }

        // BMP.
        if (raw[0] == 0x42 && raw[1] == 0x4D)
        {
            return true;
        }

        // GIF.
        if (raw[0] == 0x47 && raw[1] == 0x49 && raw[2] == 0x46)
        {
            return true;
        }

        return false;
    }

    private static bool LooksLikeBundle(string text)
    {
        return text.Contains("amneziageo-bundle", StringComparison.OrdinalIgnoreCase);
    }

    private static VpnLinkCodec.Imported? TryDecodeQrImage(byte[] raw)
    {
        try
        {
            using var stream = new MemoryStream(raw);
            var text = QrCodec.Decode(stream);
            return text is null ? null : VpnLinkCodec.TryDecodeQr(text);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string? DecodeUtf8(byte[] raw)
    {
        try
        {
            var text = new UTF8Encoding(false, true).GetString(raw);
            return text.TrimStart('﻿');
        }
        catch (Exception)
        {
            return null;
        }
    }
}