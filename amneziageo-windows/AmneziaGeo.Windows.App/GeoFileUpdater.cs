using System.Net.Http;
using System.Security.Cryptography;
using AmneziaGeo.Decl;
using AmneziaGeo.Geo;

namespace AmneziaGeo.Windows.App;

/// <summary>
/// Downloads geo source files and records their update metadata.
/// </summary>
internal sealed class GeoFileUpdater(IStateStore store, HttpClient http)
{
    /// <summary>
    /// Downloads a source's file under its unique name and records its metadata.
    /// </summary>
    public async Task<GeoFileMetadata> UpdateAsync(GeoSource source, CancellationToken ct = default)
    {
        var data = await http.GetByteArrayAsync(source.Url, ct);

        var path = TunnelPaths.GeoDataFile(source.Name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllBytesAsync(path, data, ct);

        var sha = Convert.ToHexStringLower(SHA256.HashData(data));
        var count = source.Kind.Equals("geoip", StringComparison.OrdinalIgnoreCase)
            ? GeoIpDatabase.Countries(data).Count
            : GeoSiteDatabase.Categories(data).Count;

        var metadata = new GeoFileMetadata(source.Name, source.Url, DateTimeOffset.UtcNow, sha, count);
        await store.SaveGeoFileAsync(metadata, ct);
        return metadata;
    }
}
