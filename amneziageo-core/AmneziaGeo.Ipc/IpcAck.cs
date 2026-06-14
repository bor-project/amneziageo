namespace AmneziaGeo.Ipc;

/// <summary>
/// The agent's reply to a command.
/// </summary>
public sealed record IpcAck(
    bool Ok,
    string Message);
