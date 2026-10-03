namespace AmneziaGeo.Ipc;

/// <summary>
/// What a repair does to a tunnel that has stopped carrying, cheapest first. Everything below <see cref="Restart"/>
/// leaves the tunnel, its routes and its DNS standing.
/// </summary>
public enum RecoveryStep
{
    /// <summary>
    /// Binds the tunnel socket to another source port, which is what a NAT that has forgotten the session needs.
    /// </summary>
    Rebind,

    /// <summary>
    /// Resolves the endpoint again and hands the peer what came back, for a server that has moved.
    /// </summary>
    Resolve,

    /// <summary>
    /// Re-dials the carrier the tunnel is wrapped in.
    /// </summary>
    Carrier,

    /// <summary>
    /// Raises the session again, and with it the routes, the DNS and the firewall.
    /// </summary>
    Restart,
}

/// <summary>
/// One reading of a live tunnel, as the counters and the echoes inside it saw it.
/// </summary>
public readonly record struct LinkSample(
    bool TxMoved,
    bool RxMoved,
    int LossPercent,
    bool Churning,
    int HandshakeAgeSeconds,
    long Sent = 0,
    long Received = 0);

/// <summary>
/// Says when a live tunnel has stopped carrying and what to try next. No single counter names it: a session that
/// keeps re-establishing holds a young handshake while nothing crosses it, and a tunnel nobody is using looks the
/// same as a dead one from the outside. So three independent things are read - how often the session is
/// re-established while nothing comes back, what the echoes sent inside the tunnel lose, and whether anything at all
/// comes back while the handshake ages - and each of them alone is enough.
/// </summary>
public sealed class LinkRecovery
{
    /// <summary>
    /// Seconds of the evidence standing before the link is called dead. Long enough to sit through a rekey on a
    /// lossy link, short enough that a stalled player is not left waiting for a minute.
    /// </summary>
    public const int DeadSeconds = 12;

    /// <summary>
    /// Seconds of arriving traffic that stand the ladder down. Traffic is the only proof a repair worked: a
    /// handshake proves the server answers and nothing more.
    /// </summary>
    public const int HealthySeconds = 5;

    /// <summary>
    /// Seconds of repairing after which the ladder stands down and the cause is reported instead. A ladder that
    /// climbs forever costs a battery on the devices that can least afford it.
    /// </summary>
    public const int GiveUpSeconds = 600;

    /// <summary>
    /// Handshake age past which a tunnel receiving nothing counts as dead, where no echo has measured the link.
    /// </summary>
    public const int DefaultDeadHandshakeSeconds = 180;

    /// <summary>
    /// Seconds a tunnel that was carrying sends into silence before its source port is changed.
    /// </summary>
    public const int SilentSeconds = 6;

    /// <summary>
    /// Seconds of that silence that are enough where traffic and an echo that used to answer both went unanswered.
    /// </summary>
    public const int BusySilentSeconds = 4;

    /// <summary>
    /// Seconds of a silence within which the source port is changed on it.
    /// </summary>
    public const int SilentWindowSeconds = 30;

    /// <summary>
    /// Seconds of traffic arriving reading after reading that make a tunnel count as carrying.
    /// </summary>
    public const int LiveSeconds = 4;

    /// <summary>
    /// Seconds before the source port of a silent tunnel is changed again.
    /// </summary>
    public const int SilentHoldSeconds = 60;

    /// <summary>
    /// Bytes sent into a silence that count as sending where no echo measures the link.
    /// </summary>
    public const int SilentBytes = 2000;

    /// <summary>
    /// Bytes arriving in one reading that prove the tunnel carries, whatever its echoes lose. A dead link brings
    /// back a handshake answer or a keepalive at most, far below it.
    /// </summary>
    public const int FlowingBytes = 16 * 1024;

    // Waits between rungs; the last one is served for every attempt past it.
    private static readonly int[] _backoffSeconds = [2, 4, 8, 15, 30, 60];

    private readonly RecoveryStep[] _steps;
    private readonly int _deadHandshakeSeconds;
    private readonly int _jitterPercent;
    private readonly Random _jitter;
    private long _stalledSinceMs;
    private long _healthySinceMs;
    private long _repairingSinceMs;
    private long _nextActionMs;
    private int _rung = -1;
    private long _heardSinceMs;
    private long _heardAtMs;
    private long _sentSilent;
    private long _reboundAtMs;
    private bool _live;
    private bool _rebound;

    /// <summary>
    /// ctor
    /// </summary>
    public LinkRecovery(IReadOnlyList<RecoveryStep> steps, int deadHandshakeSeconds = DefaultDeadHandshakeSeconds, int jitterPercent = 20, int seed = 0)
    {
        _steps = [.. steps];
        _deadHandshakeSeconds = deadHandshakeSeconds > 0 ? deadHandshakeSeconds : DefaultDeadHandshakeSeconds;
        _jitterPercent = Math.Clamp(jitterPercent, 0, 50);
        _jitter = seed == 0 ? new Random() : new Random(seed);
    }

    /// <summary>
    /// Whether the link is being repaired right now.
    /// </summary>
    public bool Repairing => _repairingSinceMs > 0;

    /// <summary>
    /// Repairs attempted since the link went dead.
    /// </summary>
    public int Attempt { get; private set; }

    /// <summary>
    /// What the link was judged dead on; empty while it carries.
    /// </summary>
    public string Reason { get; private set; } = string.Empty;

    /// <summary>
    /// Whether the ladder has stood down without the link coming back.
    /// </summary>
    public bool GivenUp { get; private set; }

    /// <summary>
    /// Folds one reading of the link into the ladder and returns the repair to perform now, or null to keep
    /// waiting.
    /// </summary>
    public RecoveryStep? Sample(LinkSample sample, long nowMs)
    {
        Follow(sample, nowMs);
        var reason = Stalling(sample);
        if (reason.Length == 0)
        {
            _stalledSinceMs = 0;

            // A tunnel nobody is using proves nothing either way, so only traffic stands the ladder down.
            if (sample.RxMoved)
            {
                return Carrying(nowMs);
            }

            return Muted(sample, nowMs) ? Rebound(nowMs) : null;
        }

        _healthySinceMs = 0;
        if (_stalledSinceMs == 0)
        {
            _stalledSinceMs = nowMs;
        }

        // A ladder already climbing is paced by the wait between rungs, not by this window: a link trickling one
        // packet through every few seconds would otherwise reset the window forever and never be repaired.
        if (!Repairing && nowMs - _stalledSinceMs < DeadSeconds * 1000L)
        {
            return null;
        }

        Reason = reason;
        return Repair(nowMs);
    }

    /// <summary>
    /// Drops what a stopped tunnel left behind.
    /// </summary>
    public void Reset()
    {
        _stalledSinceMs = 0;
        _healthySinceMs = 0;
        _repairingSinceMs = 0;
        _nextActionMs = 0;
        _rung = -1;
        _heardSinceMs = 0;
        _heardAtMs = 0;
        _sentSilent = 0;
        _reboundAtMs = 0;
        _live = false;
        _rebound = false;
        Attempt = 0;
        Reason = string.Empty;
        GivenUp = false;
    }

    // Traffic arriving clears the ladder, but only once it has kept arriving: a single packet crossing during a
    // repair is not the link coming back, and clearing on it would restart the climb from the bottom every time.
    private RecoveryStep? Carrying(long nowMs)
    {
        if (_healthySinceMs == 0)
        {
            _healthySinceMs = nowMs;
        }

        if (Repairing && nowMs - _healthySinceMs >= HealthySeconds * 1000L)
        {
            Reset();
        }

        // Forgets the port change of a silence that is over.
        if (!Repairing && Attempt > 0)
        {
            _rung = -1;
            Attempt = 0;
            Reason = string.Empty;
        }

        return null;
    }

    // Follows the reception: since when it has been arriving, when it last did and what was sent after it.
    private void Follow(LinkSample sample, long nowMs)
    {
        if (sample.RxMoved)
        {
            if (_heardSinceMs == 0)
            {
                _heardSinceMs = nowMs;
            }

            _heardAtMs = nowMs;
            _sentSilent = 0;
            _rebound = false;
            return;
        }

        if (_heardSinceMs > 0)
        {
            _live = _heardAtMs - _heardSinceMs >= LiveSeconds * 1000L;
            _heardSinceMs = 0;
        }

        _sentSilent += sample.Sent;
    }

    // Tells a tunnel that was carrying, keeps sending and has heard nothing for seconds.
    private bool Muted(LinkSample sample, long nowMs)
    {
        var silence = nowMs - _heardAtMs;
        var measured = LinkHealth.LossKnown(sample.LossPercent);
        var sending = _sentSilent >= SilentBytes;

        return _live
            && !_rebound
            && !Repairing
            && !GivenUp
            && _steps.Length > 0
            && _steps[0] == RecoveryStep.Rebind
            && sample.TxMoved
            && silence >= (measured && sending ? BusySilentSeconds : SilentSeconds) * 1000L
            && silence <= SilentWindowSeconds * 1000L
            && (measured || sending)
            && (_reboundAtMs == 0 || nowMs - _reboundAtMs >= SilentHoldSeconds * 1000L);
    }

    // Takes the first rung ahead of the ladder.
    private RecoveryStep Rebound(long nowMs)
    {
        _rebound = true;
        _reboundAtMs = nowMs;
        _rung = 0;
        Attempt = 1;
        Reason = "the tunnel was carrying, keeps sending and hears nothing";

        return RecoveryStep.Rebind;
    }

    // What names a dead link, in the order the evidence is worth trusting.
    private string Stalling(LinkSample sample)
    {
        // The one case the counters hide: every completed handshake makes the link look freshly alive, so a
        // session that spends its time being re-established reads as healthy for as long as it stays broken.
        if (sample.Churning)
        {
            return "the session is being re-established instead of carrying";
        }

        // An echo waits behind whatever fills the link: a download over a slow one keeps every echo past its
        // timeout while the data itself keeps arriving, and that link is busy, not dead.
        if (LinkHealth.LossKnown(sample.LossPercent) && sample.LossPercent >= 100 && sample.Received < FlowingBytes)
        {
            return "every echo sent inside the tunnel was lost";
        }

        // Last resort, for a link no echo has measured: nothing comes back while the tunnel sends and the
        // handshake ages past the point a live session would have renewed it.
        if (sample.TxMoved && !sample.RxMoved && sample.HandshakeAgeSeconds >= _deadHandshakeSeconds)
        {
            return "the tunnel keeps sending and nothing comes back";
        }

        return string.Empty;
    }

    private RecoveryStep? Repair(long nowMs)
    {
        if (GivenUp || _steps.Length == 0)
        {
            return null;
        }

        if (_repairingSinceMs == 0)
        {
            _repairingSinceMs = nowMs;
        }
        else if (nowMs - _repairingSinceMs >= GiveUpSeconds * 1000L)
        {
            GivenUp = true;
            return null;
        }

        if (nowMs < _nextActionMs)
        {
            return null;
        }

        // Each attempt climbs a rung and waits longer, the last rung and the last wait serving every attempt
        // past them: a server that is down is not brought back by dialling it faster.
        _rung = Math.Min(_rung + 1, _steps.Length - 1);
        Attempt++;
        _nextActionMs = nowMs + Wait(Attempt);
        return _steps[_rung];
    }

    private long Wait(int attempt)
    {
        var seconds = _backoffSeconds[Math.Min(attempt - 1, _backoffSeconds.Length - 1)];
        var spread = _jitterPercent == 0 ? 0 : _jitter.Next(-seconds * _jitterPercent / 100, (seconds * _jitterPercent / 100) + 1);
        return Math.Max(1, seconds + spread) * 1000L;
    }
}
