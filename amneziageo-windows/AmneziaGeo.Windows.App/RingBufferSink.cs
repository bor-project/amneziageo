using System.Globalization;
using Serilog.Core;
using Serilog.Events;

namespace AmneziaGeo.Windows.App;

/// <summary>
/// A Serilog sink that appends each rendered log event to the in-memory <see cref="LogRingBuffer"/>, so
/// recent agent activity can be surfaced to the UI over IPC (the home-screen journal).
/// </summary>
internal sealed class RingBufferSink(LogRingBuffer buffer) : ILogEventSink
{
    /// <inheritdoc/>
    public void Emit(LogEvent logEvent)
    {
        var message = logEvent.RenderMessage(CultureInfo.InvariantCulture);
        var line = string.Create(
            CultureInfo.InvariantCulture,
            $"{logEvent.Timestamp.LocalDateTime:HH:mm:ss} {Level(logEvent.Level)} {message}");
        if (logEvent.Exception is not null)
        {
            line += $" — {logEvent.Exception.Message}";
        }

        buffer.Add(line);
    }

    private static string Level(LogEventLevel level)
    {
        return level switch
        {
            LogEventLevel.Verbose => "TRC",
            LogEventLevel.Debug => "DBG",
            LogEventLevel.Information => "INF",
            LogEventLevel.Warning => "WRN",
            LogEventLevel.Error => "ERR",
            LogEventLevel.Fatal => "FTL",
            _ => "INF",
        };
    }
}
