using System.Globalization;
using Serilog.Core;
using Serilog.Events;

namespace AmneziaGeo.Windows.App;

/// <summary>
/// Serilog sink that appends rendered events to the in-memory ring buffer.
/// </summary>
internal sealed class RingBufferSink(LogRingBuffer buffer) : ILogEventSink
{
    /// <inheritdoc/>
    public void Emit(LogEvent logEvent)
    {
        var message = logEvent.RenderMessage(CultureInfo.InvariantCulture);
        var line = string.Create(
            CultureInfo.InvariantCulture,
            $"{logEvent.Timestamp.LocalDateTime:HH:mm:ss} [{Level(logEvent.Level)}] {Source(logEvent)} {message}");
        if (logEvent.Exception is not null)
        {
            line += $" - {logEvent.Exception.Message}";
        }

        buffer.Add(line);
    }

    private static string Source(LogEvent logEvent)
    {
        return logEvent.Properties.TryGetValue("Source", out var value) && value is ScalarValue { Value: string source }
            ? source
            : "agent";
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
