using AmneziaGeo.Ipc;
using AmneziaGeo.Localization;

namespace AmneziaGeo.Cli;

/// <summary>
/// Link to the agent, however the platform reaches it.
/// </summary>
public interface IAgentLink
{
    /// <summary>
    /// Raised on every snapshot the agent pushes.
    /// </summary>
    event Action<StatusSnapshot>? SnapshotReceived;

    /// <summary>
    /// Latest snapshot received from the agent.
    /// </summary>
    StatusSnapshot Snapshot { get; }

    /// <summary>
    /// Sends a command and returns the reply as it came.
    /// </summary>
    Task<IpcAck> SendAsync(string op, params string[] args);
}

/// <summary>
/// Ack text that carries a resource key.
/// </summary>
public static class AckText
{
    /// <summary>
    /// Resolves an ack message that carries a resource key.
    /// </summary>
    public static string Localize(string message) =>
        IpcMessage.TryParse(message, out var key, out var args) ? Loc.Instance.Get(key, args) : message;
}
