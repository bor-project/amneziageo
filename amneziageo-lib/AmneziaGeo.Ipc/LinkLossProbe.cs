using System.Net;
using System.Net.Sockets;

namespace AmneziaGeo.Ipc;

/// <summary>
/// Measures what the tunnel drops. The peer counters carry bytes and the last handshake and nothing else, so loss is
/// knowable only by sending something through the tunnel and counting what fails to return - and it has to travel
/// inside the tunnel, an echo to the endpoint measuring the path the tunnel is carried over rather than the tunnel.
/// The targets are the resolvers the config declares and the peer's own address on the tunnel: both are carried by
/// the tunnel by construction, and both answer an echo. A target that never answers names an unusable probe rather
/// than a dead link, and the share stays unknown instead of reading as total loss.
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
    private readonly Queue<bool> _window = new();
    private readonly object _lock = new();
    private IPAddress? _chosen;
    private int _percent = LinkHealth.LossUnknown;

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
            var answered = await IcmpEcho.RoundTripAsync(target, TimeoutMs, ct).ConfigureAwait(false) >= 0;

            // The first target that answers is the one measured from here on: alternating between them would fold
            // two paths into one share.
            if (answered)
            {
                _chosen ??= target;
            }

            if (_chosen is not null)
            {
                Record(answered);
            }
        }
    }

    /// <summary>
    /// Drops the history a stopped tunnel left behind; the target already found is kept.
    /// </summary>
    public void Reset()
    {
        lock (_lock)
        {
            _window.Clear();
            Volatile.Write(ref _percent, LinkHealth.LossUnknown);
        }
    }

    /// <summary>
    /// Folds one attempt into the window.
    /// </summary>
    public void Record(bool answered)
    {
        lock (_lock)
        {
            _window.Enqueue(answered);
            while (_window.Count > Window)
            {
                _window.Dequeue();
            }

            if (_window.Count < MinAttempts)
            {
                return;
            }

            var lost = _window.Count(one => !one);
            Volatile.Write(ref _percent, lost * 100 / _window.Count);
        }
    }

    /// <summary>
    /// Addresses worth echoing for a config: the resolvers it declares, then the peer's own address on the tunnel.
    /// </summary>
    public static IReadOnlyList<string> TargetsFor(IEnumerable<string> dnsServers, IEnumerable<string> interfaceAddresses)
    {
        var targets = new List<string>();
        foreach (var server in dnsServers)
        {
            if (IPAddress.TryParse(server.Trim(), out var parsed) && parsed.AddressFamily == AddressFamily.InterNetwork)
            {
                Keep(targets, parsed.ToString());
            }
        }

        foreach (var address in interfaceAddresses)
        {
            if (PeerAddress(address) is { } peer)
            {
                Keep(targets, peer);
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
