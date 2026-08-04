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
        if (FileSaverHost.SaveAsync(title, suggestedName) is { } builtIn)
        {
            var picked = await builtIn;
            if (picked is null)
            {
                return false;
            }

            await File.WriteAllTextAsync(picked, text);
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

        await using var stream = await file.OpenWriteAsync();
        await using var writer = new StreamWriter(stream);
        await writer.WriteAsync(text);
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

            await using var target = File.Create(picked);
            write(target);
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
}
