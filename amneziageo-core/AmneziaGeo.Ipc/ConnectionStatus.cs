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
    /// Tearing the connection down.
    /// </summary>
    public const string Disconnecting = "disconnecting";

    /// <summary>
    /// Disconnected.
    /// </summary>
    public const string Disconnected = "disconnected";

    /// <summary>
    /// Preempted by another connection that took over the single tunnel slot.
    /// </summary>
    public const string Preempted = "preempted";

    /// <summary>
    /// Failed to connect.
    /// </summary>
    public const string Failed = "failed";
}
