using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform.Storage;

namespace AmneziaGeo.Ui.Services;

/// <summary>
/// A file chosen by the system picker or by the built-in browser.
/// </summary>
internal sealed class PickedFile
{
    private readonly IStorageFile? _file;
    private readonly string? _path;

    /// <summary>
    /// ctor
    /// </summary>
    public PickedFile(IStorageFile file)
    {
        _file = file;
        Name = file.Name;
    }

    /// <summary>
    /// ctor
    /// </summary>
    public PickedFile(string path)
    {
        _path = path;
        Name = Path.GetFileName(path);
    }

    /// <summary>
    /// File name with extension.
    /// </summary>
    public string Name { get; }

    /// <summary>
    /// Opens the contents for reading.
    /// </summary>
    public async Task<Stream> OpenReadAsync()
    {
        if (_file is not null)
        {
            return await _file.OpenReadAsync();
        }

        return File.OpenRead(_path!);
    }
}

/// <summary>
/// Opens a file through the system picker, or through the built-in browser on platforms that have none.
/// </summary>
internal static class FilePickers
{
    /// <summary>
    /// Asks for one file matching the extensions; returns null when the user backs out.
    /// </summary>
    public static async Task<PickedFile?> OpenAsync(Visual owner, string title, params string[] extensions)
    {
        if (FileBrowserHost.BrowseAsync(title, extensions) is { } browse)
        {
            var path = await browse;
            return path is null ? null : new PickedFile(path);
        }

        if (TopLevel.GetTopLevel(owner) is not { } top)
        {
            return null;
        }

        var patterns = extensions.Select(ext => $"*.{ext}").ToList();
        var files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType(title) { Patterns = patterns }],
        });
        return files.Count > 0 ? new PickedFile(files[0]) : null;
    }
}
