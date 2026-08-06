using Microsoft.Extensions.Logging;

namespace AmneziaGeo.Linux.App;

/// <summary>
/// Writes what a shared component logs into the agent log.
/// </summary>
internal sealed class AgentLogger<T>(AgentLog log, string source) : ILogger<T>
{
    /// <inheritdoc/>
    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull => null;

    /// <inheritdoc/>
    public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

    /// <inheritdoc/>
    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        if (logLevel == LogLevel.None)
        {
            return;
        }

        var message = formatter(state, exception);
        switch (logLevel)
        {
            case LogLevel.Critical:
            case LogLevel.Error:
                log.Error(source, message, exception);
                break;
            case LogLevel.Warning:
                log.Warn(source, message);
                break;
            case LogLevel.Information:
                log.Info(source, message);
                break;
            default:
                log.Debug(source, exception is null ? message : $"{message}{Environment.NewLine}{exception}");
                break;
        }
    }
}
