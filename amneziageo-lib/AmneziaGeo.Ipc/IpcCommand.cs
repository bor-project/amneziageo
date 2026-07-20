namespace AmneziaGeo.Ipc;

/// <summary>
/// A command sent by the UI to the agent to perform a privileged mutation.
/// </summary>
public sealed record IpcCommand(
    string Op,
    IReadOnlyList<string> Args);
