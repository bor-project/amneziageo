using AmneziaGeo.Decl;

namespace AmneziaGeo.Linux.App;

/// <summary>
/// Directory-backed storage for the downloaded geo database files.
/// </summary>
internal sealed class LinuxGeoFileStore(string directory) : IGeoFileStore
{
    /// <inheritdoc/>
    public byte[]? Read(string name)
    {
        var path = PathFor(name);
        return File.Exists(path) ? File.ReadAllBytes(path) : null;
    }

    /// <inheritdoc/>
    public async Task WriteAsync(string name, byte[] data, CancellationToken ct = default)
    {
        Directory.CreateDirectory(directory);
        await File.WriteAllBytesAsync(PathFor(name), data, ct).ConfigureAwait(false);
    }

    private string PathFor(string name) => Path.Combine(directory, $"{name}.dat");
}
