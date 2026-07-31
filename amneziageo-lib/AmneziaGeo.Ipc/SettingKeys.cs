using System.Globalization;

namespace AmneziaGeo.Ipc;

/// <summary>
/// Setting keys the UI addresses by name over set-setting, with the validation the agent applies to them.
/// </summary>
public static class SettingKeys
{
    /// <summary>
    /// Idle lifetime of a cached route, in seconds.
    /// </summary>
    public const string RouteTtl = "route-ttl-seconds";

    /// <summary>
    /// Reads a route lifetime: whole seconds, 0 (hold nothing) to int.MaxValue. Both processes decide by this, so
    /// what the editor accepts is exactly what the agent stores.
    /// </summary>
    public static bool TryParseRouteTtl(string? text, out int seconds)
    {
        return int.TryParse(text?.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out seconds);
    }
}
