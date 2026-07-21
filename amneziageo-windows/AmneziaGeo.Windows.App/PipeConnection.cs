using System.IO.Pipes;
using System.Text;

namespace AmneziaGeo.Windows.App;

/// <summary>
/// A single connected status-pipe client, with serialized line writes.
/// </summary>
internal sealed class PipeConnection : IDisposable
{
    private readonly StreamWriter _writer;
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    /// <summary>
    /// ctor
    /// </summary>
    public PipeConnection(NamedPipeServerStream stream)
    {
        Stream = stream;
        _writer = new StreamWriter(stream, new UTF8Encoding(false), 1024, leaveOpen: true) { AutoFlush = true };
    }

    /// <summary>
    /// The underlying pipe stream.
    /// </summary>
    public NamedPipeServerStream Stream { get; }

    /// <summary>
    /// Writes a line to the client under the write lock.
    /// </summary>
    public async Task SendAsync(string line, CancellationToken ct)
    {
        await _writeLock.WaitAsync(ct);
        try
        {
            await _writer.WriteLineAsync(line.AsMemory(), ct);
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
        Stream.Dispose();
    }
}
