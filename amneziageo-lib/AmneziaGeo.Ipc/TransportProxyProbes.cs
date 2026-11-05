using System.Net;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text.Json;
using AmneziaGeo.Decl;

namespace AmneziaGeo.Ipc;

/// <summary>
/// Checks the authenticated front independently of a WireGuard peer.
/// </summary>
public static class TransportProxyProbes
{
    /// <summary>
    /// Checks the selected transport protocol and authorization.
    /// </summary>
    public static async Task<TransportProxyProbeResult> CheckAsync(CatalogTransportProxy record, CancellationToken ct,
        Func<string, CancellationToken, Task<IPAddress?>>? resolve = null, Func<Socket, bool>? bypass = null,
        Func<DnsEndPoint, CancellationToken, ValueTask<Stream>>? connect = null, string? legacyConfig = null)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
            var proxy = record.Proxy;
            if (proxy.Transport == "websocket")
            {
                if (record.LegacyOffered && legacyConfig is null)
                {
                    return new TransportProxyProbeResult(TransportProxyAvailability.Unknown, "proxy-probe-unsupported");
                }

                var address = resolve is null
                    ? (await Dns.GetHostAddressesAsync(proxy.Host, deadline.Token).ConfigureAwait(false)).FirstOrDefault()
                    : await resolve(proxy.Host, deadline.Token).ConfigureAwait(false);
                if (address is null)
                {
                    return Failed("proxy-no-address");
                }

                var targetPort = record.Legacy ? record.LegacyPort : 9;
                var front = WsEndpoint.From(new ResolvedTransportProxy(record, "", "127.0.0.1", targetPort));
                var (outcome, _) = await WsCarrier.ProbeAsync(front, address, targetPort,
                    record.LegacyOffered ? ServiceToken.HeaderOf(legacyConfig) : null, bypass, deadline.Token).ConfigureAwait(false);
                return outcome == WsFrontOutcome.Ok ? new TransportProxyProbeResult(TransportProxyAvailability.Available)
                    : Failed(outcome switch
                    {
                        WsFrontOutcome.Tls => "proxy-tls", WsFrontOutcome.NoAddress => "proxy-no-address",
                        WsFrontOutcome.Refused => "proxy-auth", _ => "proxy-no-answer",
                    });
            }

            if (proxy.Transport != "xhttp")
            {
                return new TransportProxyProbeResult(TransportProxyAvailability.Unknown, "proxy-probe-unsupported");
            }

            var nonce = Guid.NewGuid().ToString("N");
            var response = await XHttpProbeClient.SendAsync(proxy, nonce,
                connect ?? ((endpoint, token) => ConnectAsync(endpoint, resolve, bypass, token)), deadline.Token).ConfigureAwait(false);
            if (response.StatusCode == 404)
            {
                return new TransportProxyProbeResult(TransportProxyAvailability.Unknown, "proxy-probe-unsupported");
            }

            if (response.StatusCode != 200)
            {
                return Failed(response.StatusCode == 401 ? "proxy-auth" : "proxy-unavailable");
            }

            using var result = JsonDocument.Parse(response.Body);
            return result.RootElement.TryGetProperty("nonce", out var echoed) && echoed.GetString() == nonce
                && result.RootElement.TryGetProperty("transport", out var transport) && transport.GetString() == "xhttp"
                && result.RootElement.TryGetProperty("running", out var running) && running.ValueKind == JsonValueKind.True
                ? new TransportProxyProbeResult(TransportProxyAvailability.Available) : Failed("proxy-bad-answer");
        }
        catch (Exception ex) when (!ct.IsCancellationRequested && ex is HttpRequestException or IOException or InvalidDataException or SocketException
            or AuthenticationException or OperationCanceledException or JsonException or FormatException or InvalidOperationException or ArgumentException)
        {
            return Failed(ex is AuthenticationException ? "proxy-tls"
                : ex is InvalidDataException or JsonException ? "proxy-bad-answer" : "proxy-no-answer");
        }
    }

    private static async ValueTask<Stream> ConnectAsync(DnsEndPoint endpoint,
        Func<string, CancellationToken, Task<IPAddress?>>? resolve, Func<Socket, bool>? bypass, CancellationToken ct)
    {
        var address = resolve is null
            ? (await Dns.GetHostAddressesAsync(endpoint.Host, ct).ConfigureAwait(false)).FirstOrDefault()
            : await resolve(endpoint.Host, ct).ConfigureAwait(false);
        if (address is null)
        {
            throw new SocketException((int)SocketError.HostNotFound);
        }

        var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            if (bypass?.Invoke(socket) == false)
            {
                throw new SocketException((int)SocketError.AccessDenied);
            }

            await socket.ConnectAsync(new IPEndPoint(address, endpoint.Port), ct).ConfigureAwait(false);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    private static TransportProxyProbeResult Failed(string fault) => new(TransportProxyAvailability.Unavailable, fault);
}
