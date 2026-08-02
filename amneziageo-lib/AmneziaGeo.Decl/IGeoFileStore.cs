namespace AmneziaGeo.Decl;

/// <summary>
/// Platform storage for downloaded geo database files (geoip/geosite .dat).
/// </summary>
public interface IGeoFileStore
{
    /// <summary>
    /// Returns the stored file bytes for a source name, or null when absent.
    /// </summary>
    byte[]? Read(string name);

    /// <summary>
    /// Writes downloaded file bytes for a source name.
    /// </summary>
    Task WriteAsync(string name, byte[] data, CancellationToken ct = default);
}
