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
    /// Checks the selected UDP destination at the authenticated front.
    /// </summary>
    public static async Task<TransportProxyProbeResult> CheckTargetAsync(ResolvedTransportProxy selected, CancellationToken ct,
        Func<string, CancellationToken, Task<IPAddress?>>? resolve = null, Func<Socket, bool>? bypass = null,
        Func<DnsEndPoint, CancellationToken, ValueTask<Stream>>? connect = null)
    {
        if (selected.Record.Legacy || selected.Record.Proxy.Transport == "quic")
        {
            return Unsupported();
        }

        if (ProxyTargetRules.NormalizeHost(selected.TargetHost) is not { } host || selected.TargetPort is < 1 or > 65535)
        {
            return selected.Record.Proxy.Targets?.Restricted == true ? Failed("bad-proxy-target") : Unsupported();
        }

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
            var nonce = Guid.NewGuid().ToString("N");
            var target = new ProxyDestination(host, selected.TargetPort);
            var response = await XHttpProbeClient.SendAsync(selected.Record.Proxy, nonce,
                connect ?? ((endpoint, token) => ConnectAsync(endpoint, resolve, bypass, token)), deadline.Token, target).ConfigureAwait(false);
            if (response.StatusCode == 404)
            {
                return Unsupported();
            }

            using var document = JsonDocument.Parse(response.Body);
            var root = document.RootElement;
            if (response.StatusCode != 200)
            {
                var error = root.TryGetProperty("error", out var code) && code.ValueKind == JsonValueKind.String ? code.GetString() : null;
                return Failed(response.StatusCode == 403 && error == "proxy-target-denied" ? error
                    : response.StatusCode == 400 && error == "bad-proxy-target" ? error
                    : response.StatusCode is 401 or 403 ? "proxy-auth" : "proxy-unavailable");
            }

            if (!root.TryGetProperty("nonce", out var echoed) || echoed.GetString() != nonce
                || !root.TryGetProperty("transport", out var transport) || !SameTransport(transport.GetString(), selected.Record.Proxy)
                || !root.TryGetProperty("running", out var running) || running.ValueKind != JsonValueKind.True)
            {
                return Failed("proxy-bad-answer");
            }

            if (!root.TryGetProperty("target", out var answer) || !root.TryGetProperty("allowed", out var allowed))
            {
                return Unsupported();
            }

            return allowed.ValueKind == JsonValueKind.True && answer.ValueKind == JsonValueKind.Object
                && answer.TryGetProperty("host", out var echoedHost) && echoedHost.ValueKind == JsonValueKind.String
                && ProxyTargetRules.NormalizeHost(echoedHost.GetString()) == host
                && answer.TryGetProperty("port", out var echoedPort) && echoedPort.TryGetInt32(out var port) && port == target.Port
                ? new TransportProxyProbeResult(TransportProxyAvailability.Available, ProtocolVersion: ProxyTransportModes.IsHttp(selected.Record.Proxy) ? 2 : 0) : Failed("proxy-bad-answer");
        }
        catch (Exception ex) when (!ct.IsCancellationRequested && ex is HttpRequestException or IOException or InvalidDataException or SocketException
            or AuthenticationException or OperationCanceledException or JsonException or FormatException or InvalidOperationException or ArgumentException)
        {
            return Failed(ex is AuthenticationException ? "proxy-tls"
                : ex is InvalidDataException or JsonException ? "proxy-bad-answer" : "proxy-no-answer");
        }
    }

    /// <summary>
    /// Rejects a destination refused by the front.
    /// </summary>
    public static async Task EnsureTargetAsync(ResolvedTransportProxy selected, IPAddress address, CancellationToken ct,
        Func<Socket, bool>? bypass = null)
    {
        var result = await CheckTargetAsync(selected, ct, (_, _) => Task.FromResult<IPAddress?>(address), bypass).ConfigureAwait(false);
        if (result.Fault is "proxy-target-denied" or "bad-proxy-target")
        {
            throw new TransportProxyFault(result.Fault);
        }

        if (ProxyTransportModes.Unconfirmed(selected.Record.Proxy) && result.Availability != TransportProxyAvailability.Available)
        {
            throw new TransportProxyFault("proxy-version-unconfirmed");
        }
    }

    /// <summary>
    /// Checks the selected transport protocol and authorization.
    /// </summary>
    public static async Task<TransportProxyProbeResult> CheckAsync(CatalogTransportProxy record, CancellationToken ct,
        Func<string, CancellationToken, Task<IPAddress?>>? resolve = null, Func<Socket, bool>? bypass = null,
        Func<DnsEndPoint, CancellationToken, ValueTask<Stream>>? connect = null, string? legacyConfig = null, ProxyDestination? target = null)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
            var proxy = record.Proxy;
            if (proxy.Transport == "websocket")
            {
                if (!record.Legacy)
                {
                    var frontResult = await CheckOptionalHttpFrontAsync(proxy, deadline.Token, resolve, bypass, connect).ConfigureAwait(false);
                    if (frontResult.Availability == TransportProxyAvailability.Available || frontResult.Fault == "proxy-tls")
                    {
                        return frontResult;
                    }
                }

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

                var destination = target ?? proxy.Targets?.Destinations.FirstOrDefault(one => one.Port is > 0 and <= 65535);
                var targetPort = record.Legacy ? record.LegacyPort : destination?.Port ?? 9;
                var front = WsEndpoint.From(new ResolvedTransportProxy(record, "", record.Legacy ? "127.0.0.1" : destination?.Host ?? "127.0.0.1", targetPort));
                var (outcome, detail) = await WsCarrier.ProbeAsync(front, address, targetPort,
                    record.LegacyOffered ? ServiceToken.HeaderOf(legacyConfig) : null, bypass, deadline.Token).ConfigureAwait(false);
                if (!record.Legacy && outcome == WsFrontOutcome.Refused)
                {
                    return detail.StartsWith("HTTP/1.1 401", StringComparison.OrdinalIgnoreCase)
                        || detail.StartsWith("HTTP/1.1 403", StringComparison.OrdinalIgnoreCase) ? Failed("proxy-auth")
                        : destination is null ? Unsupported() : Failed("proxy-rejected");
                }

                return outcome == WsFrontOutcome.Ok ? new TransportProxyProbeResult(TransportProxyAvailability.Available)
                    : Failed(outcome switch
                    {
                        WsFrontOutcome.Tls => "proxy-tls", WsFrontOutcome.NoAddress => "proxy-no-address",
                        WsFrontOutcome.Refused => "proxy-auth", _ => "proxy-no-answer",
                    });
            }

            if (!ProxyTransportModes.IsHttp(proxy))
            {
                return new TransportProxyProbeResult(TransportProxyAvailability.Unknown, "proxy-probe-unsupported");
            }

            return await CheckHttpFrontAsync(proxy, deadline.Token, resolve, bypass, connect).ConfigureAwait(false);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested && ex is HttpRequestException or IOException or InvalidDataException or SocketException
            or AuthenticationException or OperationCanceledException or JsonException or FormatException or InvalidOperationException or ArgumentException)
        {
            return Failed(ex is AuthenticationException ? "proxy-tls"
                : ex is InvalidDataException or JsonException ? "proxy-bad-answer" : "proxy-no-answer");
        }
    }

    private static bool SameTransport(string? actual, OfferedProxy expected) => actual == expected.Transport
        || ProxyTransportModes.IsHttp(expected) && actual is "http" or "xhttp";

    private static async Task<TransportProxyProbeResult> CheckOptionalHttpFrontAsync(OfferedProxy proxy, CancellationToken ct,
        Func<string, CancellationToken, Task<IPAddress?>>? resolve, Func<Socket, bool>? bypass,
        Func<DnsEndPoint, CancellationToken, ValueTask<Stream>>? connect)
    {
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(TimeSpan.FromSeconds(2));
        try
        {
            return await CheckHttpFrontAsync(proxy, budget.Token, resolve, bypass, connect).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return Unsupported();
        }
    }

    private static async Task<TransportProxyProbeResult> CheckHttpFrontAsync(OfferedProxy proxy, CancellationToken ct,
        Func<string, CancellationToken, Task<IPAddress?>>? resolve, Func<Socket, bool>? bypass,
        Func<DnsEndPoint, CancellationToken, ValueTask<Stream>>? connect)
    {
        try
        {
            var nonce = Guid.NewGuid().ToString("N");
            var response = await XHttpProbeClient.SendAsync(proxy, nonce,
                connect ?? ((endpoint, token) => ConnectAsync(endpoint, resolve, bypass, token)), ct).ConfigureAwait(false);
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
                && result.RootElement.TryGetProperty("transport", out var transport) && SameTransport(transport.GetString(), proxy)
                && result.RootElement.TryGetProperty("running", out var running) && running.ValueKind == JsonValueKind.True
                ? new TransportProxyProbeResult(TransportProxyAvailability.Available, ProtocolVersion: ProxyTransportModes.IsHttp(proxy) ? 2 : 0) : Failed("proxy-bad-answer");
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

    private static TransportProxyProbeResult Unsupported() => new(TransportProxyAvailability.Unknown, "proxy-probe-unsupported");
}
