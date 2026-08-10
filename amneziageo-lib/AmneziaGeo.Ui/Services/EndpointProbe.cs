using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using AmneziaGeo.Decl;
using AmneziaGeo.Ipc;

namespace AmneziaGeo.Ui.Services;

/// <summary>
/// How a server answered the last measurement.
/// </summary>
internal enum ProbeOutcome
{
    /// <summary>
    /// Not measured yet.
    /// </summary>
    Unknown,

    /// <summary>
    /// The server answered.
    /// </summary>
    Alive,

    /// <summary>
    /// Nothing came back before the timeout.
    /// </summary>
    NoAnswer,

    /// <summary>
    /// The endpoint carries no address, or its name does not resolve.
    /// </summary>
    NoAddress,
}

/// <summary>
/// One measurement: the outcome, the average round trip in milliseconds, and the share of probes lost.
/// </summary>
internal readonly record struct ProbeResult(ProbeOutcome Outcome, int Milliseconds, int LossPercent);

/// <summary>
/// Measures whether a configuration's server answers, how long it takes, and how much of a short burst it
/// drops. A WebSocket transport is measured by a TCP connect to the front it dials; a plain tunnel by an ICMP
/// echo to its endpoint, since AmneziaWG answers a real handshake and nothing else.
/// </summary>
internal static class EndpointProbe
{
    private const int TimeoutMs = 1500;

    // Probes per measurement: enough for a loss reading, few enough to keep a sweep of every server short.
    private const int Probes = 5;

    /// <summary>
    /// Measures one configuration's server.
    /// </summary>
    public static async Task<ProbeResult> MeasureAsync(
        string endpoint,
        bool webSocket,
        string webSocketHost,
        int webSocketPort,
        CancellationToken ct)
    {
        // The field may hold a bare host, a whole wss:// URL, or nothing at all, in which case the carrier
        // stands at the endpoint's own host on the port beside it.
        var carrier = WsEndpoint.Parse(webSocketHost, webSocketPort, HostOf(endpoint));
        var overWebSocket = webSocket && carrier.Host.Length > 0;
        var address = await ResolveAsync(overWebSocket ? carrier.Host : HostOf(endpoint), ct).ConfigureAwait(false);
        if (address is null)
        {
            return new ProbeResult(ProbeOutcome.NoAddress, 0, 100);
        }

        var answered = 0;
        var elapsed = 0L;
        for (var i = 0; i < Probes && !ct.IsCancellationRequested; i++)
        {
            var one = overWebSocket
                ? await ConnectAsync(address, carrier.Port, ct).ConfigureAwait(false)
                : await EchoAsync(address, ct).ConfigureAwait(false);
            if (one.Outcome == ProbeOutcome.Alive)
            {
                answered++;
                elapsed += one.Milliseconds;
            }
        }

        return answered > 0
            ? new ProbeResult(ProbeOutcome.Alive, (int)(elapsed / answered), (Probes - answered) * 100 / Probes)
            : new ProbeResult(ProbeOutcome.NoAnswer, 0, 100);
    }

    // The host of a "host:port" endpoint, brackets around an IPv6 literal included; a bare IPv6 literal is
    // taken whole, having no port to strip.
    private static string HostOf(string endpoint)
    {
        var value = endpoint.Trim();
        if (value.StartsWith('['))
        {
            var close = value.IndexOf(']');
            return close > 1 ? value[1..close] : string.Empty;
        }

        var colon = value.IndexOf(':');
        if (colon < 0)
        {
            return value;
        }

        return value.IndexOf(':', colon + 1) < 0 ? value[..colon] : value;
    }

    // Resolves a host to one address, IPv4 first: the measurement follows the family the tunnel dials.
    private static async Task<IPAddress?> ResolveAsync(string host, CancellationToken ct)
    {
        if (host.Length == 0)
        {
            return null;
        }

        if (IPAddress.TryParse(host, out var literal))
        {
            return literal;
        }

        try
        {
            var addresses = await Dns.GetHostAddressesAsync(host, ct).ConfigureAwait(false);
            return addresses.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork)
                ?? addresses.FirstOrDefault();
        }
        catch (Exception)
        {
            return null;
        }
    }

    // Times a TCP connect; a refused port counts as no answer, the transport being unusable either way.
    private static async Task<ProbeResult> ConnectAsync(IPAddress address, int port, CancellationToken ct)
    {
        using var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeoutMs);
        var clock = Stopwatch.StartNew();
        try
        {
            await socket.ConnectAsync(new IPEndPoint(address, port), deadline.Token).ConfigureAwait(false);
            return new ProbeResult(ProbeOutcome.Alive, (int)clock.ElapsedMilliseconds, 0);
        }
        catch (Exception)
        {
            return new ProbeResult(ProbeOutcome.NoAnswer, 0, 100);
        }
    }

    // AmneziaWG answers a real handshake and nothing else, so a plain tunnel is measured by an echo to its
    // endpoint.
    private static async Task<ProbeResult> EchoAsync(IPAddress address, CancellationToken ct)
    {
        var trip = await IcmpEcho.RoundTripAsync(address, TimeoutMs, ct).ConfigureAwait(false);
        return trip >= 0
            ? new ProbeResult(ProbeOutcome.Alive, trip, 0)
            : new ProbeResult(ProbeOutcome.NoAnswer, 0, 100);
    }
}
