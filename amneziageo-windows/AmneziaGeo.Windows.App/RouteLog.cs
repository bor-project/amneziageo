using AmneziaGeo.Dal;

namespace AmneziaGeo.Windows.App;

/// <summary>
/// Toggleable routing log: route-table changes, matched DNS resolutions, outbound destinations. Levelless -
/// each entry is the original record; a failed route action is marked in the message text, not a synthetic level.
/// </summary>
internal static class RouteLog
{
    /// <summary>
    /// Settings key that toggles the routing log.
    /// </summary>
    public const string SettingKey = "route-log";

    private static volatile bool _enabled;
    private static SqliteLogStore? _store;

    /// <summary>
    /// Whether route actions are currently being recorded.
    /// </summary>
    public static bool Enabled
    {
        get => _enabled;
        set => _enabled = value;
    }

    /// <summary>
    /// Binds the log store the static writer persists rows into.
    /// </summary>
    public static void UseStore(SqliteLogStore store)
    {
        _store = store;
    }

    /// <summary>
    /// Records a route-table add/remove with its target and next hop; a failed action is prefixed FAILED.
    /// </summary>
    public static void Write(string action, string target, string via, bool ok, string? note = null)
    {
        if (!_enabled)
        {
            return;
        }

        var prefix = ok ? string.Empty : "FAILED ";
        Append($"{prefix}{action,-14} {target} via {via}{(string.IsNullOrEmpty(note) ? string.Empty : "  " + note)}");
    }

    /// <summary>
    /// Records a free-form routing event.
    /// </summary>
    public static void Note(string message)
    {
        if (!_enabled)
        {
            return;
        }

        Append(message);
    }

    private static void Append(string body)
    {
        _store?.AppendRoute(DateTimeOffset.Now.ToUnixTimeMilliseconds(), body);
    }
}
