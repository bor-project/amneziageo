using Serilog.Core;
using Serilog.Events;

namespace AmneziaGeo.Windows.App;

/// <summary>
/// Owns the live Serilog level switch so the verbosity can be changed at runtime (#82) without a restart.
/// Wraps Serilog behind a small surface so the broker and CLI can raise/lower the level without depending on
/// Serilog directly. Three user-facing levels are exposed: "info" (default), "debug", and "trace".
/// </summary>
internal sealed class LogLevelController
{
    /// <summary>
    /// The switch Serilog is configured to obey (AppHost binds MinimumLevel.ControlledBy to it).
    /// </summary>
    public LoggingLevelSwitch Switch { get; } = new(LogEventLevel.Information);

    /// <summary>
    /// The current level as a persisted token ("info" / "debug" / "trace").
    /// </summary>
    public string Current => Format(Switch.MinimumLevel);

    /// <summary>
    /// Applies a token to the live switch. Unknown tokens fall back to Information.
    /// </summary>
    public void Set(string? token)
    {
        Switch.MinimumLevel = Parse(token);
    }

    /// <summary>
    /// Maps a persisted token to a Serilog level. "trace" maps to Verbose (the RingBufferSink already renders
    /// Verbose as TRC); anything unrecognized is Information so a bad value never silences the log.
    /// </summary>
    public static LogEventLevel Parse(string? token)
    {
        return token?.Trim().ToLowerInvariant() switch
        {
            "trace" => LogEventLevel.Verbose,
            "debug" => LogEventLevel.Debug,
            _ => LogEventLevel.Information,
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
            _ => "info",
        };
    }

    /// <summary>
    /// Whether a token is one of the three exposed levels.
    /// </summary>
    public static bool IsValid(string? token)
    {
        return token is "info" or "debug" or "trace";
    }
}
