namespace AmneziaGeo.Decl;

/// <summary>
/// The geo keys of the routing lists the window offers to start a list from.
/// </summary>
public static class RoutingDefaults
{
    /// <summary>
    /// The geo keys of the list of unavailable sites: the services closed off in the country and the ones that turn
    /// it away themselves, one by one. They all go through the tunnel.
    /// </summary>
    public static readonly string[] Unavailable =
    [
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

    /// <summary>
    /// The app rules of the list of unavailable apps: the programs of the same services, named the way people know
    /// them, which each device finds itself. They all go through the tunnel.
    /// </summary>
    public static readonly string[] UnavailableApps =
    [
        "app:YouTube",
        "app:Instagram",
        "app:Facebook",
        "app:WhatsApp",
        "app:X",
        "app:Discord",
        "app:ChatGPT",
        "app:Telegram",
    ];
}
