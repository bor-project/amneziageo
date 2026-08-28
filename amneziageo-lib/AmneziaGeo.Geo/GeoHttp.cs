using System.Collections.Concurrent;
using System.Net.Http;
using System.Security.Authentication;

using Microsoft.Extensions.Logging;

namespace AmneziaGeo.Geo;

/// <summary>
/// HTTP for geo sources. A request the machine refuses over its certificate is repeated without verifying
/// the server, so a host whose certificate store or clock is out of date still receives the rule databases.
/// A subscription is not such a source: it carries private keys, so it goes through <see cref="SendVerifiedAsync"/>
/// and a rejected certificate stays an error there. Downloads of the application setup deliberately do not go
/// through here either.
/// </summary>
public sealed class GeoHttp(HttpClient http, ILogger<GeoHttp> logger) : IDisposable
{
    private readonly Lazy<HttpClient> _unverified = new(CreateUnverified);
    private readonly ConcurrentDictionary<string, byte> _reported = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Sends a request, repeating it unverified when the certificate is rejected.
    /// </summary>
    public async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, HttpCompletionOption completion, CancellationToken ct)
    {
        try
        {
            return await http.SendAsync(request, completion, ct);
        }
        catch (HttpRequestException ex) when (IsCertificateFailure(ex))
        {
            Report(request.RequestUri?.ToString(), ex);
            return await _unverified.Value.SendAsync(Clone(request), completion, ct);
        }
    }

    /// <summary>
    /// Sends a request whose answer has to be proven: a rejected certificate stays an error.
    /// </summary>
    public Task<HttpResponseMessage> SendVerifiedAsync(HttpRequestMessage request, HttpCompletionOption completion, CancellationToken ct)
    {
        return http.SendAsync(request, completion, ct);
    }

    /// <summary>
    /// Downloads a small text file, repeating it unverified when the certificate is rejected.
    /// </summary>
    public async Task<string> GetStringAsync(string url, CancellationToken ct)
    {
        try
        {
            return await http.GetStringAsync(url, ct);
        }
        catch (HttpRequestException ex) when (IsCertificateFailure(ex))
        {
            Report(url, ex);
            return await _unverified.Value.GetStringAsync(url, ct);
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_unverified.IsValueCreated)
        {
            _unverified.Value.Dispose();
        }
    }

    // Tells a rejected certificate apart from a dead host or a refused port: only the former is worth
    // retrying without verification, and a network failure retried that way would just fail twice.
    private static bool IsCertificateFailure(HttpRequestException ex)
    {
        return ex.HttpRequestError == HttpRequestError.SecureConnectionError || ex.InnerException is AuthenticationException;
    }

    private static HttpClient CreateUnverified()
    {
        var handler = new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator,
        };

        return new HttpClient(handler);
    }

    // A request that was already sent cannot be sent again; geo requests carry headers only, no body.
    private static HttpRequestMessage Clone(HttpRequestMessage request)
    {
        var clone = new HttpRequestMessage(request.Method, request.RequestUri)
        {
            Version = request.Version,
            VersionPolicy = request.VersionPolicy,
        };

        foreach (var header in request.Headers)
        {
            clone.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        return clone;
    }

    // Once per host: the check runs on a timer, and a line per request would bury the rest of the log.
    private void Report(string? url, Exception ex)
    {
        var host = Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.Host : url ?? "?";
        if (!_reported.TryAdd(host, 0))
        {
            return;
        }

        logger.LogWarning(
            ex,
            "this machine rejected the certificate of {Host}; the rule database is taken from it anyway, with no proof of who served it - a substituted list would pass unnoticed, so check the clock and the root certificates here",
            host);
    }
}
