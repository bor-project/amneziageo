using AmneziaGeo.Ipc;
using Microsoft.Extensions.Logging;

namespace AmneziaGeo.Windows.App;

/// <summary>
/// Watches what a websocket-carried tunnel actually carries. A live carrier process says nothing about the stream
/// inside it: the WG session survives on keepalives, small packets keep crossing, and a transfer stalls with
/// nothing in the journal to show for it. This samples the link and re-dials the carrier when it stops carrying,
/// which is what a manual reconnect does - without tearing down the tunnel, its routes and its DNS.
/// </summary>
internal sealed class CarrierWatchdog(WsTunnelTransport carrier, UapiClient uapi, LinkLossProbe probe, string tunnelName, ILogger logger)
{
    // Echoes without one answer after which the link is declared unmeasurable. A probe whose target is not
    // carried by the tunnel never answers, and the reading it then never produces passes for perfect health.
    private const int UnmeasurableAttempts = 60;

    // Shortest gap between re-dials, so a genuinely bad underlay is not dialled in a loop.
    private static readonly TimeSpan _redialCooldown = TimeSpan.FromMinutes(2);

    private static readonly TimeSpan _traceInterval = TimeSpan.FromSeconds(60);

    private readonly CarrierHealth _health = new();
    private long _lastRx = -1;
    private long _lastTx = -1;
    private long _lastBytesOut = -1;
    private long _lastBytesRetrans = -1;
    private bool _degradedLogged;
    private bool _unmeasurableLogged;
    private DateTimeOffset _redialledAt = DateTimeOffset.MinValue;
    private DateTimeOffset _tracedAt = DateTimeOffset.MinValue;

    /// <summary>
    /// Samples the link once a second for as long as the session runs.
    /// </summary>
    public async Task RunAsync(CancellationToken ct)
    {
        logger.LogInformation("{Name}: watching the websocket carrier - it is re-dialled if nothing comes back while the tunnel keeps sending, if the channel degrades, or if the carrier has to send its traffic again",
            tunnelName);

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(1000, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            if (uapi.TryGetPeerStatus(tunnelName) is { } status)
            {
                Sample(status);
            }
        }
    }

    private void Sample(UapiClient.PeerStatus status)
    {
        var rxMoved = _lastRx >= 0 && status.RxBytes > _lastRx;
        var txMoved = _lastTx >= 0 && status.TxBytes > _lastTx;
        _lastRx = status.RxBytes;
        _lastTx = status.TxBytes;

        var wire = carrier.Retransmission();
        var bytesOut = Moved(_lastBytesOut, wire.BytesOut);
        var bytesRetrans = Moved(_lastBytesRetrans, wire.BytesRetrans);
        _lastBytesOut = wire.BytesOut;
        _lastBytesRetrans = wire.BytesRetrans;

        var reason = _health.Verdict(txMoved, rxMoved, bytesOut, bytesRetrans, probe.Percent, probe.RttMs);
        Unmeasurable();
        Trace(status);
        if (reason.Length == 0)
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        if (now - _redialledAt < _redialCooldown)
        {
            return;
        }

        _redialledAt = now;
        _health.Clear();
        carrier.Redial($"{tunnelName}: {reason}");
        probe.Reset();
    }

    // A counter that went backwards belongs to a connection the carrier has since replaced.
    private static long Moved(long last, long now)
    {
        return last >= 0 && now >= last ? now - last : 0;
    }

    // A probe nothing answers is worth saying once: the loss and the round trip stay unknown for the session, so
    // the channel is judged on silence and on what the carrier repeats, and on nothing else.
    private void Unmeasurable()
    {
        if (_unmeasurableLogged || probe.Answering || probe.Attempts < UnmeasurableAttempts)
        {
            return;
        }

        _unmeasurableLogged = true;
        logger.LogWarning("{Name}: nothing has answered {Attempts} echoes inside the tunnel, so the channel's loss and round trip stay unknown for this session; check that the probe's target is carried by the tunnel",
            tunnelName, probe.Attempts);
    }

    // Writes the channel to the journal: on every crossing into and out of a degraded reading, and once a minute
    // regardless, so a stalled transfer leaves a record of what the carrier held while it stalled.
    private void Trace(UapiClient.PeerStatus status)
    {
        if (_health.Degrading != _degradedLogged)
        {
            _degradedLogged = _health.Degrading;
            LogCrossing(_health.Degrading);
        }

        var now = DateTimeOffset.UtcNow;
        if (now - _tracedAt < _traceInterval)
        {
            return;
        }

        _tracedAt = now;
        var sessions = carrier.Sessions();
        logger.LogInformation("{Name}: the carrier holds {Established} of {Total} connection(s), has been re-dialled {Redials} time(s) and repeats {Retrans}% of what it sends; the tunnel received {Rx} B, sent {Tx} B, loses {Loss}% at {Rtt} ms",
            tunnelName, sessions.Established, sessions.Total, carrier.Redials, _health.RetransPercent, status.RxBytes, status.TxBytes, probe.Percent, probe.RttMs);
    }

    private void LogCrossing(bool degraded)
    {
        var sessions = carrier.Sessions();
        if (degraded)
        {
            logger.LogWarning("{Name}: the channel is degrading - losing {Loss}% at {Rtt} ms while the carrier holds {Established} of {Total} connection(s)",
                tunnelName, probe.Percent, probe.RttMs, sessions.Established, sessions.Total);
            return;
        }

        logger.LogInformation("{Name}: the channel carries again - losing {Loss}% at {Rtt} ms", tunnelName, probe.Percent, probe.RttMs);
    }
}
