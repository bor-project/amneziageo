namespace AmneziaGeo.Ipc;

/// <summary>
/// Connection status tokens shared across the IPC boundary.
/// </summary>
public static class ConnectionStatus
{
    /// <summary>
    /// Not in use by any active connection.
    /// </summary>
    public const string Idle = "idle";

    /// <summary>
    /// Bringing the connection up.
    /// </summary>
    public const string Connecting = "connecting";

    /// <summary>
    /// Connected and healthy.
    /// </summary>
    public const string Connected = "connected";

    /// <summary>
    /// Connected on a lower-priority member.
    /// </summary>
    public const string Degraded = "degraded";

    /// <summary>
    /// Switching to another member.
    /// </summary>
    public const string Failover = "failover";

    /// <summary>
    /// Disconnected.
    /// </summary>
    public const string Disconnected = "disconnected";

    /// <summary>
    /// Preempted by another connection.
    /// </summary>
    public const string Preempted = "preempted";

    /// <summary>
    /// Failed to connect.
    /// </summary>
    public const string Failed = "failed";
}
