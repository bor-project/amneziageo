namespace AmneziaGeo.Ipc;

/// <summary>
/// Terms both sides read the link metrics by.
/// </summary>
public static class LinkHealth
{
    /// <summary>
    /// Window the handshake rate is counted over.
    /// </summary>
    public const int WindowSeconds = 120;

    /// <summary>
    /// Handshake rate at which the session is being re-established instead of living: a healthy peer rekeys
    /// once every two to three minutes, a lossy link repeats every fifteen seconds.
    /// </summary>
    public const int ChurnPerMinute = 3;

    /// <summary>
    /// Loss share of a link no probe has been able to measure.
    /// </summary>
    public const int LossUnknown = -1;

    /// <summary>
    /// Loss share at which the link is called lossy: below it a stream repairs itself unnoticed, above it every
    /// other retransmission is paid for in latency.
    /// </summary>
    public const int LossyPercent = 5;

    /// <summary>
    /// Whether the rate names a link that keeps re-establishing.
    /// </summary>
    public static bool Churning(int handshakesPerMinute)
    {
        return handshakesPerMinute >= ChurnPerMinute;
    }

    /// <summary>
    /// Whether the share was measured at all.
    /// </summary>
    public static bool LossKnown(int lossPercent)
    {
        return lossPercent >= 0;
    }

    /// <summary>
    /// Whether the share names a link that drops enough to be felt.
    /// </summary>
    public static bool Lossy(int lossPercent)
    {
        return lossPercent >= LossyPercent;
    }
}

/// <summary>
/// What the peer counters said over the last interval, and what the tunnel lost while they said it.
/// </summary>
public sealed record LinkReading(
    long RxBitsPerSecond,
    long TxBitsPerSecond,
    int HandshakesPerMinute,
    int LossPercent = LinkHealth.LossUnknown,
    // Round trip to the far end of the tunnel, timed by the echo the loss share is counted from; -1 until one
    // comes back.
    int RttMs = -1)
{
    /// <summary>
    /// A link that has carried nothing yet.
    /// </summary>
    public static readonly LinkReading Empty = new(0, 0, 0);

    // Rate step the screen resolves; a smaller move is not worth a snapshot.
    private const long Resolution = 100_000;

    // Round trip step the screen resolves; the average wanders by a millisecond on its own.
    private const int RttResolution = 5;

    /// <summary>
    /// Whether the difference from another reading is worth showing.
    /// </summary>
    public bool DiffersFrom(LinkReading other)
    {
        return HandshakesPerMinute != other.HandshakesPerMinute
            || LossPercent != other.LossPercent
            || Math.Abs(RttMs - other.RttMs) >= RttResolution
            || Math.Abs(RxBitsPerSecond - other.RxBitsPerSecond) >= Resolution
            || Math.Abs(TxBitsPerSecond - other.TxBitsPerSecond) >= Resolution;
    }
}

/// <summary>
/// Turns peer counters sampled over time into transfer rates and a handshake rate.
/// </summary>
public sealed class LinkMeter
{
    private readonly Queue<long> _handshakes = new();
    private long _rxBytes = -1;
    private long _txBytes = -1;
    private long _tick;
    private long _handshakeUnix;

    /// <summary>
    /// The last reading; replaced whole, so a reader never sees half of one.
    /// </summary>
    public LinkReading Reading { get; private set; } = LinkReading.Empty;

    /// <summary>
    /// Folds one peer reading into the rates and returns the result. The loss share comes from the probe, the
    /// counters carrying no trace of what never arrived.
    /// </summary>
    public LinkReading Sample(long rxBytes, long txBytes, long handshakeUnix, int lossPercent = LinkHealth.LossUnknown, int rttMs = -1)
    {
        var now = Environment.TickCount64;
        if (handshakeUnix > _handshakeUnix)
        {
            // The first handshake seen predates the meter, so it seeds the baseline instead of counting.
            if (_handshakeUnix > 0)
            {
                _handshakes.Enqueue(now);
            }

            _handshakeUnix = handshakeUnix;
        }

        while (_handshakes.Count > 0 && now - _handshakes.Peek() > LinkHealth.WindowSeconds * 1000L)
        {
            _handshakes.Dequeue();
        }

        var perMinute = (int)(((_handshakes.Count * 60L) + (LinkHealth.WindowSeconds / 2)) / LinkHealth.WindowSeconds);
        var span = now - _tick;
        var rx = 0L;
        var tx = 0L;

        // A counter that went backwards belongs to a new session: reseed rather than report a negative rate.
        if (_rxBytes >= 0 && span > 0 && rxBytes >= _rxBytes && txBytes >= _txBytes)
        {
            rx = (rxBytes - _rxBytes) * 8000 / span;
            tx = (txBytes - _txBytes) * 8000 / span;
        }

        _rxBytes = rxBytes;
        _txBytes = txBytes;
        _tick = now;
        Reading = new LinkReading(rx, tx, perMinute, lossPercent, rttMs);
        return Reading;
    }

    /// <summary>
    /// Drops what a stopped tunnel left behind.
    /// </summary>
    public void Reset()
    {
        _handshakes.Clear();
        _rxBytes = -1;
        _txBytes = -1;
        _tick = 0;
        _handshakeUnix = 0;
        Reading = LinkReading.Empty;
    }
}
