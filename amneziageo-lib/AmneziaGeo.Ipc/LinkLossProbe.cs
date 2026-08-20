using System.Net;
using System.Net.Sockets;

namespace AmneziaGeo.Ipc;

/// <summary>
/// Measures what the tunnel drops. The peer counters carry bytes and the last handshake and nothing else, so loss is
/// knowable only by sending something through the tunnel and counting what fails to return - and it has to travel
/// inside the tunnel, an echo to the endpoint measuring the path the tunnel is carried over rather than the tunnel.
/// The target is the peer's own address on the tunnel and nothing else: it is the far end of the tunnel itself. A
/// resolver the config declares sits past the exit, so what it loses belongs to the public path behind the server
/// and would read here as loss of the channel. A target that never answers names an unusable probe rather than a
/// dead link, and the share stays unknown instead of reading as total loss.
/// </summary>
public sealed class LinkLossProbe
{
    private const int IntervalMs = 1_000;
    private const int TimeoutMs = 1_500;

    // Attempts the share is taken over: half a minute of history, long enough that one lost echo does not read as a
    // broken link and short enough that a link recovering shows it.
    private const int Window = 30;

    // Attempts before the first share is reported.
    private const int MinAttempts = 10;

    // Targets tried; more than a few would only spend the first minute looking for a responder.
    private const int MaxTargets = 3;

    private readonly IPAddress[] _targets;
    private readonly Queue<int> _window = new();
    private readonly object _lock = new();
    private IPAddress? _chosen;

    // Whether this session has been answered. Its window starts there: the seconds a fresh tunnel spends
    // putting its routes in place drop echoes that belong to the setup and not to the channel, and a target
    // kept from an earlier session would fold them in.
    private bool _answered;

    private int _attempts;
    private int _percent = LinkHealth.LossUnknown;
    private int _rttMs = -1;

    /// <summary>
    /// ctor
    /// </summary>
    public LinkLossProbe(IReadOnlyList<string> targets)
    {
        var parsed = new List<IPAddress>();
        foreach (var target in targets)
        {
            if (IPAddress.TryParse(target, out var address))
            {
                parsed.Add(address);
            }
        }

        _targets = [.. parsed];
    }

    /// <summary>
    /// The share of echoes lost over the window; unknown while nothing has answered yet.
    /// </summary>
    public int Percent => Volatile.Read(ref _percent);

    /// <summary>
    /// Average round trip of the echoes that came back over the window; -1 while none has. It is the far end of
    /// the tunnel that answers, so this is the channel's own time, measured where an echo to the endpoint is
    /// swallowed by the tunnel it carries.
    /// </summary>
    public int RttMs => Volatile.Read(ref _rttMs);

    /// <summary>
    /// Whether any target has answered. Nothing here separates a target the tunnel does not carry from one that
    /// simply never replies, and both leave the share unknown for the whole session - a state worth reading,
    /// because an unknown share is not a healthy one.
    /// </summary>
    public bool Answering => Volatile.Read(ref _chosen) is not null;

    /// <summary>
    /// Echoes sent since the session started.
    /// </summary>
    public int Attempts => Volatile.Read(ref _attempts);

    /// <summary>
    /// Echoes the target once a second for as long as the session runs.
    /// </summary>
    public async Task RunAsync(CancellationToken ct)
    {
        if (_targets.Length == 0)
        {
            return;
        }

        var attempt = 0;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(IntervalMs, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            var target = _chosen ?? _targets[attempt++ % _targets.Length];
            var trip = await IcmpEcho.RoundTripAsync(target, TimeoutMs, ct).ConfigureAwait(false);
            Interlocked.Increment(ref _attempts);

            // The first target that answers is the one measured from here on: alternating between them would fold
            // two paths into one share.
            if (trip >= 0)
            {
                _chosen ??= target;
                _answered = true;
            }

            if (_chosen is not null && _answered)
            {
                Record(trip);
            }
        }
    }

    /// <summary>
    /// Drops the history a stopped tunnel left behind; the target already found is kept, while the window
    /// waits for the next session to answer before it counts anything again.
    /// </summary>
    public void Reset()
    {
        lock (_lock)
        {
            _answered = false;
            _window.Clear();
            Volatile.Write(ref _percent, LinkHealth.LossUnknown);
            Volatile.Write(ref _rttMs, -1);
        }
    }

    /// <summary>
    /// Folds one attempt into the window; a negative round trip stands for an echo that never came back.
    /// </summary>
    public void Record(int rttMs)
    {
        lock (_lock)
        {
            _window.Enqueue(rttMs);
            while (_window.Count > Window)
            {
                _window.Dequeue();
            }

            // The time stands on the first answer, unlike the share: a round trip needs no history, and waiting
            // for one would leave a freshly connected server with no time on it at all.
            var answered = 0;
            var total = 0L;
            foreach (var one in _window)
            {
                if (one >= 0)
                {
                    answered++;
                    total += one;
                }
            }

            Volatile.Write(ref _rttMs, answered > 0 ? (int)(total / answered) : -1);
            if (_window.Count < MinAttempts)
            {
                return;
            }

            Volatile.Write(ref _percent, (_window.Count - answered) * 100 / _window.Count);
        }
    }

    /// <summary>
    /// Every address worth echoing, the peer first. The peer alone measures the channel and nothing behind it,
    /// but a server that hands its clients a single-host address answers at no peer address at all, and a
    /// tunnel carrying named destinations routes none of that subnet into itself either - so the resolvers the
    /// config declares follow, measuring more than the channel but carried by it, and answering.
    /// </summary>
    public static IReadOnlyList<string> Targets(IEnumerable<string> interfaceAddresses, IEnumerable<string> dnsServers)
    {
        var targets = new List<string>();
        foreach (var peer in PeerTargets(interfaceAddresses))
        {
            Keep(targets, peer);
        }

        foreach (var server in BeyondTargets(dnsServers))
        {
            Keep(targets, server);
        }

        return targets;
    }

    /// <summary>
    /// The far end of the tunnel: the peer's own address on every subnet the interface sits in. Nothing else
    /// measures the tunnel, every other address being reached through it and out the far side.
    /// </summary>
    public static IReadOnlyList<string> PeerTargets(IEnumerable<string> interfaceAddresses)
    {
        var targets = new List<string>();
        foreach (var address in interfaceAddresses)
        {
            if (PeerAddress(address) is { } peer)
            {
                Keep(targets, peer);
            }
        }

        return targets;
    }

    /// <summary>
    /// The resolvers the config declares: reached through the tunnel and out past the exit, so they measure the
    /// public path behind the server rather than the channel to it.
    /// </summary>
    public static IReadOnlyList<string> BeyondTargets(IEnumerable<string> dnsServers)
    {
        var targets = new List<string>();
        foreach (var server in dnsServers)
        {
            // A resolver on loopback is this machine's own proxy: it answers an echo without the tunnel carrying
            // anything, which is worse than measuring nothing at all.
            if (IPAddress.TryParse(server.Trim(), out var parsed)
                && parsed.AddressFamily == AddressFamily.InterNetwork
                && !IPAddress.IsLoopback(parsed))
            {
                Keep(targets, parsed.ToString());
            }
        }

        return targets;
    }

    private static void Keep(List<string> targets, string target)
    {
        if (targets.Count < MaxTargets && !targets.Contains(target, StringComparer.Ordinal))
        {
            targets.Add(target);
        }
    }

    // The peer's own address on the tunnel: the first host of the subnet the interface sits in. A single-host
    // address declares no subnet, and a server hands its clients addresses out of one /24, so that is what is read
    // into it - a wrong guess costs one target that never answers.
    private static string? PeerAddress(string cidr)
    {
        var slash = cidr.IndexOf('/');
        var host = slash < 0 ? cidr.Trim() : cidr[..slash].Trim();
        if (!IPAddress.TryParse(host, out var parsed) || parsed.AddressFamily != AddressFamily.InterNetwork)
        {
            return null;
        }

        var declared = slash < 0 || !int.TryParse(cidr[(slash + 1)..].Trim(), out var prefix) ? 24 : prefix;
        var width = declared is >= 8 and <= 30 ? declared : 24;
        var bytes = parsed.GetAddressBytes();
        var value = ((uint)bytes[0] << 24) | ((uint)bytes[1] << 16) | ((uint)bytes[2] << 8) | bytes[3];
        var first = (value & (uint.MaxValue << (32 - width))) + 1;
        return first == value ? null : new IPAddress([(byte)(first >> 24), (byte)(first >> 16), (byte)(first >> 8), (byte)first]).ToString();
    }
}
