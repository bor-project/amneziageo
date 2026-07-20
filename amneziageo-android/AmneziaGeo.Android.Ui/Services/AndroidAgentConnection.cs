using AmneziaGeo.Ipc;
using AmneziaGeo.Localization;
using AmneziaGeo.Ui.Services;

namespace AmneziaGeo.Android.Ui.Services;

/// <summary>
/// Bridges the shared UI to Android. Command handling will move to VpnService as the engine is implemented.
/// </summary>
internal sealed class AndroidAgentConnection : IAgentConnection
{
    private bool _started;
    private bool _disposed;

    public event Action? Connected;

    public event Action? Disconnected;

    public event Action<StatusSnapshot>? SnapshotReceived;

    public void Start()
    {
        if (_started || _disposed)
        {
            return;
        }

        _started = true;
        Connected?.Invoke();
        SnapshotReceived?.Invoke(new StatusSnapshot(
            AgentVersion: "Android preview",
            BoundTarget: null,
            Configs: [],
            Profiles: [],
            RoutingLists: [],
            Active: false,
            BoundStatus: ConnectionStatus.Disconnected,
            Sources: [],
            EngineVersion: string.Empty));
    }

    public Task<IpcAck> SendCommandAsync(IpcCommand command) => NotReady();

    public Task<IpcAck> SendCommandRawAsync(IpcCommand command) => NotReady();

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_started)
        {
            Disconnected?.Invoke();
        }
    }

    private static Task<IpcAck> NotReady()
        => Task.FromResult(new IpcAck(false, Loc.Instance.Get("Android_EngineNotReady")));
}
