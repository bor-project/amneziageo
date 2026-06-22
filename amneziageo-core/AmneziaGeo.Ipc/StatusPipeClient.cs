using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace AmneziaGeo.Ipc;

/// <summary>
/// Connects to the agent's status pipe, receives snapshots, sends commands, and reconnects when the link drops.
/// </summary>
public sealed class StatusPipeClient
{
    private static readonly TimeSpan _reconnectDelay = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan _commandTimeout = TimeSpan.FromSeconds(30);

    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly Lock _ackGate = new();
    private StreamWriter? _writer;
    private TaskCompletionSource<IpcAck>? _pendingAck;

    /// <summary>
    /// Raised when the client connects to the agent.
    /// </summary>
    public event Action? Connected;

    /// <summary>
    /// Raised when the link to the agent drops.
    /// </summary>
    public event Action? Disconnected;

    /// <summary>
    /// Raised when a status snapshot is received from the agent.
    /// </summary>
    public event Action<StatusSnapshot>? SnapshotReceived;

    /// <summary>
    /// Whether the client is currently connected to the agent.
    /// </summary>
    public bool IsConnected => _writer is not null;

    /// <summary>
    /// When true, the client sends an <see cref="IpcContract.OpAttachUi"/> marker on every (re)connect so
    /// the agent treats this connection as a presence-holding UI session — the tunnel stays up only while
    /// such a session is connected. The UI sets this; transient command clients (the CLI) leave it false.
    /// </summary>
    public bool AnnounceUi { get; init; }

    /// <summary>
    /// Connects and reads messages until cancellation, reconnecting after drops.
    /// </summary>
    public async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await ReadLoopAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception)
            {
                // fall through to reconnect
            }

            Disconnected?.Invoke();
            try
            {
                await Task.Delay(_reconnectDelay, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    /// <summary>
    /// Sends a command to the agent and awaits its acknowledgement.
    /// </summary>
    public async Task<IpcAck> SendCommandAsync(IpcCommand command, CancellationToken ct)
    {
        var writer = _writer;
        if (writer is null)
        {
            return new IpcAck(false, "not connected to agent");
        }

        var tcs = new TaskCompletionSource<IpcAck>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_ackGate)
        {
            _pendingAck = tcs;
        }

        var line = JsonSerializer.Serialize(new IpcEnvelope(IpcContract.CommandType, Command: command), IpcJson.Options);
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await writer.WriteLineAsync(line.AsMemory(), ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            return new IpcAck(false, $"send failed: {ex.Message}");
        }
        finally
        {
            _writeLock.Release();
        }

        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            timeout.CancelAfter(_commandTimeout);
            try
            {
                return await tcs.Task.WaitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return new IpcAck(false, "command timed out");
            }
        }
    }

    private async Task ReadLoopAsync(CancellationToken ct)
    {
        using (var pipe = new NamedPipeClientStream(".", IpcContract.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous))
        {
            await pipe.ConnectAsync(ct).ConfigureAwait(false);

            var encoding = new UTF8Encoding(false);
            using (var writer = new StreamWriter(pipe, encoding, 1024, leaveOpen: true) { AutoFlush = true })
            using (var reader = new StreamReader(pipe, encoding, false, 1024, leaveOpen: true))
            {
                _writer = writer;
                Connected?.Invoke();
                if (AnnounceUi)
                {
                    await AnnounceUiAsync(writer, ct).ConfigureAwait(false);
                }

                try
                {
                    while (!ct.IsCancellationRequested)
                    {
                        var line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
                        if (line is null)
                        {
                            break;
                        }

                        if (line.Length == 0)
                        {
                            continue;
                        }

                        Dispatch(line);
                    }
                }
                finally
                {
                    _writer = null;
                    FailPendingAck();
                }
            }
        }
    }

    /// <summary>
    /// Sends the UI-attach marker on the freshly opened connection. Fire-and-forget (no ack awaited): the
    /// agent's reply is an ordinary ack with no pending request and is harmlessly ignored. Uses the write
    /// lock so it cannot interleave with a concurrent user command.
    /// </summary>
    private async Task AnnounceUiAsync(StreamWriter writer, CancellationToken ct)
    {
        var line = JsonSerializer.Serialize(
            new IpcEnvelope(IpcContract.CommandType, Command: new IpcCommand(IpcContract.OpAttachUi, [])),
            IpcJson.Options);
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await writer.WriteLineAsync(line.AsMemory(), ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private void Dispatch(string line)
    {
        var envelope = JsonSerializer.Deserialize<IpcEnvelope>(line, IpcJson.Options);
        if (envelope is { Type: IpcContract.SnapshotType, Snapshot: not null })
        {
            SnapshotReceived?.Invoke(envelope.Snapshot);
        }
        else if (envelope is { Type: IpcContract.AckType, Ack: not null })
        {
            TaskCompletionSource<IpcAck>? pending;
            lock (_ackGate)
            {
                pending = _pendingAck;
                _pendingAck = null;
            }

            pending?.TrySetResult(envelope.Ack);
        }
    }

    private void FailPendingAck()
    {
        TaskCompletionSource<IpcAck>? pending;
        lock (_ackGate)
        {
            pending = _pendingAck;
            _pendingAck = null;
        }

        pending?.TrySetResult(new IpcAck(false, "disconnected"));
    }
}
