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
    /// Downloads a source's file under its unique name and records its metadata. When
    /// <paramref name="progress"/> is supplied it reports the download percent (0-100), or -1 when the
    /// server gives no content length (indeterminate).
    /// </summary>
    public async Task<GeoFileMetadata> UpdateAsync(GeoSource source, IProgress<int>? progress = null, CancellationToken ct = default)
    {
        // Download only when the remote file actually changed: a conditional GET (ETag / Last-Modified
        // captured at the last download) lets the server answer 304, so an unchanged multi-megabyte list
        // is never re-fetched or rewritten. Both the installer's "download-geo" and the app's "Обновить
        // все" run through here, so both are change-only.
        var existing = await store.GetGeoFileAsync(source.Name, ct);
        var fresh = await DownloadAsync(source.Url, existing, progress, ct);
        if (fresh is null)
        {
            return existing!;   // null only when the server confirmed unchanged (existing is non-null)
        }

        var (data, etag, lastModified) = fresh.Value;

        // Validate BEFORE writing anything to disk: a wrong URL (e.g. a GitHub repo page that returns
        // HTML, a 404 body, or a geosite file given as geoip) must never be persisted, or every later
        // index build - list-geo, routing-list materialization - would hit a parse error and the whole
        // geo subsystem would stop working until the source is removed. Parsing here turns a bad source
        // into a clear, isolated failure for just that source.
        var count = CountEntries(source, data);

        var path = TunnelPaths.GeoDataFile(source.Name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllBytesAsync(path, data, ct);

        var sha = Convert.ToHexStringLower(SHA256.HashData(data));
        var metadata = new GeoFileMetadata(source.Name, source.Url, DateTimeOffset.UtcNow, sha, count, etag, lastModified);
        await store.SaveGeoFileAsync(metadata, ct);
        return metadata;
    }

    /// <summary>
    /// Parses the downloaded bytes as the source's declared kind and returns the entry count, throwing a
    /// clear <see cref="InvalidDataException"/> when the content is not a usable v2ray .dat (unparseable,
    /// or zero entries - the typical signature of an HTML page or a 404 body served for a bad URL).
    /// </summary>
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
                $"«{source.Url}» не похож на файл {source.Kind}.dat — не удалось разобрать. Укажите прямую ссылку на .dat (raw), а не на страницу репозитория.", ex);
        }

        if (count == 0)
        {
            throw new InvalidDataException(
                $"«{source.Url}» не содержит ни одной категории {source.Kind}. Похоже, это не .dat-файл — нужна прямая (raw) ссылка на geoip.dat / geosite.dat.");
        }

        return count;
    }

    /// <summary>
    /// Streams the URL into memory, reporting download progress as integer percent (or -1 when the
    /// server sends no Content-Length). Streaming - rather than GetByteArrayAsync - is what lets the UI
    /// show a live percentage for the multi-megabyte geo files. Also returns the response's HTTP
    /// validators (ETag / Last-Modified, empty when absent) so a later update-check can ask the server
    /// whether the file changed without downloading it again.
    /// </summary>
    // A dead/blocked host (e.g. github.com on a censored network) must fail fast, not freeze the row for
    // the OS's ~21 s SYN timeout. Bounds the connect + response-headers phase.
    private static readonly TimeSpan _connectTimeout = TimeSpan.FromSeconds(12);

    // No byte for this long aborts the body transfer. With HttpCompletionOption.ResponseHeadersRead the
    // HttpClient.Timeout stops covering the stream once the headers arrive, so a server that sends
    // headers then goes silent would hang the body read - and the geo-source row's spinner - forever.
    private static readonly TimeSpan _stallTimeout = TimeSpan.FromSeconds(30);

    // Returns null when the server confirms the file is unchanged (304, or a 200 whose validator still
    // matches) so the caller can skip the download entirely.
    private async Task<(byte[] Data, string ETag, string LastModified)?> DownloadAsync(string url, GeoFileMetadata? existing, IProgress<int>? progress, CancellationToken ct)
    {
        // One deadline reused across phases: a short bound for connect/headers, then an INACTIVITY bound
        // for the body that is reset on every chunk below - so a slow but progressing multi-megabyte
        // download is never killed, while a stalled or unreachable source fails fast and lets the row
        // clear so the user can retry.
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
            return null;   // server says unchanged - nothing to download
        }

        response.EnsureSuccessStatusCode();
        var etag = response.Headers.ETag?.ToString() ?? string.Empty;
        // Some servers ignore conditional headers and return 200 even when nothing changed; if the
        // validator still matches what we have, treat it as unchanged and skip the body.
        if (existing is not null && !string.IsNullOrEmpty(existing.ETag) && string.Equals(existing.ETag, etag, StringComparison.Ordinal))
        {
            return null;
        }

        stall.CancelAfter(_stallTimeout);   // headers are in - switch to the body inactivity bound
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
            stall.CancelAfter(_stallTimeout);   // progress arrived - push the inactivity deadline back
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
