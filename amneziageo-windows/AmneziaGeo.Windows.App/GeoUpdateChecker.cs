using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using AmneziaGeo.Decl;

namespace AmneziaGeo.Windows.App;

/// <summary>
/// Decides whether a geo source's remote file changed since it was last downloaded, WITHOUT re-fetching
/// the multi-megabyte file. It issues a conditional GET using the validators captured at download time
/// (<see cref="GeoFileMetadata.ETag"/> / <see cref="GeoFileMetadata.LastModified"/>): a 304 means
/// up-to-date, and even a 200 only pulls the response headers (ResponseHeadersRead + dispose) so the
/// fresh validators can be compared without downloading the body. When the server offers no usable
/// validators it falls back to a companion ".sha256sum" file (tiny) compared against the stored hash.
/// </summary>
internal sealed class GeoUpdateChecker(IStateStore store, HttpClient http)
{
    /// <summary>The outcome of an update-check for a single source.</summary>
    public enum Status
    {
        /// <summary>The remote file matches what we have.</summary>
        UpToDate,

        /// <summary>The remote file differs (or was never downloaded) — a download would change it.</summary>
        Available,

        /// <summary>Could not determine (network error, or the server offered nothing to compare).</summary>
        Unknown,
    }

    /// <summary>
    /// Checks one source. Returns <see cref="Status.Available"/> when it has never been downloaded.
    /// </summary>
    public async Task<Status> CheckAsync(GeoSource source, CancellationToken ct = default)
    {
        var meta = await store.GetGeoFileAsync(source.Name, ct);
        if (meta is null)
        {
            // Never downloaded — the initial download is the "update" that is available.
            return Status.Available;
        }

        var byHeaders = await CheckHeadersAsync(source.Url, meta, ct);
        if (byHeaders != Status.Unknown)
        {
            return byHeaders;
        }

        return await CheckChecksumAsync(source.Url, meta, ct);
    }

    private async Task<Status> CheckHeadersAsync(string url, GeoFileMetadata meta, CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            if (!string.IsNullOrEmpty(meta.ETag) && EntityTagHeaderValue.TryParse(meta.ETag, out var tag))
            {
                request.Headers.IfNoneMatch.Add(tag);
            }

            if (!string.IsNullOrEmpty(meta.LastModified)
                && DateTimeOffset.TryParse(meta.LastModified, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var since))
            {
                request.Headers.IfModifiedSince = since;
            }

            // ResponseHeadersRead + dispose-without-reading: even a 200 pulls only the headers, never the
            // multi-megabyte body, so the check stays lightweight whichever way the server answers.
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            if (response.StatusCode == HttpStatusCode.NotModified)
            {
                return Status.UpToDate;
            }

            if (!response.IsSuccessStatusCode)
            {
                return Status.Unknown;
            }

            // 200: the server ignored the conditional headers (or they were dropped across a redirect).
            // Compare the freshly-returned validators against the stored ones instead.
            var etag = response.Headers.ETag?.ToString() ?? string.Empty;
            if (!string.IsNullOrEmpty(meta.ETag) && !string.IsNullOrEmpty(etag))
            {
                return string.Equals(meta.ETag, etag, StringComparison.Ordinal) ? Status.UpToDate : Status.Available;
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
            // Loyalsoldier (and many geo publishers) ship "<file>.sha256sum" next to the data file. Its
            // first token is the hex digest of the file we would download.
            var text = await http.GetStringAsync(url + ".sha256sum", ct);
            var hash = text.Split([' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();

            // Guard against a "soft 404" — an HTTP 200 carrying an HTML error / captive-portal page rather
            // than the checksum. Only a real 64-char hex digest is comparable; anything else means "can't
            // tell" (Unknown), never a false "update available".
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
}
