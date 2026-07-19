using System.Globalization;

using AmneziaGeo.Dal;

using Serilog.Core;
using Serilog.Events;

namespace AmneziaGeo.Windows.App;

/// <summary>
/// Serilog sink that writes rendered agent-log events into the ageo table of the structured log store.
/// </summary>
internal sealed class LogDbSink(SqliteLogStore store) : ILogEventSink
{
    /// <inheritdoc/>
    public void Emit(LogEvent logEvent)
    {
        var message = logEvent.RenderMessage(CultureInfo.InvariantCulture);
        if (logEvent.Exception is not null)
        {
            message = message + Environment.NewLine + logEvent.Exception;
        }

        store.AppendAgent(
            logEvent.Timestamp.ToUnixTimeMilliseconds(),
            LogLevels.Id(logEvent.Level),
            Source(logEvent),
            message);
    }

    private static string Source(LogEvent logEvent)
    {
        return logEvent.Properties.TryGetValue("Source", out var value) && value is ScalarValue { Value: string source }
            ? source
            : "agent";
    }
}
