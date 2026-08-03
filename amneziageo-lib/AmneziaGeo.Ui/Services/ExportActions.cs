using System;
using System.IO;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform.Storage;

namespace AmneziaGeo.Ui.Services;

/// <summary>
/// Копирование payload в буфер и сохранение в файл для экранов экспорта.
/// </summary>
internal static class ExportActions
{
    public static async Task<bool> CopyToClipboardAsync(Visual source, string text)
    {
        var clipboard = TopLevel.GetTopLevel(source)?.Clipboard;
        if (clipboard is null)
        {
            return false;
        }

        await clipboard.SetTextAsync(text);
        return true;
    }

    public static async Task<bool> SaveTextAsync(Visual source, string text, string title, string suggestedName)
    {
        if (TopLevel.GetTopLevel(source) is not { } top)
        {
            return false;
        }

        var file = await top.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = title,
            SuggestedFileName = suggestedName,
        });
        if (file is null)
        {
            return false;
        }

        await using (var stream = await file.OpenWriteAsync())
        {
            await using var writer = new StreamWriter(stream);
            await writer.WriteAsync(text);
        }

        RestrictToOwner(file);
        return true;
    }

    /// <summary>
    /// Ограничивает доступ к сохранённому файлу владельцем: выгрузки несут приватные ключи.
    /// </summary>
    public static void RestrictToOwner(IStorageFile file)
    {
        if (OperatingSystem.IsWindows() || file.TryGetLocalPath() is not { } path)
        {
            return;
        }

        try
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        catch (Exception)
        {
            // Не всякий выданный пикером путь принадлежит нам.
        }
    }
}
