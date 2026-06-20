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

    /// <summary>
    /// When set, a WFP kill-switch is armed while a tunnel is up: all traffic is blocked except the
    /// tunnel, loopback, this process, DHCP, Hyper-V, and (per <see cref="AllowLan"/>) the LAN.
    /// Defaults off so enabling it is an explicit choice.
    /// </summary>
    public bool KillSwitchEnabled { get; init; }

    /// <summary>
    /// When the kill-switch is armed, also permit private LAN ranges (RFC1918, link-local, multicast)
    /// so host/Hyper-V SSH and local devices keep working. Defaults on.
    /// </summary>
    public bool AllowLan { get; init; } = true;

    /// <summary>
    /// URL of the update metadata file (JSON with version/description/setup). Empty disables update
    /// checks. The installer is expected to sit next to this file (resolved relative to it).
    /// </summary>
    public string UpdateUrl { get; init; } = string.Empty;
}
