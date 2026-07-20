using AmneziaGeo.Ipc;

namespace AmneziaGeo.Ui.Services;

/// <summary>
/// Inert connection used by design-time data.
/// </summary>
internal sealed class NullAgentConnection : IAgentConnection
{
    public event Action? Connected
    {
        add { }
        remove { }
    }

    public event Action? Disconnected
    {
        add { }
        remove { }
    }

    public event Action<StatusSnapshot>? SnapshotReceived
    {
        add { }
        remove { }
    }

    public void Start()
    {
    }

    public Task<IpcAck> SendCommandAsync(IpcCommand command) => NotAvailable();

    public Task<IpcAck> SendCommandRawAsync(IpcCommand command) => NotAvailable();

    public void Dispose()
    {
    }

    private static Task<IpcAck> NotAvailable() => Task.FromResult(new IpcAck(false, "Design-time connection is unavailable."));
}
