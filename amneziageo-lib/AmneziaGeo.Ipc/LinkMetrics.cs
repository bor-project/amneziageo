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
    /// Whether the rate names a link that keeps re-establishing.
    /// </summary>
    public static bool Churning(int handshakesPerMinute)
    {
        return handshakesPerMinute >= ChurnPerMinute;
    }
}

/// <summary>
/// What the peer counters said over the last interval.
/// </summary>
public sealed record LinkReading(long RxBitsPerSecond, long TxBitsPerSecond, int HandshakesPerMinute)
{
    /// <summary>
    /// A link that has carried nothing yet.
    /// </summary>
    public static readonly LinkReading Empty = new(0, 0, 0);

    // Rate step the screen resolves; a smaller move is not worth a snapshot.
    private const long Resolution = 100_000;

    /// <summary>
    /// Whether the difference from another reading is worth showing.
    /// </summary>
    public bool DiffersFrom(LinkReading other)
    {
        return HandshakesPerMinute != other.HandshakesPerMinute
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
    /// Folds one peer reading into the rates and returns the result.
    /// </summary>
    public LinkReading Sample(long rxBytes, long txBytes, long handshakeUnix)
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
        Reading = new LinkReading(rx, tx, perMinute);
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
