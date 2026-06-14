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

    /// <inheritdoc/>
    public void Dispose()
    {
        _cts.Cancel();
        _cts.Dispose();
    }
}
