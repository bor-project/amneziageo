using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Security;
using System.Security.Cryptography;
using System.Text.Json;
using AmneziaGeo.Decl;

namespace AmneziaGeo.Ipc;

internal static class HttpGetProbeClient
{
    internal static async Task<XHttpProbeClient.Response> SendAsync(OfferedProxy proxy, string nonce,
        Func<DnsEndPoint, CancellationToken, ValueTask<Stream>> connect, CancellationToken ct, ProxyDestination? target)
    {
        if (!OfferedProxyCatalog.Valid(proxy) || !ProxyTransportModes.IsHttp(proxy))
        {
            throw new InvalidDataException("The HTTP proxy protocol is invalid.");
        }

        using var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseProxy = false,
            UseCookies = false,
            AutomaticDecompression = DecompressionMethods.None,
            MaxResponseHeadersLength = 16,
            ConnectCallback = async (_, token) =>
            {
                using var pending = CancellationTokenSource.CreateLinkedTokenSource(token, ct);
                var connection = await connect(new DnsEndPoint(proxy.Host, proxy.Port), pending.Token).ConfigureAwait(false);
                var tls = new SslStream(connection, leaveInnerStreamOpen: false);
                try
                {
                    var options = XHttpProbeClient.TlsOptions(proxy);
                    var tlsFailure = "";
                    options.RemoteCertificateValidationCallback = (_, certificate, chain, errors) =>
                    {
                        tlsFailure = ProxyTls.Failure(certificate, chain, errors, proxy.Tls.Pin, proxy.Tls.ServerName);
                        return tlsFailure.Length == 0;
                    };
                    options.ApplicationProtocols = [SslApplicationProtocol.Http11];
                    try
                    {
                        await tls.AuthenticateAsClientAsync(options, pending.Token).ConfigureAwait(false);
                    }
                    catch (System.Security.Authentication.AuthenticationException ex)
                    {
                        throw new TransportProxyFault("proxy-tls", new("tls", tlsFailure.Length > 0 ? tlsFailure : "handshake"), ex);
                    }
                    return tls;
                }
                catch
                {
                    await tls.DisposeAsync().ConfigureAwait(false);
                    throw;
                }
            },
        };
        using var client = new HttpClient(handler);
        var auth = new HttpGetAuthentication(proxy.Auth.Credential);
        var basePath = "/" + proxy.Path + "/";
        var challengeNonce = RandomNumberGenerator.GetBytes(16);
        var challengeQuery = new Dictionary<string, string>
        {
            ["op"] = "challenge",
            ["n"] = Convert.ToHexStringLower(challengeNonce),
        };
        using var challenge = Request(proxy, basePath, challengeQuery, auth);
        using var proof = await client.SendAsync(challenge, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (InfrastructureStatus(proof) || (int)proof.StatusCode != 200 && !HasProtocolHeaders(proof))
        {
            throw StatusFailure(proof, "challenge");
        }

        var proofBody = await BodyAsync(proof, "challenge", ct).ConfigureAwait(false);
        if ((int)proof.StatusCode != 200)
        {
            throw Failure("challenge", "http-status", status: (int)proof.StatusCode);
        }

        if (Header(proof, "X-WG-Version", "challenge") != HttpGetAuthentication.Version)
        {
            throw Failure("challenge", "version", "X-WG-Version");
        }

        using var json = Parse(proofBody, "challenge");
        var epoch = ChallengeEpoch(json.RootElement);
        var tick = ChallengeTime(json.RootElement);
        var challengeSignature = Header(proof, "X-WG-Auth", "challenge");
        try
        {
            auth.VerifyChallenge(basePath, challengeNonce, epoch, tick, challengeSignature);
        }
        catch (InvalidDataException ex)
        {
            throw Failure("challenge", "signature", "X-WG-Auth", inner: ex);
        }

        var started = Stopwatch.GetTimestamp();
        var query = new Dictionary<string, string> { ["r"] = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16)) };
        if (target is not null)
        {
            query["host"] = target.Host;
            query["port"] = target.Port.ToString(CultureInfo.InvariantCulture);
        }

        var path = basePath + "_amneziageo/probe/" + nonce;
        var requestNonce = RandomNumberGenerator.GetBytes(16);
        var time = checked(tick + (ulong)Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        var (digest, signature) = auth.SignRequest(path, query, [], epoch, time, requestNonce, ProxyTransportModes.Method(proxy));
        using var request = Request(proxy, path, query, auth);
        request.Headers.Add("X-WG-Epoch", Convert.ToHexStringLower(epoch));
        request.Headers.Add("X-WG-Time", time.ToString(CultureInfo.InvariantCulture));
        request.Headers.Add("X-WG-Nonce", Convert.ToHexStringLower(requestNonce));
        request.Headers.Add("X-WG-Auth", signature);
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (InfrastructureStatus(response))
        {
            throw StatusFailure(response, "probe");
        }

        var body = await BodyAsync(response, "probe", ct).ConfigureAwait(false);
        var responseEpoch = Epoch(Header(response, "X-WG-Epoch", "probe"), "probe", "X-WG-Epoch");
        var responseTime = HeaderTime(response);
        var version = Header(response, "X-WG-Version", "probe");
        var sequence = Header(response, "X-WG-Seq", "probe", optional: true);
        var dropped = Header(response, "X-WG-Down-Dropped", "probe", optional: true);
        var authError = Header(response, "X-WG-Auth-Error", "probe", optional: true);
        var responseSignature = Header(response, "X-WG-Auth", "probe");
        try
        {
            auth.VerifyResponse(digest, (int)response.StatusCode, responseEpoch,
                responseTime, version, sequence, dropped, authError, body, responseSignature);
        }
        catch (InvalidDataException ex)
        {
            throw Failure("probe", "signature", "X-WG-Auth", inner: ex);
        }

        if (!epoch.AsSpan().SequenceEqual(responseEpoch))
        {
            throw Failure("probe", "restarted");
        }

        if (version != HttpGetAuthentication.Version)
        {
            throw Failure("probe", "version", "X-WG-Version");
        }

        return new XHttpProbeClient.Response((int)response.StatusCode, body);
    }

    private static TransportProxyFault Failure(string stage, string reason, string field = "", int status = 0, Exception? inner = null) =>
        new("proxy-bad-answer", new(stage, reason, status, field), inner);

    private static bool HasProtocolHeaders(HttpResponseMessage response) =>
        response.Headers.Any(header => header.Key.StartsWith("X-WG-", StringComparison.OrdinalIgnoreCase));

    private static TransportProxyFault StatusFailure(HttpResponseMessage response, string stage) =>
        new(response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden ? "proxy-auth"
            : TransportProxyProbes.TransientStatus((int)response.StatusCode) ? "proxy-unavailable" : "proxy-bad-answer",
            new(stage, "http-status", (int)response.StatusCode));

    private static JsonDocument Parse(byte[] body, string stage)
    {
        try
        {
            return JsonDocument.Parse(body);
        }
        catch (JsonException ex)
        {
            throw Failure(stage, "invalid-json", inner: ex);
        }
    }

    private static byte[] ChallengeEpoch(JsonElement root) => root.ValueKind == JsonValueKind.Object
        && root.TryGetProperty("epoch", out var value) && value.ValueKind == JsonValueKind.String
        ? Epoch(value.GetString() ?? "", "challenge", "epoch") : throw Failure("challenge", "invalid-field", "epoch");

    private static ulong ChallengeTime(JsonElement root) => root.ValueKind == JsonValueKind.Object
        && root.TryGetProperty("time", out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetUInt64(out var tick)
        ? tick : throw Failure("challenge", "invalid-field", "time");

    private static ulong HeaderTime(HttpResponseMessage response) =>
        ulong.TryParse(Header(response, "X-WG-Time", "probe"), NumberStyles.None, CultureInfo.InvariantCulture, out var tick)
            ? tick : throw Failure("probe", "invalid-field", "X-WG-Time");

    private static bool InfrastructureStatus(HttpResponseMessage response) => (TransportProxyProbes.TransientStatus((int)response.StatusCode)
        || response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        && !HasProtocolHeaders(response);

    private static HttpRequestMessage Request(OfferedProxy proxy, string path, Dictionary<string, string> query,
        HttpGetAuthentication auth)
    {
        var address = new UriBuilder("http", proxy.Host, proxy.Port, path)
        {
            Query = string.Join("&", query.OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .Select(pair => Uri.EscapeDataString(pair.Key) + "=" + Uri.EscapeDataString(pair.Value))),
        };
        var request = new HttpRequestMessage(new HttpMethod(ProxyTransportModes.Method(proxy)), address.Uri)
        {
            Version = HttpVersion.Version11,
            VersionPolicy = HttpVersionPolicy.RequestVersionExact,
        };
        if (request.Method == HttpMethod.Post)
        {
            request.Content = new ByteArrayContent([]);
        }

        request.Headers.Add("X-WG-Version", HttpGetAuthentication.Version);
        request.Headers.Add("X-WG-Key", auth.KeyId);
        if (proxy.Auth.Header == "Authorization" && !proxy.Anonymous)
        {
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", proxy.Auth.Credential);
        }
        request.Headers.CacheControl = new System.Net.Http.Headers.CacheControlHeaderValue { NoCache = true, NoStore = true };
        return request;
    }

    private static string Header(HttpResponseMessage response, string name, string stage, bool optional = false)
    {
        if (!response.Headers.TryGetValues(name, out var values))
        {
            return optional ? "" : throw Failure(stage, "missing-header", name);
        }

        var items = values.Take(2).ToArray();
        return items.Length == 1 ? items[0] : throw Failure(stage, "duplicate-header", name);
    }

    private static byte[] Epoch(string value, string stage, string field)
    {
        if (value.Length != 32 || !value.All(char.IsAsciiHexDigit))
        {
            throw Failure(stage, "invalid-field", field);
        }

        return Convert.FromHexString(value);
    }

    private static async Task<byte[]> BodyAsync(HttpResponseMessage response, string stage, CancellationToken ct)
    {
        var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await using var owned = stream.ConfigureAwait(false);
        var data = new byte[4097];
        var length = 0;
        while (length < data.Length)
        {
            var count = await stream.ReadAsync(data.AsMemory(length), ct).ConfigureAwait(false);
            if (count == 0)
            {
                return data[..length];
            }

            length += count;
        }

        throw Failure(stage, "body-limit");
    }
}
