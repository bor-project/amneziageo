using Serilog.Core;
using Serilog.Events;

namespace AmneziaGeo.Windows.App;

/// <summary>
/// Adds a short "Source" property (the logger's class name from SourceContext) for the log line's source column.
/// </summary>
internal sealed class LogSourceEnricher : ILogEventEnricher
{
    /// <inheritdoc/>
    public void Enrich(LogEvent logEvent, ILogEventPropertyFactory propertyFactory)
    {
        logEvent.AddPropertyIfAbsent(propertyFactory.CreateProperty("Source", ShortSource(logEvent)));
    }

    // Last dotted segment of SourceContext (e.g. "...App.AgentStatusBroker" -> "AgentStatusBroker"); "agent"
    // when the event carries no category.
    private static string ShortSource(LogEvent logEvent)
    {
        if (logEvent.Properties.TryGetValue("SourceContext", out var value)
            && value is ScalarValue { Value: string context } && context.Length > 0)
        {
            var cut = context.LastIndexOf('.');
            return cut >= 0 && cut < context.Length - 1 ? context[(cut + 1)..] : context;
        }

        return "agent";
    }
}
