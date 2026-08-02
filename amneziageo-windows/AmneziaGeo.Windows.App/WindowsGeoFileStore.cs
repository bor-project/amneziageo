using AmneziaGeo.Decl;

namespace AmneziaGeo.Windows.App;

/// <summary>
/// Machine-root storage for downloaded geo database files.
/// </summary>
internal sealed class WindowsGeoFileStore : IGeoFileStore
{
    /// <inheritdoc/>
    public byte[]? Read(string name)
    {
        var path = TunnelPaths.GeoDataFile(name);
        return File.Exists(path) ? File.ReadAllBytes(path) : null;
    }

    /// <inheritdoc/>
    public async Task WriteAsync(string name, byte[] data, CancellationToken ct = default)
    {
        var path = TunnelPaths.GeoDataFile(name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllBytesAsync(path, data, ct).ConfigureAwait(false);
    }
}
