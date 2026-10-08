namespace AmneziaGeo.Decl;

/// <summary>
/// The geo keys of the routing lists the window offers to start a list from.
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
        "geoip:ag-discord-voice",
        "geosite:openai",
        "geosite:telegram",
        "geoip:telegram",
    ];
}
