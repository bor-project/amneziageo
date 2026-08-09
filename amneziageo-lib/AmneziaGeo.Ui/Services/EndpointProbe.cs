using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

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
        var overWebSocket = webSocket && webSocketHost.Length > 0;
        var address = await ResolveAsync(overWebSocket ? webSocketHost : HostOf(endpoint), ct).ConfigureAwait(false);
        if (address is null)
        {
            return new ProbeResult(ProbeOutcome.NoAddress, 0, 100);
        }

        var answered = 0;
        var elapsed = 0L;
        for (var i = 0; i < Probes && !ct.IsCancellationRequested; i++)
        {
            var one = overWebSocket
                ? await ConnectAsync(address, webSocketPort, ct).ConfigureAwait(false)
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

    // Windows echoes through the OS helper, which needs no elevation; Linux and Android take an unprivileged
    // ICMP datagram socket, the runtime looking for a ping binary where Android keeps none.
    private static Task<ProbeResult> EchoAsync(IPAddress address, CancellationToken ct)
    {
        return OperatingSystem.IsWindows() ? SystemEchoAsync(address) : SocketEchoAsync(address, ct);
    }

    private static async Task<ProbeResult> SystemEchoAsync(IPAddress address)
    {
        try
        {
            using var ping = new Ping();
            var reply = await ping.SendPingAsync(address, TimeoutMs).ConfigureAwait(false);
            return reply.Status == IPStatus.Success
                ? new ProbeResult(ProbeOutcome.Alive, (int)reply.RoundtripTime, 0)
                : new ProbeResult(ProbeOutcome.NoAnswer, 0, 100);
        }
        catch (Exception)
        {
            return new ProbeResult(ProbeOutcome.NoAnswer, 0, 100);
        }
    }

    private static async Task<ProbeResult> SocketEchoAsync(IPAddress address, CancellationToken ct)
    {
        var v6 = address.AddressFamily == AddressFamily.InterNetworkV6;
        var socket = default(Socket);
        try
        {
            socket = new Socket(address.AddressFamily, SocketType.Dgram, v6 ? ProtocolType.IcmpV6 : ProtocolType.Icmp);
            socket.Connect(new IPEndPoint(address, 0));
        }
        catch (Exception)
        {
            // A kernel that hands out no ping socket leaves the OS helper as the only way to echo.
            socket?.Dispose();
            return await SystemEchoAsync(address).ConfigureAwait(false);
        }

        using (socket)
        using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            deadline.CancelAfter(TimeoutMs);
            var reply = new byte[128];
            var clock = Stopwatch.StartNew();
            try
            {
                await socket.SendAsync(EchoRequest(v6), SocketFlags.None, deadline.Token).ConfigureAwait(false);
                var received = await socket.ReceiveAsync(reply, SocketFlags.None, deadline.Token).ConfigureAwait(false);
                return received > 0
                    ? new ProbeResult(ProbeOutcome.Alive, (int)clock.ElapsedMilliseconds, 0)
                    : new ProbeResult(ProbeOutcome.NoAnswer, 0, 100);
            }
            catch (Exception)
            {
                return new ProbeResult(ProbeOutcome.NoAnswer, 0, 100);
            }
        }
    }

    // An echo request: type, code, checksum, identifier, sequence, and a short payload. A ping socket rewrites
    // the identifier and the IPv6 checksum itself.
    private static byte[] EchoRequest(bool v6)
    {
        var packet = new byte[16];
        packet[0] = v6 ? (byte)128 : (byte)8;
        packet[4] = 0x41;
        packet[5] = 0x47;
        packet[7] = 1;
        for (var i = 8; i < packet.Length; i++)
        {
            packet[i] = (byte)i;
        }

        if (!v6)
        {
            var sum = Checksum(packet);
            packet[2] = (byte)(sum >> 8);
            packet[3] = (byte)sum;
        }

        return packet;
    }

    // The one's complement of the one's complement sum over the packet's 16-bit words.
    private static ushort Checksum(byte[] packet)
    {
        var sum = 0;
        for (var i = 0; i + 1 < packet.Length; i += 2)
        {
            sum += (packet[i] << 8) | packet[i + 1];
        }

        while ((sum >> 16) != 0)
        {
            sum = (sum & 0xFFFF) + (sum >> 16);
        }

        return (ushort)~sum;
    }
}
