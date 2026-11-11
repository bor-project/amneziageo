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
                    options.ApplicationProtocols = [SslApplicationProtocol.Http11];
                    await tls.AuthenticateAsClientAsync(options, pending.Token).ConfigureAwait(false);
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
        if (InfrastructureStatus(proof))
        {
            return new XHttpProbeClient.Response((int)proof.StatusCode, []);
        }

        var proofBody = await BodyAsync(proof, ct).ConfigureAwait(false);
        if ((int)proof.StatusCode != 200 || Header(proof, "X-WG-Version") != HttpGetAuthentication.Version)
        {
            throw new InvalidDataException("The proxy requires signed HTTP GET version 2.");
        }

        using var json = JsonDocument.Parse(proofBody);
        var epoch = Epoch(json.RootElement.GetProperty("epoch").GetString() ?? "");
        var tick = json.RootElement.GetProperty("time").GetUInt64();
        auth.VerifyChallenge(basePath, challengeNonce, epoch, tick, Header(proof, "X-WG-Auth"));
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
            return new XHttpProbeClient.Response((int)response.StatusCode, []);
        }

        var body = await BodyAsync(response, ct).ConfigureAwait(false);
        var responseEpoch = Epoch(Header(response, "X-WG-Epoch"));
        auth.VerifyResponse(digest, (int)response.StatusCode, responseEpoch,
            ulong.Parse(Header(response, "X-WG-Time"), NumberStyles.None, CultureInfo.InvariantCulture),
            Header(response, "X-WG-Version"), Header(response, "X-WG-Seq", optional: true),
            Header(response, "X-WG-Down-Dropped", optional: true), Header(response, "X-WG-Auth-Error", optional: true),
            body, Header(response, "X-WG-Auth"));
        if (!epoch.AsSpan().SequenceEqual(responseEpoch) || Header(response, "X-WG-Version") != HttpGetAuthentication.Version)
        {
            throw new InvalidDataException("The HTTP GET server restarted.");
        }

        return new XHttpProbeClient.Response((int)response.StatusCode, body);
    }

    private static bool InfrastructureStatus(HttpResponseMessage response) => TransportProxyProbes.TransientStatus((int)response.StatusCode)
        && !response.Headers.Any(header => header.Key.StartsWith("X-WG-", StringComparison.OrdinalIgnoreCase));

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
        request.Headers.CacheControl = new System.Net.Http.Headers.CacheControlHeaderValue { NoCache = true, NoStore = true };
        return request;
    }

    private static string Header(HttpResponseMessage response, string name, bool optional = false)
    {
        if (!response.Headers.TryGetValues(name, out var values))
        {
            return optional ? "" : throw new InvalidDataException("An HTTP GET signature header is missing.");
        }

        var items = values.Take(2).ToArray();
        return items.Length == 1 ? items[0] : throw new InvalidDataException("An HTTP GET signature header is duplicated.");
    }

    private static byte[] Epoch(string value)
    {
        if (value.Length != 32)
        {
            throw new InvalidDataException("The HTTP GET epoch is invalid.");
        }

        return Convert.FromHexString(value);
    }

    private static async Task<byte[]> BodyAsync(HttpResponseMessage response, CancellationToken ct)
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

        throw new InvalidDataException("The HTTP GET probe body is too large.");
    }
}
