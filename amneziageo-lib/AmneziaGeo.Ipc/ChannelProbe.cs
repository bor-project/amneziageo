using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace AmneziaGeo.Ipc;

/// <summary>
/// What the agent knows before the run: where to aim each probe and what the session counters say. The tunnel is
/// measured from inside the process that owns it, so nothing here is discovered by guessing. A carrier port set
/// means the tunnel rides a websocket, and the endpoint is that front: it is reached by a connect, not an echo.
/// </summary>
public sealed record ChannelProbeOptions(
    string Config,
    bool Connected,
    string? Gateway = null,
    string? Endpoint = null,
    IReadOnlyList<string>? TunnelTargets = null,
    IReadOnlyList<string>? BeyondTargets = null,
    bool TunnelIsDefault = false,
    bool EndpointOutsideTunnel = true,
    int HandshakeAgeSeconds = -1,
    int RekeysPerMinute = -1,
    long RxBytes = -1,
    long TxBytes = -1,
    string SpeedUrl = ChannelProbe.DefaultSpeedUrl,
    string? SourceHost = null,
    Func<Socket, bool>? Bypass = null,
    int ConfiguredMtu = 0,
    int CarrierPort = 0);

/// <summary>
/// Runs the ladder: gateway, the server outside the tunnel, the session, the server inside the tunnel, the public
/// path past the exit, and a download through the tunnel against the same download beside it. Every leg cuts off
/// the layer in front of it, so the first one that fails is the answer to "which part is broken".
/// </summary>
public static class ChannelProbe
{
    /// <summary>
    /// Where the throughput legs pull their bytes from.
    /// </summary>
    public const string DefaultSpeedUrl = "https://speed.cloudflare.com/__down?bytes=25000000";

    // Echoes per leg, and the answer that has to come back before the timeout.
    private const int Echoes = 6;
    private const int EchoTimeoutMs = 1_000;
    private const int EchoGapMs = 150;

    // Attempts a dead target is given before the leg is abandoned; without it a dead leg costs the whole budget.
    private const int DeadAfter = 3;

    /// <summary>
    /// Payload a path of the usual 1500-byte MTU carries whole.
    /// </summary>
    public const int FullPayloadBytes = 1472;

    // Payloads the path is asked to carry, largest first; the first that passes names the step it breaks at.
    private static readonly int[] _sizes = [FullPayloadBytes, 1400, 1280, 1000];

    // How close to the byte the size between two ladder steps is narrowed down.
    private const int SizeStep = 8;

    // Seconds each throughput leg pulls for.
    private const int DownloadMs = 4_000;

    // Bytes a destination has to hand over before what it took counts as a rate; a page that ends in a moment
    // times its own latency and nothing else.
    private const int SourceFloorBytes = 256 * 1024;

    /// <summary>
    /// Runs every leg and returns the finished report.
    /// </summary>
    public static async Task<CheckReport> RunAsync(ChannelProbeOptions options, CancellationToken ct)
    {
        var legs = new List<CheckLeg> { await EchoLegAsync(CheckLegs.Gateway, options.Gateway, null, true, ct).ConfigureAwait(false) };

        legs.Add(await EndpointAsync(options, ct).ConfigureAwait(false));

        // A download rides the tunnel only where the tunnel is the default route: under a routing list it leaves
        // through the physical path, and calling that a tunnel measurement would blame the server for the house.
        var tunneled = options.Connected && options.TunnelIsDefault;
        if (options.Connected)
        {
            legs.Add(Handshake(options));
            legs.Add(await InsideAsync(CheckLegs.Peer, options.TunnelTargets, "nothing inside the tunnel answered an echo", ct).ConfigureAwait(false));
            legs.Add(tunneled
                ? await InsideAsync(CheckLegs.Beyond, options.BeyondTargets, "nothing past the exit answered an echo", ct).ConfigureAwait(false)
                : new CheckLeg(CheckLegs.Beyond, LegState.Skipped, Note: "the routing list carries only what it names, so this echo says nothing about the path past the exit"));
            legs.Add(tunneled
                ? (await ThroughputAsync(CheckLegs.Tunnel, options.SpeedUrl, null, ct).ConfigureAwait(false)).Leg
                : new CheckLeg(CheckLegs.Tunnel, LegState.Skipped, Note: "the routing list carries only what it names, so this download does not ride the tunnel"));
            legs.Add(await SourceAsync(options, tunneled, ct).ConfigureAwait(false));
        }

        legs.Add(await BesideAsync(options, tunneled, ct).ConfigureAwait(false));

        var (key, args, culprit) = ChannelVerdict.Decide(legs, options.Connected);
        var advice = MtuAdvice.For(Narrowest(legs), options.ConfiguredMtu);
        return new CheckReport(
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            options.Config,
            legs,
            key,
            args,
            culprit,
            advice);
    }

    // The tightest size any leg measured: a tunnel packet has to pass all of them, so the smallest decides.
    private static int Narrowest(IReadOnlyList<CheckLeg> legs)
    {
        var sizes = legs.Where(leg => leg.MaxPacketBytes > 0).Select(leg => leg.MaxPacketBytes).ToList();
        return sizes.Count > 0 ? sizes.Min() : 0;
    }

    // The path the tunnel is carried over. An echo to the server while the tunnel is up measures that path only
    // where it leaves beside the tunnel; where everything is swallowed by the tun, the same echo would measure the
    // tunnel and read as the path, which is exactly the confusion this ladder exists to remove. A tunnel carried
    // inside a websocket is measured at that front: the endpoint's own port answers nothing on such a network.
    private static async Task<CheckLeg> EndpointAsync(ChannelProbeOptions options, CancellationToken ct)
    {
        if (options.Connected && !options.EndpointOutsideTunnel && options.Bypass is null)
        {
            return new CheckLeg(CheckLegs.Endpoint, LegState.Skipped, Note: "this system cannot send beside its own tunnel while it runs");
        }

        return options.CarrierPort > 0
            ? await ConnectLegAsync(CheckLegs.Endpoint, options.Endpoint, options.CarrierPort, options.Bypass, ct).ConfigureAwait(false)
            : await EchoLegAsync(CheckLegs.Endpoint, options.Endpoint, options.Bypass, true, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// A leg measured by connecting: the carrier answers TCP, so round trip, jitter and loss come from a burst
    /// of connects. The largest packet is left unmeasured - a stream carrier splits what it is given, and the
    /// size it takes says nothing about the MTU the tunnel should run at.
    /// </summary>
    public static async Task<CheckLeg> ConnectLegAsync(string name, string? target, int port, Func<Socket, bool>? bypass, CancellationToken ct)
    {
        if (!IPAddress.TryParse(target, out var address))
        {
            return new CheckLeg(name, LegState.Unknown, Note: target is { Length: > 0 } ? "not an address" : "no address to probe");
        }

        var times = new List<int>();
        var lost = 0;
        for (var attempt = 0; attempt < Echoes; attempt++)
        {
            if (attempt > 0)
            {
                await Task.Delay(EchoGapMs, ct).ConfigureAwait(false);
            }

            var trip = await ReachAsync(address, port, bypass, ct).ConfigureAwait(false);
            if (trip < 0)
            {
                lost++;
                if (lost >= DeadAfter && times.Count == 0)
                {
                    break;
                }

                continue;
            }

            times.Add(trip);
        }

        if (times.Count == 0)
        {
            return new CheckLeg(name, LegState.Unknown, LossPercent: LinkHealth.LossUnknown, Note: $"{address}:{port} accepted no connection");
        }

        var average = (int)times.Average();
        var jitter = times.Count > 1 ? (int)times.Select(one => Math.Abs(one - average)).Average() : 0;
        var loss = lost * 100 / (times.Count + lost);
        return new CheckLeg(name, ChannelVerdict.StateFor(loss), average, jitter, loss, Note: $"{address}:{port} inside a websocket");
    }

    // One timed connect to the carrier; a refused or unanswered port counts as a loss.
    private static async Task<int> ReachAsync(IPAddress address, int port, Func<Socket, bool>? bypass, CancellationToken ct)
    {
        using var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(EchoTimeoutMs);
        var clock = Stopwatch.StartNew();
        try
        {
            bypass?.Invoke(socket);
            await socket.ConnectAsync(new IPEndPoint(address, port), deadline.Token).ConfigureAwait(false);
            return (int)clock.ElapsedMilliseconds;
        }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException or ObjectDisposedException)
        {
            return -1;
        }
    }

    // The same download beside the tunnel; a plain socket already goes that way unless the tunnel is the default.
    private static async Task<CheckLeg> BesideAsync(ChannelProbeOptions options, bool tunneled, CancellationToken ct)
    {
        if (!tunneled)
        {
            return (await ThroughputAsync(CheckLegs.Direct, options.SpeedUrl, null, ct).ConfigureAwait(false)).Leg;
        }

        return options.Bypass is null
            ? new CheckLeg(CheckLegs.Direct, LegState.Skipped, Note: "this system cannot send beside its own tunnel")
            : (await ThroughputAsync(CheckLegs.Direct, options.SpeedUrl, options.Bypass, ct).ConfigureAwait(false)).Leg;
    }

    // The destination the user's traffic actually goes to, pulled over the same tunnel as the neutral download
    // beside it. Alone it says nothing - a slow source and a slow tunnel look the same - and next to the neutral
    // one it separates them in a single run.
    private static async Task<CheckLeg> SourceAsync(ChannelProbeOptions options, bool tunneled, CancellationToken ct)
    {
        if (options.SourceHost is not { Length: > 0 } host)
        {
            return new CheckLeg(CheckLegs.Source, LegState.Skipped, Note: "nothing here knows which destination carries this traffic");
        }

        if (!tunneled)
        {
            return new CheckLeg(CheckLegs.Source, LegState.Skipped, Note: $"the routing list decides where {host} goes, so this download does not ride the tunnel");
        }

        var (leg, bytes) = await ThroughputAsync(CheckLegs.Source, SourceUrl(host), null, ct).ConfigureAwait(false);
        if (bytes >= SourceFloorBytes)
        {
            return leg with { Note = host };
        }

        // A page that ends in a moment, a refusal or a redirect timed its own latency and nothing else. The leg
        // says what came back instead of calling the destination slow: a rate invented here would blame it.
        var reason = bytes > 0 ? $"sent {CheckFormat.Bytes(bytes)}, too little to time" : "handed over nothing to time";
        return new CheckLeg(CheckLegs.Source, LegState.Unknown, Note: $"{host} {reason}");
    }

    // Where a named destination is asked for its bytes; one given as a URL is taken as it stands.
    private static string SourceUrl(string host)
    {
        return host.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || host.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
                ? host
                : $"https://{host}/";
    }

    /// <summary>
    /// A leg measured by echo: round trip, jitter, loss, and - when asked for - the largest payload the path
    /// carries whole. That size costs a burst of its own, which a sweep over every server would pay per server.
    /// </summary>
    public static async Task<CheckLeg> EchoLegAsync(string name, string? target, Func<Socket, bool>? bypass, bool measureSize, CancellationToken ct)
    {
        if (!IPAddress.TryParse(target, out var address))
        {
            return new CheckLeg(name, LegState.Unknown, Note: target is { Length: > 0 } ? "not an address" : "no address to probe");
        }

        var (rtt, jitter, loss) = await EchoAsync(address, bypass, ct).ConfigureAwait(false);
        if (loss >= 100)
        {
            return new CheckLeg(name, LegState.Unknown, LossPercent: LinkHealth.LossUnknown, Note: $"{address} never answered");
        }

        var size = measureSize ? await LargestAsync(address, bypass, ct).ConfigureAwait(false) : 0;
        return new CheckLeg(name, ChannelVerdict.StateFor(loss), rtt, jitter, loss, MaxPacketBytes: size, Note: address.ToString());
    }

    // The session itself: nothing is sent, the engine's own counters are read.
    private static CheckLeg Handshake(ChannelProbeOptions options)
    {
        var state = options.HandshakeAgeSeconds < 0
            ? LegState.Unknown
            : options.RekeysPerMinute >= LinkHealth.ChurnPerMinute
                ? LegState.Bad
                : options.HandshakeAgeSeconds > HandshakeAge.SilentSeconds
                    ? LegState.Weak
                    : LegState.Ok;

        return new CheckLeg(
            CheckLegs.Handshake,
            state,
            AgeSeconds: options.HandshakeAgeSeconds,
            RekeysPerMinute: options.RekeysPerMinute,
            RxBytes: options.RxBytes,
            TxBytes: options.TxBytes);
    }

    // A leg sent through the tunnel: the first target that answers is the one measured. A silent set leaves the
    // leg unknown rather than borrowing the next target's path, which is how a resolver past the exit came to
    // stand in for the peer.
    private static async Task<CheckLeg> InsideAsync(string name, IReadOnlyList<string>? targets, string silent, CancellationToken ct)
    {
        foreach (var target in targets ?? [])
        {
            if (!IPAddress.TryParse(target, out var address))
            {
                continue;
            }

            var (rtt, jitter, loss) = await EchoAsync(address, null, ct).ConfigureAwait(false);
            if (loss < 100)
            {
                return new CheckLeg(name, ChannelVerdict.StateFor(loss), rtt, jitter, loss, Note: address.ToString());
            }
        }

        return new CheckLeg(name, LegState.Unknown, Note: silent);
    }

    // Round trip, jitter and loss over one burst; a target silent from the start is abandoned early.
    private static async Task<(int Rtt, int Jitter, int Loss)> EchoAsync(IPAddress address, Func<Socket, bool>? bypass, CancellationToken ct)
    {
        var times = new List<int>();
        var sent = 0;
        var lost = 0;
        for (var attempt = 0; attempt < Echoes; attempt++)
        {
            if (attempt > 0)
            {
                await Task.Delay(EchoGapMs, ct).ConfigureAwait(false);
            }

            sent++;
            var rtt = await IcmpEcho.RoundTripAsync(address, EchoTimeoutMs, 32, false, bypass, ct).ConfigureAwait(false);
            if (rtt < 0)
            {
                lost++;
                if (lost >= DeadAfter && times.Count == 0)
                {
                    return (-1, -1, 100);
                }

                continue;
            }

            times.Add(rtt);
        }

        if (times.Count == 0)
        {
            return (-1, -1, 100);
        }

        var average = (int)times.Average();
        var jitter = times.Count > 1 ? (int)times.Select(one => Math.Abs(one - average)).Average() : 0;
        return (average, jitter, lost * 100 / sent);
    }

    // The largest payload the path carries whole: sizes are tried largest first with fragmentation refused,
    // then halved between the last refusal and the first pass, because a ladder step is too coarse to set an
    // MTU by.
    private static async Task<int> LargestAsync(IPAddress address, Func<Socket, bool>? bypass, CancellationToken ct)
    {
        var passed = 0;
        var refused = 0;
        foreach (var size in _sizes)
        {
            if (await IcmpEcho.RoundTripAsync(address, EchoTimeoutMs, size, true, bypass, ct).ConfigureAwait(false) >= 0)
            {
                passed = size;
                break;
            }

            refused = size;
        }

        if (passed == 0 || refused == 0)
        {
            return passed;
        }

        while (refused - passed > SizeStep)
        {
            var middle = passed + ((refused - passed) / 2);
            if (await IcmpEcho.RoundTripAsync(address, EchoTimeoutMs, middle, true, bypass, ct).ConfigureAwait(false) >= 0)
            {
                passed = middle;
                continue;
            }

            refused = middle;
        }

        return passed;
    }

    // Bits per second pulled over the budget, with what arrived; a bypass sends the same request beside the
    // tunnel. The byte count decides whether the rate is one at all: a destination that ends after a page timed
    // its own latency.
    private static async Task<(CheckLeg Leg, long Bytes)> ThroughputAsync(string name, string url, Func<Socket, bool>? bypass, CancellationToken ct)
    {
        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = System.Net.DecompressionMethods.None,
            ConnectTimeout = TimeSpan.FromSeconds(5),
        };

        if (bypass is not null)
        {
            handler.ConnectCallback = (context, token) => ConnectAsync(context, bypass, token);
        }

        try
        {
            using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(20) };
            using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
            budget.CancelAfter(DownloadMs + 5_000);

            var clock = Stopwatch.StartNew();
            using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, budget.Token).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            var stream = await response.Content.ReadAsStreamAsync(budget.Token).ConfigureAwait(false);
            await using (stream.ConfigureAwait(false))
            {
                var buffer = new byte[64 * 1024];
                var total = 0L;
                var started = clock.ElapsedMilliseconds;
                while (clock.ElapsedMilliseconds - started < DownloadMs)
                {
                    var read = await stream.ReadAsync(buffer, budget.Token).ConfigureAwait(false);
                    if (read <= 0)
                    {
                        break;
                    }

                    total += read;
                }

                var span = Math.Max(clock.ElapsedMilliseconds - started, 1);
                var bits = total * 8000 / span;
                return (new CheckLeg(name, ChannelVerdict.StateFor(bits), BitsPerSecond: bits), total);
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or SocketException or OperationCanceledException or InvalidOperationException)
        {
            return (new CheckLeg(name, LegState.Bad, BitsPerSecond: 0, Note: "the download never started"), 0);
        }
        finally
        {
            handler.Dispose();
        }
    }

    // A connection made beside the tunnel: the socket is excused from it before it dials.
    private static async ValueTask<Stream> ConnectAsync(SocketsHttpConnectionContext context, Func<Socket, bool> bypass, CancellationToken ct)
    {
        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            bypass(socket);
            await socket.ConnectAsync(context.DnsEndPoint, ct).ConfigureAwait(false);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch (Exception)
        {
            socket.Dispose();
            throw;
        }
    }
}
