using System.Globalization;

namespace AmneziaGeo.Decl;

/// <summary>
/// How a config picks the MTU its tunnel comes up with.
/// </summary>
public enum MtuMode
{
    /// <summary>
    /// The largest size the path and the profile carry, never above the one the config declares.
    /// </summary>
    Auto = 0,

    /// <summary>
    /// The size the config text declares, and nothing else.
    /// </summary>
    Config = 1,

    /// <summary>
    /// The size stored for the config.
    /// </summary>
    Custom = 2,
}

/// <summary>
/// Reads a mode and the sizes one is allowed to carry.
/// </summary>
public static class MtuModes
{
    /// <summary>
    /// Smallest MTU a tunnel is allowed to come up with.
    /// </summary>
    public const int MinMtu = 576;

    /// <summary>
    /// Largest MTU a tunnel is allowed to come up with.
    /// </summary>
    public const int MaxMtu = 1500;

    /// <summary>
    /// The mode a stored number stands for; anything unknown follows the link.
    /// </summary>
    public static MtuMode From(int stored) => stored switch
    {
        (int)MtuMode.Config => MtuMode.Config,
        (int)MtuMode.Custom => MtuMode.Custom,
        _ => MtuMode.Auto,
    };

    /// <summary>
    /// Reads a mode written by name; anything else keeps the mode already stored.
    /// </summary>
    public static MtuMode Parse(string text, MtuMode stored) => (text?.Trim() ?? string.Empty).ToLowerInvariant() switch
    {
        "auto" => MtuMode.Auto,
        "config" => MtuMode.Config,
        "custom" => MtuMode.Custom,
        _ => stored,
    };

    /// <summary>
    /// Reads a mode written as auto, config, or a size, which stands for the custom mode.
    /// </summary>
    public static bool TryParse(string text, out MtuMode mode, out int size)
    {
        mode = MtuMode.Auto;
        size = 0;
        var token = text?.Trim() ?? string.Empty;
        if (token.Equals("auto", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (token.Equals("config", StringComparison.OrdinalIgnoreCase))
        {
            mode = MtuMode.Config;
            return true;
        }

        if (int.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out size) && size is >= MinMtu and <= MaxMtu)
        {
            mode = MtuMode.Custom;
            return true;
        }

        size = 0;
        return false;
    }

    /// <summary>
    /// The name a mode is written under.
    /// </summary>
    public static string Text(MtuMode mode) => mode switch
    {
        MtuMode.Config => "config",
        MtuMode.Custom => "custom",
        _ => "auto",
    };

    /// <summary>
    /// Whether the mode leaves the size to the client rather than to the person.
    /// </summary>
    public static bool IsReadOnly(MtuMode mode) => mode != MtuMode.Custom;
}
