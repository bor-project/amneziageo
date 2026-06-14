namespace AmneziaGeo.Ipc;

/// <summary>
/// A single newline-delimited JSON message exchanged over the pipe.
/// </summary>
public sealed record IpcEnvelope(
    string Type,
    StatusSnapshot? Snapshot);
