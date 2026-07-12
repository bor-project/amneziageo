using Serilog;
using Serilog.Core;
using Serilog.Events;

namespace AmneziaGeo.Windows.App;

/// <summary>
/// Serilog file sink whose file can be dropped and reopened at runtime, so the log can be cleared without
/// restarting the agent.
/// </summary>
internal sealed class ResettableFileSink : ILogEventSink, IDisposable
{
    private readonly string _path;
    private readonly string _template;
    private readonly object _gate = new();
    private Logger _inner;

    /// <summary>
    /// ctor
    /// </summary>
    public ResettableFileSink(string path, string outputTemplate)
    {
        _path = path;
        _template = outputTemplate;
        _inner = Build();
    }

    private Logger Build()
    {
        return new LoggerConfiguration()
            .MinimumLevel.Verbose()
            .WriteTo.File(_path, outputTemplate: _template, rollingInterval: RollingInterval.Infinite, shared: true)
            .CreateLogger();
    }

    /// <inheritdoc/>
    public void Emit(LogEvent logEvent)
    {
        lock (_gate)
        {
            _inner.Write(logEvent);
        }
    }

    /// <summary>
    /// Closes the file, empties it, and reopens fresh.
    /// </summary>
    public void Reset()
    {
        lock (_gate)
        {
            _inner.Dispose();
            EmptyFile();
            _inner = Build();
        }
    }

    // Deletes the file; when another process still holds it (a per-tunnel service), truncates instead.
    private void EmptyFile()
    {
        try
        {
            File.Delete(_path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Truncate();
        }
    }

    private void Truncate()
    {
        try
        {
            using var stream = new FileStream(_path, FileMode.OpenOrCreate, FileAccess.Write, FileShare.ReadWrite);
            stream.SetLength(0);
        }
        catch (IOException)
        {
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        lock (_gate)
        {
            _inner.Dispose();
        }
    }
}
