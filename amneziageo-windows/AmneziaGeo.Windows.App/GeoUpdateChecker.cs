using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using AmneziaGeo.Decl;

namespace AmneziaGeo.Windows.App;

/// <summary>
/// Decides whether a geo source's remote file changed without re-fetching it.
/// </summary>
internal sealed class GeoUpdateChecker(IStateStore store, HttpClient http)
{
    /// <summary>
    /// Update-check outcome for a single source.
    /// </summary>
    public enum Status
    {
        /// <summary>
        /// Remote file matches the local copy.
        /// </summary>
        UpToDate,

        /// <summary>
        /// Remote file differs or was never downloaded.
        /// </summary>
        Available,

        /// <summary>
        /// Could not determine.
        /// </summary>
        Unknown,
    }

    /// <summary>
    /// Checks one source.
    /// </summary>
    public async Task<Status> CheckAsync(GeoSource source, CancellationToken ct = default)
    {
        var meta = await store.GetGeoFileAsync(source.Name, ct);
        if (meta is null)
        {
            return Status.Available;
        }

        // A clean 304 / matching validator / matching byte length is a reliable "current". A changed validator
        // is NOT proof of new bytes - redirect/CDN ETags rotate on identical content - so a published checksum
        // arbitrates before falling back to the header verdict.
        var localSize = TryLocalSize(source.Name);
        var byHeaders = await CheckHeadersAsync(source.Url, meta, localSize, ct);
        if (byHeaders == Status.UpToDate)
        {
            return Status.UpToDate;
        }

        // Content-based check for sources that ship a .sha256sum next to the data file (the geosite/geoip
        // releases). It suppresses the rotating-ETag false positive without re-downloading the whole file.
        var byChecksum = await CheckChecksumAsync(source.Url, meta, ct);
        if (byChecksum != Status.Unknown)
        {
            return byChecksum;
        }

        // No checksum to arbitrate (a raw host): fall back to the header verdict.
        return byHeaders;
    }

    private async Task<Status> CheckHeadersAsync(string url, GeoFileMetadata meta, long localSize, CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            // Ask for the file verbatim so a reported Content-Length is the raw size, comparable to the stored file.
            request.Headers.AcceptEncoding.Clear();
            request.Headers.AcceptEncoding.ParseAdd("identity");
            if (!string.IsNullOrEmpty(meta.ETag) && EntityTagHeaderValue.TryParse(meta.ETag, out var tag))
            {
                request.Headers.IfNoneMatch.Add(tag);
            }

            if (!string.IsNullOrEmpty(meta.LastModified)
                && DateTimeOffset.TryParse(meta.LastModified, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var since))
            {
                request.Headers.IfModifiedSince = since;
            }

            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            if (response.StatusCode == HttpStatusCode.NotModified)
            {
                return Status.UpToDate;
            }

            if (!response.IsSuccessStatusCode)
            {
                return Status.Unknown;
            }

            // Same byte length as the stored file past a changed validator means unchanged content: geo .dat
            // files change size whenever their category set changes, so an identical length is treated as
            // current. This is what kills the permanent false "update available" on hosts with churning ETags
            // that ship no checksum sidecar. A differing / unknown length falls through to the validators.
            var remoteLen = response.Content.Headers.ContentLength;
            if (localSize > 0 && remoteLen is > 0 && remoteLen.Value == localSize)
            {
                return Status.UpToDate;
            }

            var etag = response.Headers.ETag?.ToString() ?? string.Empty;
            if (!string.IsNullOrEmpty(meta.ETag) && !string.IsNullOrEmpty(etag))
            {
                return string.Equals(NormalizeETag(meta.ETag), NormalizeETag(etag), StringComparison.Ordinal) ? Status.UpToDate : Status.Available;
            }

            var lastModified = response.Content.Headers.LastModified?.ToString("R", CultureInfo.InvariantCulture) ?? string.Empty;
            if (!string.IsNullOrEmpty(meta.LastModified) && !string.IsNullOrEmpty(lastModified))
            {
                return string.Equals(meta.LastModified, lastModified, StringComparison.Ordinal) ? Status.UpToDate : Status.Available;
            }

            return Status.Unknown;
        }
        catch (HttpRequestException)
        {
            return Status.Unknown;
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            return Status.Unknown;
        }
    }

    private async Task<Status> CheckChecksumAsync(string url, GeoFileMetadata meta, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(meta.Sha256))
        {
            return Status.Unknown;
        }

        try
        {
            // Publishers ship a .sha256sum next to the data file; first token is the digest.
            var text = await http.GetStringAsync(url + ".sha256sum", ct);
            var hash = text.Split([' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();

            // Guard against a soft 404: only a real 64-char hex digest is comparable.
            if (hash is not { Length: 64 } || !hash.All(Uri.IsHexDigit))
            {
                return Status.Unknown;
            }

            return string.Equals(hash, meta.Sha256, StringComparison.OrdinalIgnoreCase) ? Status.UpToDate : Status.Available;
        }
        catch (HttpRequestException)
        {
            return Status.Unknown;
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            return Status.Unknown;
        }
    }

    // The stored data file's byte length, or 0 when it is missing / unreadable.
    private static long TryLocalSize(string name)
    {
        try
        {
            var info = new FileInfo(TunnelPaths.GeoDataFile(name));
            return info.Exists ? info.Length : 0;
        }
        catch (IOException)
        {
            return 0;
        }
    }

    // Drops the weak-validator prefix so a strong/weak flip (a gzip negotiation difference) on identical
    // content does not read as a change.
    private static string NormalizeETag(string etag)
    {
        var trimmed = etag.Trim();
        return trimmed.StartsWith("W/", StringComparison.Ordinal) ? trimmed[2..] : trimmed;
    }
}
