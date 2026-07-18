using AmneziaGeo.Ipc;
using AmneziaGeo.Localization;

namespace AmneziaGeo.Windows.Ui.Services;

/// <summary>
/// Holds the agent connection and surfaces status snapshots.
/// </summary>
internal sealed class AgentConnection : IDisposable
{
    // Real UI presence; the agent keeps the tunnel up only while a UI is attached.
    private readonly StatusPipeClient _client = new() { AnnounceUi = true };
    private readonly CancellationTokenSource _cts = new();
    private Task? _loop;
    private bool _disposed;

    /// <summary>
    /// Raised when the connection to the agent opens.
    /// </summary>
    public event Action? Connected;

    /// <summary>
    /// Raised when the connection to the agent drops.
    /// </summary>
    public event Action? Disconnected;

    /// <summary>
    /// Raised when a status snapshot arrives.
    /// </summary>
    public event Action<StatusSnapshot>? SnapshotReceived;

    /// <summary>
    /// Starts the background connection loop.
    /// </summary>
    public void Start()
    {
        _client.Connected += () => Connected?.Invoke();
        _client.Disconnected += () => Disconnected?.Invoke();
        _client.SnapshotReceived += snapshot => SnapshotReceived?.Invoke(snapshot);
        _loop = _client.RunAsync(_cts.Token);
    }

    /// <summary>
    /// Sends a command and localizes the reply.
    /// </summary>
    public async Task<IpcAck> SendCommandAsync(IpcCommand command)
    {
        var ack = await _client.SendCommandAsync(command, _cts.Token);
        return IpcMessage.TryParse(ack.Message, out var key, out var args)
            ? ack with { Message = Loc.Instance.Get(key, args) }
            : ack;
    }

    /// <summary>
    /// Sends a command and returns the raw reply, keeping its localization key so the caller can branch on it.
    /// </summary>
    public Task<IpcAck> SendCommandRawAsync(IpcCommand command) => _client.SendCommandAsync(command, _cts.Token);

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _cts.Cancel();
        _cts.Dispose();
    }
}
