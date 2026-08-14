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
    /// Reads a route lifetime: whole seconds, 0 (hold nothing) to int.MaxValue. Both processes decide by this, so
    /// what the editor accepts is exactly what the agent stores.
    /// </summary>
    public static bool TryParseRouteTtl(string? text, out int seconds)
    {
        return int.TryParse(text?.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out seconds);
    }

    /// <summary>
    /// Reads a listening port: a whole number a socket can be bound to.
    /// </summary>
    public static bool TryParseProxyPort(string? text, out int port)
    {
        return int.TryParse(text?.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out port)
            && port is > 0 and <= 65535;
    }
}
