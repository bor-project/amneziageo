namespace AmneziaGeo.Ipc;

/// <summary>
/// Shared constants for the agent status/control pipe protocol.
/// </summary>
public static class IpcContract
{
    /// <summary>
    /// The named-pipe name the agent listens on.
    /// </summary>
    public const string PipeName = "AmneziaGeo.Agent";

    /// <summary>
    /// Envelope type for a client greeting.
    /// </summary>
    public const string HelloType = "hello";

    /// <summary>
    /// Envelope type for a status snapshot pushed by the agent.
    /// </summary>
    public const string SnapshotType = "snapshot";
}
