namespace AmneziaGeo.Decl;

/// <summary>
/// The routing list a fresh install starts with, shared by the agent that puts it in and the window that offers it.
/// </summary>
public static class RoutingDefaults
{
    /// <summary>
    /// The geo keys of the services closed off in the country.
    /// </summary>
    public static readonly string[] Closed = ["geosite:ru-blocked", "geoip:ru-blocked"];

    /// <summary>
    /// The geo keys of the list of unavailable sites: the services closed off in the country and the ones that turn
    /// it away themselves. They all go through the tunnel.
    /// </summary>
    public static readonly string[] Unavailable =
    [
        .. Closed,
        "geosite:youtube",
        "geosite:meta",
        "geosite:whatsapp",
        "geosite:twitter",
        "geosite:discord",
        "geosite:openai",
        "geosite:telegram",
        "geoip:telegram",
    ];

    /// <summary>
    /// Returns the name of the list of unavailable sites in a language given by its letters. The agents read no
    /// translations, so the name stands here the way the window shows it (Preset_ClosedName).
    /// </summary>
    public static string UnavailableName(string? language) =>
        language is not null && language.StartsWith("ru", StringComparison.OrdinalIgnoreCase)
            ? "Недоступные сайты"
            : "Unavailable sites";
}
