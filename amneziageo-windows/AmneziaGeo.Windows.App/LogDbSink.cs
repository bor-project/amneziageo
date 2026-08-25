using System.Globalization;

using AmneziaGeo.Dal;

using Serilog.Core;
using Serilog.Events;
using Serilog.Formatting.Display;

namespace AmneziaGeo.Windows.App;

/// <summary>
/// Serilog sink that writes rendered agent-log events into the ageo table of the structured log store.
/// </summary>
internal sealed class LogDbSink(SqliteLogStore store) : ILogEventSink
{
    // Writes string values as they stand: the default rendering wraps every one of them in quotes.
    private static readonly MessageTemplateTextFormatter _message = new("{Message:l}", CultureInfo.InvariantCulture);

    /// <inheritdoc/>
    public void Emit(LogEvent logEvent)
    {
        var message = Render(logEvent);
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

    private static string Render(LogEvent logEvent)
    {
        var writer = new StringWriter(CultureInfo.InvariantCulture);
        _message.Format(logEvent, writer);
        return writer.ToString();
    }

    private static string Source(LogEvent logEvent)
    {
        return logEvent.Properties.TryGetValue("Source", out var value) && value is ScalarValue { Value: string source }
            ? source
            : "agent";
    }
}
