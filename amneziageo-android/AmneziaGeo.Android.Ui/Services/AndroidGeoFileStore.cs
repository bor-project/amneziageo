using AmneziaGeo.Decl;

namespace AmneziaGeo.Android.Ui.Services;

/// <summary>
/// FilesDir-backed storage for downloaded geo database files.
/// </summary>
internal sealed class AndroidGeoFileStore(string directory) : IGeoFileStore
{
    /// <inheritdoc/>
    public byte[]? Read(string name)
    {
        var path = PathFor(name);
        return System.IO.File.Exists(path) ? System.IO.File.ReadAllBytes(path) : null;
    }

    /// <inheritdoc/>
    public Stream? OpenRead(string name)
    {
        var path = PathFor(name);
        return System.IO.File.Exists(path) ? System.IO.File.OpenRead(path) : null;
    }

    /// <inheritdoc/>
    public async Task WriteAsync(string name, byte[] data, CancellationToken ct = default)
    {
        System.IO.Directory.CreateDirectory(directory);
        await System.IO.File.WriteAllBytesAsync(PathFor(name), data, ct).ConfigureAwait(false);
    }

    private string PathFor(string name) => System.IO.Path.Combine(directory, $"{name}.dat");
}
