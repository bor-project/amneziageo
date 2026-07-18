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
    // Throttles the size check + roll so a persistently shared file never turns Emit into a per-line storm.
    private static readonly TimeSpan RollCheckInterval = TimeSpan.FromSeconds(15);

    private readonly string _path;
    private readonly string _template;
    private readonly object _gate = new();
    private Logger _inner;
    private DateTime _nextRollCheckUtc = DateTime.MinValue;

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
            RollIfNeeded();
            _inner.Write(logEvent);
        }
    }

    // Bounds the agent log to the same footprint as the routing log: past the roll threshold, release the
    // file, rotate it, and reopen fresh. Checked at most once per interval (not per line): another process (a
    // per-tunnel service) may hold the shared file, so the rename fails and only retries next interval,
    // succeeding once no one else holds it - without a stat + dispose/reopen on every logged line.
    private void RollIfNeeded()
    {
        var now = DateTime.UtcNow;
        if (now < _nextRollCheckUtc)
        {
            return;
        }

        _nextRollCheckUtc = now + RollCheckInterval;

        long length;
        try
        {
            var info = new FileInfo(_path);
            if (!info.Exists)
            {
                return;
            }

            length = info.Length;
        }
        catch (IOException)
        {
            return;
        }

        if (length <= LogRoller.MaxBytes)
        {
            return;
        }

        _inner.Dispose();
        try
        {
            LogRoller.Roll(_path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
        finally
        {
            _inner = Build();
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
