using AmneziaGeo.Ipc;

namespace AmneziaGeo.Windows.Ui.Services;

/// <summary>
/// Maintains the background connection to the agent and surfaces status snapshots.
/// </summary>
internal sealed class AgentConnection : IDisposable
{
    private readonly StatusPipeClient _client = new();
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
    /// Sends a command to the agent and returns its acknowledgement.
    /// </summary>
    public Task<IpcAck> SendCommandAsync(IpcCommand command)
    {
        return _client.SendCommandAsync(command, _cts.Token);
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        // Idempotent: shutdown disposes this both from the explicit tray "Выход" path and from the
        // lifetime's ShutdownRequested handler (OS session end). A second Cancel on a disposed source
        // would throw, so guard it.
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _cts.Cancel();
        _cts.Dispose();
    }
}
