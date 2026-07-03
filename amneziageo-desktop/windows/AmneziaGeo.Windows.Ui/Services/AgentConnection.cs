using AmneziaGeo.Ipc;
using AmneziaGeo.Localization;

namespace AmneziaGeo.Windows.Ui.Services;

/// <summary>
/// Maintains the background connection to the agent and surfaces status snapshots.
/// </summary>
internal sealed class AgentConnection : IDisposable
{
    // AnnounceUi: this is the real UI, so mark the pipe connection as a presence session - the agent keeps
    // the tunnel up only while a UI is connected and disconnects it shortly after the window closes/crashes.
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
    /// Sends a command to the agent and returns its acknowledgement, localizing the reply here - the single
    /// choke point every UI command flows through (#106). The agent, which does not localize, may reply with a
    /// marker-encoded resource key; it becomes its translation in the current UI language. A raw reply (a
    /// config payload, a file path, an exception message) carries no marker and passes through unchanged.
    /// </summary>
    public async Task<IpcAck> SendCommandAsync(IpcCommand command)
    {
        var ack = await _client.SendCommandAsync(command, _cts.Token);
        return IpcMessage.TryParse(ack.Message, out var key, out var args)
            ? ack with { Message = Loc.Instance.Get(key, args) }
            : ack;
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
