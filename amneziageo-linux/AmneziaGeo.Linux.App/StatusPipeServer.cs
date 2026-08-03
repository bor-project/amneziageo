using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using AmneziaGeo.Ipc;

namespace AmneziaGeo.Linux.App;

/// <summary>
/// Serves the agent status/control pipe: pushes snapshots and executes the commands clients send.
/// </summary>
internal sealed class StatusPipeServer : IDisposable
{
    // .NET maps a named pipe to this unix socket on Linux.
    private static readonly string _socketPath = $"/tmp/CoreFxPipe_{IpcContract.PipeName}";

    private readonly LinuxAgent _agent;
    private readonly AgentLog _log;
    private readonly List<Client> _clients = [];
    private readonly Lock _gate = new();
    private bool _disposed;

    /// <summary>
    /// ctor
    /// </summary>
    public StatusPipeServer(LinuxAgent agent, AgentLog log)
    {
        _agent = agent;
        _log = log;
    }

    /// <summary>
    /// Accepts clients until cancellation.
    /// </summary>
    public async Task RunAsync(CancellationToken ct)
    {
        _agent.StateChanged += BroadcastAsync;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var stream = new NamedPipeServerStream(
                    IpcContract.PipeName,
                    PipeDirection.InOut,
                    NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous);

                OpenToDesktopSession();

                try
                {
                    await stream.WaitForConnectionAsync(ct).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    await stream.DisposeAsync().ConfigureAwait(false);
                    if (ex is OperationCanceledException)
                    {
                        break;
                    }

                    _log.Error("ipc", "accept failed", ex);
                    await Task.Delay(500, ct).ConfigureAwait(false);
                    continue;
                }

                _ = ServeAsync(stream, ct);
            }
        }
        finally
        {
            _agent.StateChanged -= BroadcastAsync;
        }
    }

    // A root-run agent has to let the desktop session reach the socket it just created.
    private void OpenToDesktopSession()
    {
        if (Environment.GetEnvironmentVariable("USER") is "root" || Environment.UserName == "root")
        {
            try
            {
                if (File.Exists(_socketPath))
                {
                    File.SetUnixFileMode(_socketPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.OtherRead | UnixFileMode.OtherWrite);
                }
            }
            catch (Exception ex)
            {
                _log.Warn("ipc", $"could not relax {_socketPath} permissions: {ex.Message}");
            }
        }
    }

    private async Task ServeAsync(NamedPipeServerStream stream, CancellationToken ct)
    {
        var client = new Client(stream);
        lock (_gate)
        {
            _clients.Add(client);
        }

        _log.Info("ipc", "client connected");
        try
        {
            await client.SendAsync(Serialize(await _agent.BuildSnapshotAsync(ct).ConfigureAwait(false)), ct).ConfigureAwait(false);
            using var reader = new StreamReader(stream, new UTF8Encoding(false), false, 1024, leaveOpen: true);
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

                await HandleAsync(client, line, ct).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException)
        {
        }
        catch (Exception ex)
        {
            _log.Error("ipc", "client loop broke", ex);
        }
        finally
        {
            lock (_gate)
            {
                _clients.Remove(client);
            }

            client.Dispose();
            _log.Info("ipc", "client disconnected");
        }
    }

    private async Task HandleAsync(Client client, string line, CancellationToken ct)
    {
        var envelope = JsonSerializer.Deserialize<IpcEnvelope>(line, IpcJson.Options);
        if (envelope is not { Type: IpcContract.CommandType, Command: not null })
        {
            return;
        }

        var ack = await _agent.DispatchAsync(envelope.Command, ct).ConfigureAwait(false);
        var reply = JsonSerializer.Serialize(new IpcEnvelope(IpcContract.AckType, Ack: ack), IpcJson.Options);
        await client.SendAsync(reply, ct).ConfigureAwait(false);
    }

    // Pushes a fresh snapshot to every connected client.
    private async Task BroadcastAsync(CancellationToken ct)
    {
        Client[] targets;
        lock (_gate)
        {
            if (_clients.Count == 0)
            {
                return;
            }

            targets = [.. _clients];
        }

        var line = Serialize(await _agent.BuildSnapshotAsync(ct).ConfigureAwait(false));
        foreach (var client in targets)
        {
            try
            {
                await client.SendAsync(line, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException)
            {
            }
        }
    }

    private static string Serialize(StatusSnapshot snapshot) =>
        JsonSerializer.Serialize(new IpcEnvelope(IpcContract.SnapshotType, snapshot), IpcJson.Options);

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        lock (_gate)
        {
            foreach (var client in _clients)
            {
                client.Dispose();
            }

            _clients.Clear();
        }
    }

    /// <summary>
    /// One connected client with serialized line writes.
    /// </summary>
    private sealed class Client : IDisposable
    {
        private readonly NamedPipeServerStream _stream;
        private readonly StreamWriter _writer;
        private readonly SemaphoreSlim _writeLock = new(1, 1);

        /// <summary>
        /// ctor
        /// </summary>
        public Client(NamedPipeServerStream stream)
        {
            _stream = stream;
            _writer = new StreamWriter(stream, new UTF8Encoding(false), 1024, leaveOpen: true) { AutoFlush = true };
        }

        /// <summary>
        /// Writes one line under the write lock.
        /// </summary>
        public async Task SendAsync(string line, CancellationToken ct)
        {
            await _writeLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                await _writer.WriteLineAsync(line.AsMemory(), ct).ConfigureAwait(false);
            }
            finally
            {
                _writeLock.Release();
            }
        }

        /// <inheritdoc/>
        public void Dispose()
        {
            _writer.Dispose();
            _writeLock.Dispose();
            _stream.Dispose();
        }
    }
}
