using System.Globalization;
using System.Text;

using AmneziaGeo.Dal;

namespace AmneziaGeo.Windows.App;

/// <summary>
/// Renders a structured log row to a single greppable line: timestamp, optional level, optional source,
/// message. The shared shape used by the in-app viewer, the export, and the diagnostics bundle.
/// </summary>
internal static class LogFormat
{
    /// <summary>
    /// "yyyy-MM-dd HH:mm:ss.fff [LVL] source message" for the agent log; "yyyy-MM-dd HH:mm:ss.fff message"
    /// for the levelless routing log.
    /// </summary>
    public static string Render(LogRow row)
    {
        var ts = DateTimeOffset.FromUnixTimeMilliseconds(row.UnixMs).LocalDateTime
            .ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture);
        var sb = new StringBuilder(ts);
        if (row.Level is not null)
        {
            sb.Append(" [").Append(row.Level).Append(']');
        }

        if (!string.IsNullOrEmpty(row.Source))
        {
            sb.Append(' ').Append(row.Source);
        }

        sb.Append(' ').Append(row.Message);
        return sb.ToString();
    }
}
