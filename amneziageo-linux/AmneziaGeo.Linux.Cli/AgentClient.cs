using AmneziaGeo.Ipc;
using AmneziaGeo.Localization;

namespace AmneziaGeo.Linux.Cli;

/// <summary>
/// Link to the agent: connects, holds the latest snapshot, sends commands and localizes the replies.
/// </summary>
internal sealed class AgentClient : IDisposable
{
    /// <summary>
    /// Unix socket the named pipe maps to on Linux.
    /// </summary>
    public const string SocketPath = "/tmp/CoreFxPipe_" + IpcContract.PipeName;

    private readonly StatusPipeClient _client;
    private readonly CancellationTokenSource _cts = new();
    private readonly TaskCompletionSource<StatusSnapshot> _first = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private StatusSnapshot? _snapshot;
    private Task? _loop;
    private bool _disposed;

    /// <summary>
    /// ctor
    /// </summary>
    public AgentClient(TimeSpan commandTimeout)
    {
        _client = new StatusPipeClient { CommandTimeout = commandTimeout };
        _client.SnapshotReceived += Accept;
    }

    /// <summary>
    /// Raised on every snapshot the agent pushes.
    /// </summary>
    public event Action<StatusSnapshot>? SnapshotReceived;

    /// <summary>
    /// Latest snapshot received from the agent.
    /// </summary>
    public StatusSnapshot Snapshot => _snapshot ?? throw new InvalidOperationException("no snapshot received");

    /// <summary>
    /// Cancellation tied to the client's lifetime.
    /// </summary>
    public CancellationToken Token => _cts.Token;

    /// <summary>
    /// Connects and waits for the first snapshot.
    /// </summary>
    public async Task<bool> ConnectAsync(TimeSpan timeout)
    {
        _loop = _client.RunAsync(_cts.Token);
        var arrived = await Task.WhenAny(_first.Task, Task.Delay(timeout, _cts.Token)).ConfigureAwait(false);
        return ReferenceEquals(arrived, _first.Task);
    }

    /// <summary>
    /// Sends a command and returns the reply as it came: callers that show it localize it, callers that
    /// consume a JSON or text payload read it verbatim.
    /// </summary>
    public Task<IpcAck> SendAsync(string op, params string[] args) =>
        _client.SendCommandAsync(new IpcCommand(op, args), _cts.Token);

    /// <summary>
    /// Resolves an ack message that carries a resource key.
    /// </summary>
    public static string Localize(string message) =>
        IpcMessage.TryParse(message, out var key, out var args) ? Loc.Instance.Get(key, args) : message;

    /// <summary>
    /// Why the agent could not be reached.
    /// </summary>
    public static string UnreachableHint()
    {
        if (!File.Exists(SocketPath))
        {
            return $"the agent is not running: {SocketPath} does not exist";
        }

        return $"could not talk to the agent on {SocketPath}; check its permissions and that the agent is alive";
    }

    private void Accept(StatusSnapshot snapshot)
    {
        _snapshot = snapshot;
        _first.TrySetResult(snapshot);
        SnapshotReceived?.Invoke(snapshot);
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _client.SnapshotReceived -= Accept;
        _cts.Cancel();
        _cts.Dispose();
    }
}
