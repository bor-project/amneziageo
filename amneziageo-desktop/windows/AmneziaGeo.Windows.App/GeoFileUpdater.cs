using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
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
    /// Downloads a source file and records its metadata.
    /// </summary>
    public async Task<GeoFileMetadata> UpdateAsync(GeoSource source, IProgress<int>? progress = null, CancellationToken ct = default)
    {
        var existing = await store.GetGeoFileAsync(source.Name, ct);
        var fresh = await DownloadAsync(source.Url, existing, progress, ct);
        if (fresh is null)
        {
            return existing!;
        }

        var (data, etag, lastModified) = fresh.Value;

        var count = CountEntries(source, data);

        var path = TunnelPaths.GeoDataFile(source.Name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllBytesAsync(path, data, ct);

        var sha = Convert.ToHexStringLower(SHA256.HashData(data));
        var metadata = new GeoFileMetadata(source.Name, source.Url, DateTimeOffset.UtcNow, sha, count, etag, lastModified);
        await store.SaveGeoFileAsync(metadata, ct);
        return metadata;
    }

    private static int CountEntries(GeoSource source, byte[] data)
    {
        var isGeoip = source.Kind.Equals("geoip", StringComparison.OrdinalIgnoreCase);
        int count;
        try
        {
            count = isGeoip ? GeoIpDatabase.Countries(data).Count : GeoSiteDatabase.Categories(data).Count;
        }
        catch (Exception ex) when (ex is InvalidDataException or ArgumentException or FormatException)
        {
            throw new InvalidDataException(
                $"«{source.Url}» не похож на файл {source.Kind}.dat - не удалось разобрать. Укажите прямую ссылку на .dat (raw), а не на страницу репозитория.", ex);
        }

        if (count == 0)
        {
            throw new InvalidDataException(
                $"«{source.Url}» не содержит ни одной категории {source.Kind}. Похоже, это не .dat-файл - нужна прямая (raw) ссылка на geoip.dat / geosite.dat.");
        }

        return count;
    }

    // Dead host must fail fast, not hang on the OS SYN timeout.
    private static readonly TimeSpan _connectTimeout = TimeSpan.FromSeconds(12);

    // Body-stall bound: aborts when no byte arrives for this long.
    private static readonly TimeSpan _stallTimeout = TimeSpan.FromSeconds(30);

    private async Task<(byte[] Data, string ETag, string LastModified)?> DownloadAsync(string url, GeoFileMetadata? existing, IProgress<int>? progress, CancellationToken ct)
    {
        using var stall = CancellationTokenSource.CreateLinkedTokenSource(ct);
        stall.CancelAfter(_connectTimeout);

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (existing is not null)
        {
            if (!string.IsNullOrEmpty(existing.ETag) && EntityTagHeaderValue.TryParse(existing.ETag, out var tag))
            {
                request.Headers.IfNoneMatch.Add(tag);
            }

            if (!string.IsNullOrEmpty(existing.LastModified)
                && DateTimeOffset.TryParse(existing.LastModified, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var since))
            {
                request.Headers.IfModifiedSince = since;
            }
        }

        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, stall.Token);
        if (response.StatusCode == HttpStatusCode.NotModified && existing is not null)
        {
            return null;
        }

        response.EnsureSuccessStatusCode();
        var etag = response.Headers.ETag?.ToString() ?? string.Empty;
        if (existing is not null && !string.IsNullOrEmpty(existing.ETag) && string.Equals(existing.ETag, etag, StringComparison.Ordinal))
        {
            return null;
        }

        stall.CancelAfter(_stallTimeout);
        var total = response.Content.Headers.ContentLength;
        var lastModified = response.Content.Headers.LastModified?.ToString("R", CultureInfo.InvariantCulture) ?? string.Empty;

        using var source = await response.Content.ReadAsStreamAsync(stall.Token);
        using var buffer = new MemoryStream(total is > 0 and < int.MaxValue ? (int)total : 0);
        var chunk = new byte[81920];
        long read = 0;
        var lastPercent = -1;
        int n;
        while ((n = await source.ReadAsync(chunk, stall.Token)) > 0)
        {
            stall.CancelAfter(_stallTimeout);
            await buffer.WriteAsync(chunk.AsMemory(0, n), stall.Token);
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
