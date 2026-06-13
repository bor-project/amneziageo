using System.Net.Http;
using System.Security.Cryptography;
using AmneziaGeo.Decl;
using AmneziaGeo.Geo;

namespace AmneziaGeo.Windows.App;

/// <summary>
/// Downloads geo database files and records their update metadata.
/// </summary>
internal sealed class GeoFileUpdater(IStateStore store)
{
    private static readonly HttpClient _http = new();

    /// <summary>
    /// Downloads a geo file, stores it on disk, and records its metadata.
    /// </summary>
    public async Task<GeoFileMetadata> UpdateAsync(string kind, string url, CancellationToken ct = default)
    {
        var data = await _http.GetByteArrayAsync(url, ct);

        var path = TunnelPaths.GeoDataFile(kind);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllBytesAsync(path, data, ct);

        var sha = Convert.ToHexStringLower(SHA256.HashData(data));
        var count = kind.Equals("geoip", StringComparison.OrdinalIgnoreCase)
            ? GeoIpDatabase.Countries(data).Count
            : GeoSiteDatabase.Categories(data).Count;

        var metadata = new GeoFileMetadata(kind, url, DateTimeOffset.UtcNow, sha, count);
        await store.SaveGeoFileAsync(metadata, ct);
        return metadata;
    }
}
