namespace AmneziaGeo.Ipc;

/// <summary>
/// A configuration and its current connection status, as seen by the agent.
/// </summary>
public sealed record ConfigEntry(
    string Name,
    string Endpoint,
    bool GeoSplit,
    string Status,
    IReadOnlyList<string> Rules,
    bool WebSocket = false,
    string WebSocketHost = "",
    int WebSocketPort = 443,
    string Dns = "",
    string Exclusions = "",
    int Mtu = 0,
    bool UseIpv6 = false,
    // Seconds since the peer's last handshake on the running tunnel, in 30-second steps so a snapshot is not
    // pushed every second; -1 for every config that is not running.
    int HandshakeAgeSeconds = -1,
    // Peer throughput over the last agent poll and how often the session is re-established; zero on every
    // config that is not running.
    long RxBitsPerSecond = 0,
    long TxBitsPerSecond = 0,
    int HandshakesPerMinute = 0,
    // Share of its own probes the running tunnel lost over the last half minute; unknown on every config that is
    // not running, and on one whose probe has found nothing inside the tunnel to answer it.
    int LossPercent = LinkHealth.LossUnknown,
    // Round trip to the far end of the running tunnel, timed inside it; -1 on every config that is not running
    // and until the first echo comes back.
    int RttMs = -1,
    // Routing list this config routes through, already resolved against the default one; null sends every
    // destination through the tunnel.
    long? RoutingListId = null);

/// <summary>
/// Terms both sides read the handshake age by.
/// </summary>
public static class HandshakeAge
{
    /// <summary>
    /// Step the age is reported in.
    /// </summary>
    public const int StepSeconds = 30;

    /// <summary>
    /// Age beyond which the peer counts as gone. A keepalive refreshes the handshake only once the session
    /// reaches its own 180-second limit, so a live tunnel climbs to 180 every cycle; the reporting step and the
    /// interval add another minute on top of that.
    /// </summary>
    public const int SilentSeconds = 300;

    /// <summary>
    /// Rounds an age down to the reporting step; a negative age stays unknown.
    /// </summary>
    public static int Step(long seconds)
    {
        return seconds < 0 ? -1 : (int)(seconds / StepSeconds * StepSeconds);
    }
}
