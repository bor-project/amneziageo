using AmneziaGeo.Ipc;

namespace AmneziaGeo.Cli;

/// <summary>
/// Link to an agent that listens on the status pipe.
/// </summary>
public sealed class PipeAgentLink : IAgentLink, IDisposable
{
    private readonly StatusPipeClient _client;
    private readonly CancellationTokenSource _cts;
    private readonly TaskCompletionSource<StatusSnapshot> _first = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private StatusSnapshot? _snapshot;
    private bool _disposed;

    /// <summary>
    /// ctor
    /// </summary>
    public PipeAgentLink(TimeSpan commandTimeout, CancellationToken ct)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _client = new StatusPipeClient { CommandTimeout = commandTimeout };
        _client.SnapshotReceived += Accept;
    }

    /// <inheritdoc/>
    public event Action<StatusSnapshot>? SnapshotReceived;

    /// <inheritdoc/>
    public StatusSnapshot Snapshot => _snapshot ?? throw new InvalidOperationException("no snapshot received");

    /// <summary>
    /// Connects and waits for the first snapshot.
    /// </summary>
    public async Task<bool> ConnectAsync(TimeSpan timeout)
    {
        _ = _client.RunAsync(_cts.Token);
        var arrived = await Task.WhenAny(_first.Task, Task.Delay(timeout, _cts.Token)).ConfigureAwait(false);
        return ReferenceEquals(arrived, _first.Task);
    }

    /// <inheritdoc/>
    public Task<IpcAck> SendAsync(string op, params string[] args) =>
        _client.SendCommandAsync(new IpcCommand(op, args), _cts.Token);

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

    private void Accept(StatusSnapshot snapshot)
    {
        _snapshot = snapshot;
        _first.TrySetResult(snapshot);
        SnapshotReceived?.Invoke(snapshot);
    }
}
