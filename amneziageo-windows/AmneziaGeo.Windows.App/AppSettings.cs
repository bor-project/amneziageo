namespace AmneziaGeo.Windows.App;

/// <summary>
/// Runtime tuning settings persisted in the state database.
/// </summary>
internal sealed record AppSettings
{
    /// <summary>
    /// How often tunneled domains are re-resolved, in seconds.
    /// </summary>
    public int RefreshSeconds { get; init; } = 60;

    /// <summary>
    /// How long a balancer waits for a member handshake before declaring it unreachable, in seconds.
    /// </summary>
    public int ConnectTimeoutSeconds { get; init; } = 20;

    /// <summary>
    /// Handshake age beyond which a balancer treats an active member as dead, in seconds.
    /// </summary>
    public int DeadThresholdSeconds { get; init; } = 180;

    /// <summary>
    /// Consecutive successful out-of-band probes of a higher-priority member required before failing back to it.
    /// </summary>
    public int FailbackProbes { get; init; } = 3;

    /// <summary>
    /// Timeout for an out-of-band endpoint reachability probe, in seconds.
    /// </summary>
    public int ProbeTimeoutSeconds { get; init; } = 2;
}
