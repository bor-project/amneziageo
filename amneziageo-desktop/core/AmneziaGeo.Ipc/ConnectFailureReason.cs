namespace AmneziaGeo.Ipc;

/// <summary>
/// Structured cause of a failed connect, surfaced from the agent to the UI.
/// </summary>
public enum ConnectFailureReason
{
    /// <summary>
    /// Cause not classified; the generic notice applies.
    /// </summary>
    Unknown,

    /// <summary>
    /// The target profile has no configuration to dial.
    /// </summary>
    ProfileEmpty,

    /// <summary>
    /// The configuration is not stored.
    /// </summary>
    ConfigMissing,

    /// <summary>
    /// Creating or starting the per-tunnel service returned a non-zero code.
    /// </summary>
    ServiceStartFailed,

    /// <summary>
    /// The per-tunnel service never answered UAPI within the connect timeout.
    /// </summary>
    ServiceLaunchFailed,

    /// <summary>
    /// The wstunnel transport did not come up.
    /// </summary>
    UnderlayUnreachable,

    /// <summary>
    /// The WireGuard adapter or driver failed to bring up.
    /// </summary>
    AdapterStartFailed,

    /// <summary>
    /// The server sent no handshake; unreachable or key rejected.
    /// </summary>
    NoHandshake,

    /// <summary>
    /// The overall connect deadline elapsed.
    /// </summary>
    Timeout,
}
