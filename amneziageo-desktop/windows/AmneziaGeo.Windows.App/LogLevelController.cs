using Serilog.Core;
using Serilog.Events;

namespace AmneziaGeo.Windows.App;

/// <summary>
/// Owns the live Serilog level switch for runtime verbosity changes.
/// </summary>
internal sealed class LogLevelController
{
    /// <summary>
    /// The default verbosity token.
    /// </summary>
    public const string DefaultToken = "error";

    /// <summary>
    /// The switch Serilog is configured to obey (AppHost binds MinimumLevel.ControlledBy to it).
    /// </summary>
    public LoggingLevelSwitch Switch { get; } = new(LogEventLevel.Error);

    /// <summary>
    /// The current level as a persisted token ("error" / "info" / "debug" / "trace").
    /// </summary>
    public string Current => Format(Switch.MinimumLevel);

    /// <summary>
    /// Applies a token to the live switch. Unknown tokens fall back to Error.
    /// </summary>
    public void Set(string? token)
    {
        Switch.MinimumLevel = Parse(token);
    }

    /// <summary>
    /// Maps a persisted token to a Serilog level.
    /// </summary>
    public static LogEventLevel Parse(string? token)
    {
        return token?.Trim().ToLowerInvariant() switch
        {
            "trace" => LogEventLevel.Verbose,
            "debug" => LogEventLevel.Debug,
            "info" => LogEventLevel.Information,
            _ => LogEventLevel.Error,
        };
    }

    /// <summary>
    /// Maps a Serilog level back to a persisted token.
    /// </summary>
    public static string Format(LogEventLevel level)
    {
        return level switch
        {
            LogEventLevel.Verbose => "trace",
            LogEventLevel.Debug => "debug",
            LogEventLevel.Information => "info",
            _ => DefaultToken,
        };
    }

    /// <summary>
    /// Whether a token is one of the four exposed levels.
    /// </summary>
    public static bool IsValid(string? token)
    {
        return token is "error" or "info" or "debug" or "trace";
    }
}
