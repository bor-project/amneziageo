using System.Globalization;
using System.Text;

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
    /// Whether a stream to a direct range leaves on a protected socket instead of riding the tunnel.
    /// </summary>
    public const string DirectTcp = "direct-tcp";

    /// <summary>
    /// Whether a direct address leaves the tun by name.
    /// </summary>
    public const string ExcludeRoutes = "exclude-routes";

    /// <summary>
    /// Whether the local proxy listens.
    /// </summary>
    public const string ProxyEnabled = "proxy-enabled";

    /// <summary>
    /// SOCKS5 port of the local proxy.
    /// </summary>
    public const string ProxySocksPort = "proxy-socks-port";

    /// <summary>
    /// HTTP port of the local proxy.
    /// </summary>
    public const string ProxyHttpPort = "proxy-http-port";

    /// <summary>
    /// Whether the local proxy admits a client without an account.
    /// </summary>
    public const string ProxyAnonymous = "proxy-anonymous";

    /// <summary>
    /// Accounts the local proxy admits clients under, one "user:password" per line.
    /// </summary>
    public const string ProxyCredentials = "proxy-credentials";

    /// <summary>
    /// User the local proxy asks for; superseded by the account list and read only to carry an old setting over.
    /// </summary>
    public const string ProxyUser = "proxy-user";

    /// <summary>
    /// Password the local proxy asks for; superseded by the account list.
    /// </summary>
    public const string ProxyPassword = "proxy-password";

    /// <summary>
    /// How the tunnel reaches other devices: lan, wifi, or both.
    /// </summary>
    public const string ShareMode = "share-mode";

    /// <summary>
    /// Whether a wired subnet is served as well.
    /// </summary>
    public const string ShareEthernet = "share-ethernet";

    /// <summary>
    /// Network name of the access point.
    /// </summary>
    public const string HotspotSsid = "hotspot-ssid";

    /// <summary>
    /// Password of the access point.
    /// </summary>
    public const string HotspotPassword = "hotspot-password";

    /// <summary>
    /// Band the access point asks for: auto, 2.4, or 5.
    /// </summary>
    public const string HotspotBand = "hotspot-band";

    /// <summary>
    /// Whether subscriptions are re-read on a timer.
    /// </summary>
    public const string SubscriptionAutoRefresh = "subscription-auto-refresh-enabled";

    /// <summary>
    /// How often subscriptions are re-read when the panel names no interval of its own, in hours.
    /// </summary>
    public const string SubscriptionRefreshInterval = "subscription-refresh-interval-hours";

    /// <summary>
    /// Shortest re-read interval, in hours.
    /// </summary>
    public const int SubscriptionIntervalMinHours = 1;

    /// <summary>
    /// Longest re-read interval, in hours.
    /// </summary>
    public const int SubscriptionIntervalMaxHours = 24 * 7;

    /// <summary>
    /// Longest network name a beacon carries, in bytes.
    /// </summary>
    public const int HotspotSsidMaxBytes = 32;

    /// <summary>
    /// Shortest password WPA2 accepts.
    /// </summary>
    public const int HotspotPasswordMinLength = 8;

    /// <summary>
    /// Longest password WPA2 accepts.
    /// </summary>
    public const int HotspotPasswordMaxLength = 63;

    /// <summary>
    /// Reads a route lifetime: whole seconds, 0 (hold nothing) to int.MaxValue. Both processes decide by this, so
    /// what the editor accepts is exactly what the agent stores.
    /// </summary>
    public static bool TryParseRouteTtl(string? text, out int seconds)
    {
        return int.TryParse(text?.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out seconds);
    }

    /// <summary>
    /// Reads a re-read interval: whole hours between 1 and 168.
    /// </summary>
    public static bool TryParseSubscriptionInterval(string? text, out int hours)
    {
        return int.TryParse(text?.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out hours)
            && hours is >= SubscriptionIntervalMinHours and <= SubscriptionIntervalMaxHours;
    }

    /// <summary>
    /// Reads a listening port: a whole number a socket can be bound to.
    /// </summary>
    public static bool TryParseProxyPort(string? text, out int port)
    {
        return int.TryParse(text?.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out port)
            && port is > 0 and <= 65535;
    }

    /// <summary>
    /// Whether a network name fits a beacon: 1 to 32 bytes, no control characters.
    /// </summary>
    public static bool IsValidHotspotSsid(string? text)
    {
        if (text is null || text.Length == 0 || text.Any(char.IsControl))
        {
            return false;
        }

        var bytes = Encoding.UTF8.GetByteCount(text);
        return bytes is > 0 and <= HotspotSsidMaxBytes;
    }

    /// <summary>
    /// Whether a password fits WPA2: 8 to 63 printable characters.
    /// </summary>
    public static bool IsValidHotspotPassword(string? text)
    {
        return text is not null
            && text.Length is >= HotspotPasswordMinLength and <= HotspotPasswordMaxLength
            && !text.Any(char.IsControl);
    }
}
