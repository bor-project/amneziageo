using AmneziaGeo.Ipc;

namespace AmneziaGeo.Ui.Services;

/// <summary>
/// Platform connection used by the shared UI to observe state and send mutations.
/// </summary>
internal interface IAgentConnection : IDisposable
{
    event Action? Connected;

    event Action? Disconnected;

    event Action<StatusSnapshot>? SnapshotReceived;

    void Start();

    Task<IpcAck> SendCommandAsync(IpcCommand command);

    Task<IpcAck> SendCommandRawAsync(IpcCommand command);
}
