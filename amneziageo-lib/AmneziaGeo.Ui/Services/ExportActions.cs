using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media.Imaging;
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
        if (FileSaverHost.SaveAsync(title, suggestedName) is { } builtIn)
        {
            var picked = await builtIn;
            if (picked is null)
            {
                return false;
            }

            await File.WriteAllTextAsync(picked, text);
            RestrictToOwner(picked);
            return true;
        }

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
    /// Writes binary content to a file the user picks.
    /// </summary>
    public static async Task<bool> SaveBinaryAsync(
        Visual source,
        Action<Stream> write,
        string title,
        string suggestedName,
        string extension,
        string typeName)
    {
        if (FileSaverHost.SaveAsync(title, suggestedName) is { } builtIn)
        {
            var picked = await builtIn;
            if (picked is null)
            {
                return false;
            }

            await using (var target = File.Create(picked))
            {
                write(target);
            }

            RestrictToOwner(picked);
            return true;
        }

        if (TopLevel.GetTopLevel(source) is not { } top)
        {
            return false;
        }

        var file = await top.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = title,
            SuggestedFileName = suggestedName,
            DefaultExtension = extension,
            FileTypeChoices = [new FilePickerFileType(typeName) { Patterns = [$"*.{extension}"] }],
        });
        if (file is null)
        {
            return false;
        }

        await using var stream = await file.OpenWriteAsync();
        write(stream);
        return true;
    }

    /// <summary>
    /// Puts a picture on the clipboard, through the platform hook where the toolkit clipboard carries text only.
    /// </summary>
    public static async Task<bool> CopyImageAsync(Visual source, Bitmap image, string name)
    {
        if (PlatformExportHost.CopyImageAsync(name, ToPng(image)) is { } platform)
        {
            return await platform;
        }

        if (TopLevel.GetTopLevel(source)?.Clipboard is not { } clipboard)
        {
            return false;
        }

        var item = new DataTransferItem();
        item.SetBitmap(image);
        var transfer = new DataTransfer();
        transfer.Add(item);
        try
        {
            await clipboard.SetDataAsync(transfer);
            return true;
        }
        catch (Exception)
        {
            // Не всякий буфер обмена принимает картинку.
            return false;
        }
    }

    /// <summary>
    /// Hands text to another application.
    /// </summary>
    public static Task<bool> SendTextAsync(string text, string name)
        => PlatformExportHost.SendAsync(name, "text/plain", Encoding.UTF8.GetBytes(text)) ?? Task.FromResult(false);

    /// <summary>
    /// Hands a picture to another application.
    /// </summary>
    public static Task<bool> SendImageAsync(Bitmap image, string name)
        => PlatformExportHost.SendAsync(name, "image/png", ToPng(image)) ?? Task.FromResult(false);

    // Encodes the bitmap as PNG bytes.
    private static byte[] ToPng(Bitmap image)
    {
        using var buffer = new MemoryStream();
        image.Save(buffer);
        return buffer.ToArray();
    }

    /// <summary>
    /// Asks for a save path on disk; returns null when the user backs out or the target has no local path.
    /// </summary>
    public static async Task<string?> PickSavePathAsync(
        Visual source,
        string title,
        string suggestedName,
        string extension,
        string typeName)
    {
        if (FileSaverHost.SaveAsync(title, suggestedName) is { } builtIn)
        {
            return await builtIn;
        }

        if (TopLevel.GetTopLevel(source) is not { } top)
        {
            return null;
        }

        var file = await top.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = title,
            SuggestedFileName = suggestedName,
            DefaultExtension = extension,
            FileTypeChoices = [new FilePickerFileType(typeName) { Patterns = [$"*.{extension}"] }],
        });
        return file?.TryGetLocalPath();
    }

    /// <summary>
    /// Ограничивает доступ к сохранённому файлу владельцем: выгрузки несут приватные ключи.
    /// </summary>
    public static void RestrictToOwner(IStorageFile file)
    {
        if (file.TryGetLocalPath() is { } path)
        {
            RestrictToOwner(path);
        }
    }

    /// <summary>
    /// Ограничивает доступ к файлу по пути владельцем.
    /// </summary>
    public static void RestrictToOwner(string path)
    {
        if (OperatingSystem.IsWindows())
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
