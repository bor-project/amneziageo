using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace AmneziaGeo.Ipc;

/// <summary>
/// Connects to the agent's status pipe and raises events as snapshots arrive, reconnecting when the link drops.
/// </summary>
public sealed class StatusPipeClient
{
    private static readonly TimeSpan _reconnectDelay = TimeSpan.FromSeconds(2);

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
    /// Connects and reads snapshots until cancellation, reconnecting after drops.
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

    private async Task ReadLoopAsync(CancellationToken ct)
    {
        using (var pipe = new NamedPipeClientStream(".", IpcContract.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous))
        {
            await pipe.ConnectAsync(ct).ConfigureAwait(false);
            Connected?.Invoke();

            using (var reader = new StreamReader(pipe, new UTF8Encoding(false), false, 1024, leaveOpen: true))
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

                    var envelope = JsonSerializer.Deserialize<IpcEnvelope>(line, IpcJson.Options);
                    if (envelope is { Type: IpcContract.SnapshotType, Snapshot: not null })
                    {
                        SnapshotReceived?.Invoke(envelope.Snapshot);
                    }
                }
            }
        }
    }
}
