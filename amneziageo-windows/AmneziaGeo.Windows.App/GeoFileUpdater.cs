using System.Globalization;
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
    /// Downloads a source's file under its unique name and records its metadata. When
    /// <paramref name="progress"/> is supplied it reports the download percent (0-100), or -1 when the
    /// server gives no content length (indeterminate).
    /// </summary>
    public async Task<GeoFileMetadata> UpdateAsync(GeoSource source, IProgress<int>? progress = null, CancellationToken ct = default)
    {
        var (data, etag, lastModified) = await DownloadAsync(source.Url, progress, ct);

        var path = TunnelPaths.GeoDataFile(source.Name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllBytesAsync(path, data, ct);

        var sha = Convert.ToHexStringLower(SHA256.HashData(data));
        var count = source.Kind.Equals("geoip", StringComparison.OrdinalIgnoreCase)
            ? GeoIpDatabase.Countries(data).Count
            : GeoSiteDatabase.Categories(data).Count;

        var metadata = new GeoFileMetadata(source.Name, source.Url, DateTimeOffset.UtcNow, sha, count, etag, lastModified);
        await store.SaveGeoFileAsync(metadata, ct);
        return metadata;
    }

    /// <summary>
    /// Streams the URL into memory, reporting download progress as integer percent (or -1 when the
    /// server sends no Content-Length). Streaming — rather than GetByteArrayAsync — is what lets the UI
    /// show a live percentage for the multi-megabyte geo files. Also returns the response's HTTP
    /// validators (ETag / Last-Modified, empty when absent) so a later update-check can ask the server
    /// whether the file changed without downloading it again.
    /// </summary>
    private async Task<(byte[] Data, string ETag, string LastModified)> DownloadAsync(string url, IProgress<int>? progress, CancellationToken ct)
    {
        using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        var total = response.Content.Headers.ContentLength;
        var etag = response.Headers.ETag?.ToString() ?? string.Empty;
        var lastModified = response.Content.Headers.LastModified?.ToString("R", CultureInfo.InvariantCulture) ?? string.Empty;

        using var source = await response.Content.ReadAsStreamAsync(ct);
        using var buffer = new MemoryStream(total is > 0 and < int.MaxValue ? (int)total : 0);
        var chunk = new byte[81920];
        long read = 0;
        var lastPercent = -1;
        int n;
        while ((n = await source.ReadAsync(chunk, ct)) > 0)
        {
            await buffer.WriteAsync(chunk.AsMemory(0, n), ct);
            read += n;
            if (total is > 0)
            {
                var percent = (int)(read * 100 / total.Value);
                if (percent != lastPercent)
                {
                    lastPercent = percent;
                    progress?.Report(percent);
                }
            }
            else
            {
                progress?.Report(-1);
            }
        }

        return (buffer.ToArray(), etag, lastModified);
    }
}
