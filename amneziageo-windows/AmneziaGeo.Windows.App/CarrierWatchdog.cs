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
    // Seconds of outgoing traffic with nothing coming back before the carrier counts as dead.
    private const int StallSeconds = 12;

    // Loss and round trip inside the tunnel that mark a channel no longer able to carry a transfer.
    private const int LossPercentLimit = 40;
    private const int RttMsLimit = 2000;

    // Seconds a degraded reading holds before it is acted on.
    private const int DegradedSeconds = 20;

    // Shortest gap between re-dials, so a genuinely bad underlay is not dialled in a loop.
    private static readonly TimeSpan _redialCooldown = TimeSpan.FromMinutes(2);

    private static readonly TimeSpan _traceInterval = TimeSpan.FromSeconds(60);

    private long _lastRx = -1;
    private long _lastTx = -1;
    private int _stalled;
    private int _degraded;
    private bool _degradedLogged;
    private DateTimeOffset _redialledAt = DateTimeOffset.MinValue;
    private DateTimeOffset _tracedAt = DateTimeOffset.MinValue;

    /// <summary>
    /// Samples the link once a second for as long as the session runs.
    /// </summary>
    public async Task RunAsync(CancellationToken ct)
    {
        logger.LogInformation("{Name}: watching the websocket carrier — it is re-dialled if nothing comes back for {Stall}s, or if the channel loses {Loss}% or answers slower than {Rtt} ms for {Degraded}s",
            tunnelName, StallSeconds, LossPercentLimit, RttMsLimit, DegradedSeconds);

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

        _stalled = txMoved && !rxMoved ? _stalled + 1 : 0;
        _degraded = Degraded() ? _degraded + 1 : 0;
        Trace(status);

        var reason = Verdict();
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
        _stalled = 0;
        _degraded = 0;
        carrier.Redial($"{tunnelName}: {reason}");
        probe.Reset();
    }

    // What the echoes inside the tunnel say about the channel right now.
    private bool Degraded()
    {
        var loss = probe.Percent;
        return (LinkHealth.LossKnown(loss) && loss >= LossPercentLimit) || probe.RttMs >= RttMsLimit;
    }

    // The reason to re-dial, or nothing while the channel still carries.
    private string Verdict()
    {
        if (_stalled >= StallSeconds)
        {
            return $"nothing has come back for {_stalled}s while the tunnel kept sending";
        }

        if (_degraded >= DegradedSeconds)
        {
            return $"the channel has been losing {probe.Percent}% at {probe.RttMs} ms for {_degraded}s";
        }

        return string.Empty;
    }

    // Writes the channel to the journal: on every crossing into and out of a degraded reading, and once a minute
    // regardless, so a stalled transfer leaves a record of what the carrier held while it stalled.
    private void Trace(UapiClient.PeerStatus status)
    {
        var degraded = _degraded > 0;
        if (degraded != _degradedLogged)
        {
            _degradedLogged = degraded;
            LogCrossing(degraded);
        }

        var now = DateTimeOffset.UtcNow;
        if (now - _tracedAt < _traceInterval)
        {
            return;
        }

        _tracedAt = now;
        var sessions = carrier.Sessions();
        logger.LogInformation("{Name}: the carrier holds {Established} of {Total} connection(s) and has been re-dialled {Redials} time(s); the tunnel received {Rx} B, sent {Tx} B, loses {Loss}% at {Rtt} ms",
            tunnelName, sessions.Established, sessions.Total, carrier.Redials, status.RxBytes, status.TxBytes, probe.Percent, probe.RttMs);
    }

    private void LogCrossing(bool degraded)
    {
        var sessions = carrier.Sessions();
        if (degraded)
        {
            logger.LogWarning("{Name}: the channel is degrading — losing {Loss}% at {Rtt} ms while the carrier holds {Established} of {Total} connection(s)",
                tunnelName, probe.Percent, probe.RttMs, sessions.Established, sessions.Total);
            return;
        }

        logger.LogInformation("{Name}: the channel carries again — losing {Loss}% at {Rtt} ms", tunnelName, probe.Percent, probe.RttMs);
    }
}
