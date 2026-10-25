using System.Collections.Concurrent;
using System.Net.Http;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

using Microsoft.Extensions.Logging;

namespace AmneziaGeo.Geo;

/// <summary>
/// HTTP for geo sources and subscriptions. A certificate the machine refuses is taken when it names the host asked and
/// chains to a root the client carries; any other stays an error. Downloads of the application setup deliberately do
/// not go through here.
/// </summary>
/// <param name="http">The client of the head.</param>
/// <param name="logger">Where a rejected certificate of a geo source is told.</param>
/// <param name="connect">How the connections of the proven requests are opened; the client of the head sends them when null.</param>
/// <param name="roots">The roots a proven request is taken under beside the store of the machine; the ones the client carries when null.</param>
public sealed class GeoHttp(
    HttpClient http,
    ILogger<GeoHttp> logger,
    Func<SocketsHttpConnectionContext, CancellationToken, ValueTask<Stream>>? connect = null,
    X509Certificate2Collection? roots = null) : IDisposable
{
    private readonly Func<SocketsHttpConnectionContext, CancellationToken, ValueTask<Stream>>? _connect = connect;
    private readonly X509Certificate2Collection _roots = roots ?? CarriedRoots.Isrg;
    private readonly ConcurrentDictionary<string, byte> _reported = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, HttpClient> _proven = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Sends a request, repeating it under the roots the client carries when the machine rejects the certificate.
    /// </summary>
    public async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, HttpCompletionOption completion, CancellationToken ct)
    {
        try
        {
            return await http.SendAsync(request, completion, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException ex) when (IsCertificateFailure(ex))
        {
            Report(request.RequestUri?.ToString(), ex);
            return await Proven(string.Empty).SendAsync(Clone(request), completion, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Sends a request whose answer has to be proven: a certificate neither the machine nor a root the client carries
    /// proves stays an error.
    /// </summary>
    public async Task<HttpResponseMessage> SendVerifiedAsync(HttpRequestMessage request, HttpCompletionOption completion, CancellationToken ct)
    {
        if (_connect is not null)
        {
            return await Proven(string.Empty).SendAsync(request, completion, ct).ConfigureAwait(false);
        }

        try
        {
            return await http.SendAsync(request, completion, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException ex) when (IsCertificateFailure(ex))
        {
            return await Proven(string.Empty).SendAsync(Clone(request), completion, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Sends a request whose answer has to be proven, taking as well the certificate whose SHA-256 is pinned.
    /// </summary>
    public Task<HttpResponseMessage> SendPinnedAsync(HttpRequestMessage request, string pin, HttpCompletionOption completion, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(pin))
        {
            return SendVerifiedAsync(request, completion, ct);
        }

        return Proven(pin).SendAsync(request, completion, ct);
    }

    /// <summary>
    /// Downloads a small text file, repeating it under the roots the client carries when the machine rejects the certificate.
    /// </summary>
    public async Task<string> GetStringAsync(string url, CancellationToken ct)
    {
        try
        {
            return await http.GetStringAsync(url, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException ex) when (IsCertificateFailure(ex))
        {
            Report(url, ex);
            return await Proven(string.Empty).GetStringAsync(url, ct).ConfigureAwait(false);
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        foreach (var client in _proven.Values)
        {
            client.Dispose();
        }
    }

    // Tells a rejected certificate apart from a dead host or a refused port: only the former is worth
    // retrying under the carried roots, and a network failure retried that way would just fail twice.
    private static bool IsCertificateFailure(HttpRequestException ex)
    {
        return ex.HttpRequestError == HttpRequestError.SecureConnectionError || ex.InnerException is AuthenticationException;
    }

    // The client of the proven requests under a pin, or under none.
    private HttpClient Proven(string pin) => _proven.GetOrAdd(pin, CreateProven);

    // Takes a certificate the machine proves, one that chains to a root the client carries, or the one whose SHA-256
    // the server of the config named.
    private HttpClient CreateProven(string pin)
    {
        var handler = new SocketsHttpHandler { UseProxy = false };
        if (_connect is not null)
        {
            handler.ConnectCallback = _connect;
        }

        handler.SslOptions.RemoteCertificateValidationCallback = (_, certificate, chain, errors) =>
            errors == SslPolicyErrors.None
            || (pin.Length > 0 && certificate is not null && Pinned(certificate, pin))
            || CarriedRoots.Prove(certificate, chain, errors, _roots);

        return new HttpClient(handler);
    }

    private static bool Pinned(X509Certificate certificate, string pin)
    {
        return string.Equals(Convert.ToHexStringLower(SHA256.HashData(certificate.GetRawCertData())), pin, StringComparison.OrdinalIgnoreCase);
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
            "this machine rejected the certificate of {Host}; it is asked again under the roots the client carries, so check the clock and the root certificates here",
            host);
    }
}
