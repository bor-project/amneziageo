using System.Net;
using System.Net.Http.Headers;

namespace AmneziaGeo.Decl;

/// <summary>
/// Downloads a file of a release, carrying on from where an earlier attempt broke off.
/// </summary>
public static class UpdateDownload
{
    private static readonly TimeSpan StallLimit = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Downloads the address into the path through the partial file beside it and keeps the partial when the download
    /// breaks off, so the next attempt asks only for the rest. Progress hears the bytes on disk and the whole size.
    /// </summary>
    public static async Task DownloadAsync(HttpClient http, Uri address, string path, Action<long, long>? progress, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(http);

        var partial = path + ".part";
        if (!await TryAsync(http, address, partial, Length(partial), progress, ct).ConfigureAwait(false))
        {
            File.Delete(partial);
            await TryAsync(http, address, partial, 0, progress, ct).ConfigureAwait(false);
        }

        File.Move(partial, path, overwrite: true);
    }

    // One request for the file or for the rest of it; false when the leftover is not the start of this file.
    private static async Task<bool> TryAsync(
        HttpClient http,
        Uri address,
        string partial,
        long have,
        Action<long, long>? progress,
        CancellationToken ct)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(StallLimit);
        using var request = new HttpRequestMessage(HttpMethod.Get, address);
        if (have > 0)
        {
            request.Headers.Range = new RangeHeaderValue(have, null);
        }

        try
        {
            using var response = await http
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, limit.Token)
                .ConfigureAwait(false);
            var resumed = have > 0 && response.StatusCode == HttpStatusCode.PartialContent;
            if (have > 0 && (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable
                || (resumed && response.Content.Headers.ContentRange?.From != have)))
            {
                return false;
            }

            if (!resumed)
            {
                response.EnsureSuccessStatusCode();
                have = 0;
            }

            var total = resumed
                ? response.Content.Headers.ContentRange?.Length ?? -1
                : response.Content.Headers.ContentLength ?? -1;
            var written = have;
            using (var source = await response.Content.ReadAsStreamAsync(limit.Token).ConfigureAwait(false))
            using (var target = new FileStream(partial, resumed ? FileMode.Append : FileMode.Create, FileAccess.Write))
            {
                var buffer = new byte[81920];
                var read = 0;
                while ((read = await source.ReadAsync(buffer, limit.Token).ConfigureAwait(false)) > 0)
                {
                    limit.CancelAfter(StallLimit);
                    await target.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                    written += read;
                    progress?.Invoke(written, total);
                }
            }

            if (total >= 0 && written != total)
            {
                throw new IOException($"{address} ended at {written} of {total} bytes");
            }

            return true;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new IOException($"{address} sent nothing for {StallLimit.TotalSeconds} seconds");
        }
    }

    private static long Length(string path)
    {
        var file = new FileInfo(path);
        return file.Exists ? file.Length : 0;
    }
}
