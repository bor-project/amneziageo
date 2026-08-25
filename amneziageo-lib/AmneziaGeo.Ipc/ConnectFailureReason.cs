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
    /// No configuration is selected to dial.
    /// </summary>
    NoTargetSelected,

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
    /// The transport carrier refused the connection for good: an expired or untrusted TLS certificate.
    /// </summary>
    TransportRejected,

    /// <summary>
    /// The overall connect deadline elapsed.
    /// </summary>
    Timeout,

    /// <summary>
    /// The configuration could not be parsed.
    /// </summary>
    ConfigInvalid,

    /// <summary>
    /// The platform refused the VPN permission.
    /// </summary>
    PermissionDenied,

    /// <summary>
    /// The routing list holds more routes than the platform takes in one transaction.
    /// </summary>
    TooManyRoutes,

    /// <summary>
    /// The platform refused to create the tunnel interface.
    /// </summary>
    TunnelSetupFailed,

    /// <summary>
    /// The tunnel engine refused to start.
    /// </summary>
    EngineStartFailed,

    /// <summary>
    /// The native tunnel engine is missing for this device.
    /// </summary>
    EngineUnavailable,

    /// <summary>
    /// The firewall drops UDP on the loopback, where the carrier hands the tunnel to the engine.
    /// </summary>
    LoopbackBlocked,
}
