using Serilog.Events;

namespace AmneziaGeo.Windows.App;

/// <summary>
/// Maps between Serilog levels, the log_levels dictionary ids (severity order), and the viewer filter tokens.
/// </summary>
internal static class LogLevels
{
    /// <summary>
    /// Dictionary id for a Serilog level; higher id is more severe (matches log_levels seeding).
    /// </summary>
    public static int Id(LogEventLevel level)
    {
        return level switch
        {
            LogEventLevel.Verbose => 1,
            LogEventLevel.Debug => 2,
            LogEventLevel.Information => 3,
            LogEventLevel.Warning => 4,
            LogEventLevel.Error => 5,
            LogEventLevel.Fatal => 6,
            _ => 3,
        };
    }

    /// <summary>
    /// Minimum dictionary id for a viewer "show this level and more severe" token; null means no floor (show all).
    /// </summary>
    public static int? MinId(string? token)
    {
        return token?.Trim().ToLowerInvariant() switch
        {
            "debug" => 2,
            "info" => 3,
            "warning" => 4,
            "error" => 5,
            _ => null,
        };
    }
}
