using System.Globalization;
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

            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            if (response.StatusCode == HttpStatusCode.NotModified)
            {
                return Status.UpToDate;
            }

            if (!response.IsSuccessStatusCode)
            {
                return Status.Unknown;
            }

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
}
