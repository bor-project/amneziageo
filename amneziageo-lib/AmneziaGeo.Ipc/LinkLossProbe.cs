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
    /// <summary>
    /// Pause between echoes once a target has answered.
    /// </summary>
    public const int IntervalMs = 5_000;

    /// <summary>
    /// Pause between echoes while the screen shows the link.
    /// </summary>
    public const int WatchedIntervalMs = 1_000;

    /// <summary>
    /// How long one look at the link keeps its echoes at the watched pace.
    /// </summary>
    public const int WatchMs = 5_000;

    /// <summary>
    /// Silence from which echoes lost one after another make an outage.
    /// </summary>
    public const int OutageMs = 7_000;

    /// <summary>
    /// Echoes lost one after another an outage takes at the least.
    /// </summary>
    public const int OutageEchoes = 2;

    /// <summary>
    /// Echoes answered one after another that end an outage.
    /// </summary>
    public const int BackEchoes = 3;

    private const int TimeoutMs = 1_500;

    // Pause between echoes while no target has answered yet.
    private const int SearchIntervalMs = 1_000;

    // Echoes sent at the search pace before the probe settles to its own.
    private const int SearchAttempts = 20;

    // History the share is taken over.
    private const int WindowMs = 60_000;

    // History the time and the recent share are taken over.
    private const int RecentMs = 30_000;

    // Attempts before the first share is reported.
    private const int MinAttempts = 4;

    // Targets tried; more than a few would only spend the first minute looking for a responder.
    private const int MaxTargets = 3;

    // Echoes the first target is given after another one was settled on.
    private const int SecondLooks = 3;

    // Echoes the share the screen shows is taken over, and echoes the time it shows is the best of.
    private const int ShownEchoes = 30;
    private const int ShownTimed = 10;

    private readonly IPAddress[] _targets;
    private readonly Queue<(long Tick, int Rtt)> _window = new();
    private readonly object _lock = new();
    private readonly int _intervalMs;
    private readonly Func<long> _clock;
    private readonly Func<IPAddress, int, CancellationToken, Task<int>> _echo;
    private readonly Func<int, CancellationToken, Task> _rest;

    // Whether the echoes leave through the tunnel alone, so what answers them was carried by it.
    private readonly bool _confined;
    private IPAddress? _chosen;

    // Whether this session has been answered. Its window starts there: the seconds a fresh tunnel spends
    // putting its routes in place drop echoes that belong to the setup and not to the channel, and a target
    // kept from an earlier session would fold them in.
    private bool _answered;

    private int _attempts;
    private int _looks;
    private int _percent = LinkHealth.LossUnknown;
    private int _recentPercent = LinkHealth.LossUnknown;
    private int _streak;
    private int _rttMs = -1;

    // What the screen is shown, and the outages it is counted between.
    private LinkNow _now = LinkNow.Unknown;
    private long _heardAt;
    private int _lostRun;
    private int _backRun;
    private bool _out;
    private int _skip;
    private bool _cut;

    // Until when the echoes go at the watched pace, and the handle a long pause is cut short by.
    private long _watchedUntil;
    private CancellationTokenSource? _wake;

    /// <summary>
    /// ctor
    /// </summary>
    public LinkLossProbe(
        IReadOnlyList<string> targets,
        int intervalMs = IntervalMs,
        Func<long>? clock = null,
        Func<IPAddress, int, CancellationToken, Task<int>>? echo = null,
        Func<int, CancellationToken, Task>? rest = null)
    {
        _intervalMs = intervalMs > 0 ? intervalMs : IntervalMs;
        _clock = clock ?? (() => Environment.TickCount64);
        _echo = echo ?? IcmpEcho.RoundTripAsync;
        _rest = rest ?? Task.Delay;
        _confined = echo is not null;
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
    /// The share of echoes lost over the last minute; unknown while nothing has answered yet.
    /// </summary>
    public int Percent => Volatile.Read(ref _percent);

    /// <summary>
    /// The share of echoes lost over the last half minute; unknown while nothing has answered yet.
    /// </summary>
    public int RecentPercent => Volatile.Read(ref _recentPercent);

    /// <summary>
    /// The longest run of echoes lost one after another over the last minute.
    /// </summary>
    public int Streak => Volatile.Read(ref _streak);

    /// <summary>
    /// Quickest round trip of the echoes that came back over the last half minute; -1 while none has. It is the far end of
    /// the tunnel that answers, so this is the channel's own time, measured where an echo to the endpoint is
    /// swallowed by the tunnel it carries.
    /// </summary>
    public int RttMs => Volatile.Read(ref _rttMs);

    /// <summary>
    /// What the screen shows of the link: the share lost over the last thirty echoes, the best time of the last ten
    /// and the longest run lost, all counted over the outage that is on, or over the link since it came back.
    /// </summary>
    public LinkNow Now => Volatile.Read(ref _now);

    /// <summary>
    /// Whether the echoes go at the watched pace.
    /// </summary>
    public bool Watched => _clock() < Interlocked.Read(ref _watchedUntil);

    /// <summary>
    /// Whether any target has answered. Nothing here separates a target the tunnel does not carry from one that
    /// simply never replies, and both leave the share unknown for the whole session - a state worth reading,
    /// because an unknown share is not a healthy one.
    /// </summary>
    public bool Answering => Volatile.Read(ref _chosen) is not null;

    /// <summary>
    /// The address the echoes are measured at; null while none has answered.
    /// </summary>
    public string? Target => Volatile.Read(ref _chosen)?.ToString();

    /// <summary>
    /// Echoes sent since the session started.
    /// </summary>
    public int Attempts => Volatile.Read(ref _attempts);

    /// <summary>
    /// Echoes the target for as long as the session runs: every second until one answers, then at the probe's own
    /// pace, and every second again while the screen shows the link.
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
                await RestAsync(attempt, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            var target = _chosen ?? _targets[attempt++ % _targets.Length];
            var trip = await _echo(target, TimeoutMs, ct).ConfigureAwait(false);
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

            await LookAgainAsync(ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Keeps the echoes at the watched pace for a while from now; the screen calls it for as long as it shows the link.
    /// </summary>
    public void Watch()
    {
        var watched = Watched;
        Interlocked.Exchange(ref _watchedUntil, _clock() + WatchMs);
        if (watched)
        {
            return;
        }

        try
        {
            Volatile.Read(ref _wake)?.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    // The pause before the next echo: a second while a target is looked for or the screen shows the link, else the probe's own.
    private int Pause(int attempt)
    {
        if (_chosen is null && attempt < SearchAttempts)
        {
            return Math.Min(SearchIntervalMs, _intervalMs);
        }

        return Watched ? Math.Min(WatchedIntervalMs, _intervalMs) : _intervalMs;
    }

    // Waits out the pause before the next echo, cut short by a look at the link.
    private async Task RestAsync(int attempt, CancellationToken ct)
    {
        var started = _clock();
        while (true)
        {
            using var wake = CancellationTokenSource.CreateLinkedTokenSource(ct);
            Volatile.Write(ref _wake, wake);
            try
            {
                var left = Pause(attempt) - (_clock() - started);
                if (left <= 0)
                {
                    return;
                }

                await _rest((int)left, wake.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
            }
            finally
            {
                Volatile.Write(ref _wake, null);
            }
        }
    }

    // Echoes the first target again after a later one was settled on, and moves the measurement to it once it answers.
    private async Task LookAgainAsync(CancellationToken ct)
    {
        if (!_confined || _chosen is not { } chosen || chosen.Equals(_targets[0]) || _looks >= SecondLooks)
        {
            return;
        }

        _looks++;
        var trip = await _echo(_targets[0], TimeoutMs, ct).ConfigureAwait(false);
        if (trip < 0)
        {
            return;
        }

        _chosen = _targets[0];
        Reset();
        _answered = true;
        Record(trip);
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
            Volatile.Write(ref _recentPercent, LinkHealth.LossUnknown);
            Volatile.Write(ref _streak, 0);
            Volatile.Write(ref _rttMs, -1);
            _heardAt = 0;
            _lostRun = 0;
            _backRun = 0;
            _out = false;
            _skip = 0;
            _cut = false;
            Volatile.Write(ref _now, LinkNow.Unknown);
        }
    }

    /// <summary>
    /// Folds one attempt into the window; a negative round trip stands for an echo that never came back.
    /// </summary>
    public void Record(int rttMs)
    {
        lock (_lock)
        {
            var now = _clock();
            _window.Enqueue((now, rttMs));
            while (now - _window.Peek().Tick >= WindowMs)
            {
                _window.Dequeue();
                _skip = Math.Max(0, _skip - 1);
            }

            var answered = 0;
            var recent = 0;
            var recentAnswered = 0;
            var quickest = int.MaxValue;
            var run = 0;
            var streak = 0;
            foreach (var (tick, one) in _window)
            {
                var fresh = now - tick < RecentMs;
                recent += fresh ? 1 : 0;
                if (one < 0)
                {
                    run++;
                    streak = Math.Max(streak, run);
                    continue;
                }

                run = 0;
                answered++;
                if (fresh)
                {
                    recentAnswered++;
                    quickest = Math.Min(quickest, one);
                }
            }

            // The time stands on the first answer, unlike the share: a round trip needs no history, and waiting
            // for one would leave a freshly connected server with no time on it at all.
            Volatile.Write(ref _rttMs, quickest < int.MaxValue ? quickest : -1);
            Volatile.Write(ref _streak, streak);
            if (recent >= MinAttempts)
            {
                Volatile.Write(ref _recentPercent, (recent - recentAnswered) * 100 / recent);
            }

            if (_window.Count >= MinAttempts)
            {
                Volatile.Write(ref _percent, (_window.Count - answered) * 100 / _window.Count);
            }

            Follow(rttMs, now);
            Volatile.Write(ref _now, Shown(now));
        }
    }

    // Follows the outages: a long enough silence begins one and echoes answered in a row end it.
    private void Follow(int rttMs, long now)
    {
        if (rttMs < 0)
        {
            _backRun = 0;
            _lostRun++;
            if (!_out && _lostRun >= OutageEchoes && now - _heardAt >= OutageMs)
            {
                _out = true;
                Cut(_lostRun);
            }

            return;
        }

        _heardAt = now;
        _lostRun = 0;
        if (!_out)
        {
            return;
        }

        _backRun++;
        if (_backRun < BackEchoes)
        {
            return;
        }

        _out = false;
        _backRun = 0;
        Cut(BackEchoes);
    }

    // Starts what the screen is shown over from the latest echoes.
    private void Cut(int echoes)
    {
        _cut = true;
        _skip = Math.Max(0, _window.Count - echoes);
    }

    // Counts what the screen is shown over the echoes of the outage that is on or of the link since it came back.
    private LinkNow Shown(long now)
    {
        var from = Math.Max(_skip, _window.Count - ShownEchoes);
        var timed = Math.Max(_skip, _window.Count - ShownTimed);
        var at = 0;
        var seen = 0;
        var lost = 0;
        var run = 0;
        var streak = 0;
        var best = int.MaxValue;
        foreach (var (tick, one) in _window)
        {
            var index = at++;
            if (index < from)
            {
                continue;
            }

            seen++;
            if (one < 0)
            {
                lost++;
                run++;
                streak = Math.Max(streak, run);
                continue;
            }

            run = 0;
            if (index >= timed && now - tick < RecentMs)
            {
                best = Math.Min(best, one);
            }
        }

        var counted = seen >= MinAttempts || (_cut && seen > 0);
        return new LinkNow(counted ? lost * 100 / seen : _now.LossPercent, best < int.MaxValue ? best : -1, streak);
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
