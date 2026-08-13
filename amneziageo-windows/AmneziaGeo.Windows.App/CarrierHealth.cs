using AmneziaGeo.Ipc;

namespace AmneziaGeo.Windows.App;

/// <summary>
/// What a second of a websocket-carried link says about the carrier under it. A carrier that stopped carrying
/// leaves the session standing - keepalives cross, a chat answers, and a transfer never finishes - so no single
/// sample names it and every verdict is taken over a window.
/// </summary>
internal sealed class CarrierHealth
{
    // Seconds kept; the longest window any verdict reads.
    private const int Window = 20;

    // Seconds of outgoing traffic with nothing coming back before the carrier counts as dead.
    private const int StallSeconds = 12;

    // Seconds inside that window the tunnel must have kept sending, so an idle machine is not called stalled.
    private const int StallSends = 8;

    // Returning packets the stall still tolerates. A strict count is cleared by the first keepalive that comes
    // back, which is why a link carrying nothing but keepalives never used to read as stalled at all.
    private const int StallReturns = 1;

    // Loss and round trip inside the tunnel that mark a channel no longer able to carry a transfer.
    private const int LossPercentLimit = 40;
    private const int RttMsLimit = 2000;

    // Seconds a degraded reading holds before it is acted on.
    private const int DegradedSeconds = 20;

    // Share of the carrier's outgoing bytes it had to send again before the carrier is called the reason a
    // transfer stalls, and the volume the window needs for that share to mean anything: on a near-idle
    // connection one repeated segment is most of what was sent.
    private const int RetransPercentLimit = 25;
    private const long RetransFloorBytes = 64 * 1024;

    private readonly List<Tick> _window = [];
    private int _degraded;

    /// <summary>
    /// Share of the carrier's outgoing bytes it had to send again over the window; -1 while the window holds
    /// too little traffic for a share to say anything.
    /// </summary>
    public int RetransPercent { get; private set; } = -1;

    /// <summary>
    /// Whether the channel currently reads as degraded, for the journal to note the crossing.
    /// </summary>
    public bool Degrading => _degraded > 0;

    /// <summary>
    /// Folds one second into the window and names the reason to re-dial, or nothing while the channel carries.
    /// The byte counts are what the carrier put on the wire during that second, not totals.
    /// </summary>
    public string Verdict(bool sent, bool returned, long bytesOut, long bytesRetrans, int lossPercent, int rttMs)
    {
        _window.Add(new Tick(sent, returned, bytesOut, bytesRetrans));
        while (_window.Count > Window)
        {
            _window.RemoveAt(0);
        }

        _degraded = Degraded(lossPercent, rttMs) ? _degraded + 1 : 0;
        Measure();

        if (Stalled())
        {
            return $"nothing has come back for {StallSeconds}s while the tunnel kept sending";
        }

        if (_degraded >= DegradedSeconds)
        {
            return $"the channel has been losing {lossPercent}% at {rttMs} ms for {_degraded}s";
        }

        if (RetransPercent >= RetransPercentLimit)
        {
            return $"the carrier has had to send {RetransPercent}% of its traffic again over the last {Window}s";
        }

        return string.Empty;
    }

    /// <summary>
    /// Drops the window a re-dial made stale, so the next verdict is taken on what the new carrier does.
    /// </summary>
    public void Clear()
    {
        _window.Clear();
        _degraded = 0;
        RetransPercent = -1;
    }

    // A stalled link keeps sending while all but a stray packet fails to come back.
    private bool Stalled()
    {
        if (_window.Count < StallSeconds)
        {
            return false;
        }

        var sent = 0;
        var returned = 0;
        for (var index = _window.Count - StallSeconds; index < _window.Count; index++)
        {
            if (_window[index].Sent)
            {
                sent++;
            }

            if (_window[index].Returned)
            {
                returned++;
            }
        }

        return returned <= StallReturns && sent >= StallSends;
    }

    // The share the window holds, once it holds enough of it.
    private void Measure()
    {
        if (_window.Count < Window)
        {
            RetransPercent = -1;
            return;
        }

        var sent = 0L;
        var again = 0L;
        foreach (var tick in _window)
        {
            sent += tick.BytesOut;
            again += tick.BytesRetrans;
        }

        RetransPercent = sent < RetransFloorBytes ? -1 : (int)(again * 100 / sent);
    }

    // What the echoes inside the tunnel say about the channel right now.
    private static bool Degraded(int lossPercent, int rttMs)
    {
        return (LinkHealth.LossKnown(lossPercent) && lossPercent >= LossPercentLimit) || rttMs >= RttMsLimit;
    }

    // One second of the link: whether the tunnel sent, whether anything came back, and what the carrier under it
    // put on the wire.
    private readonly record struct Tick(bool Sent, bool Returned, long BytesOut, long BytesRetrans);
}
