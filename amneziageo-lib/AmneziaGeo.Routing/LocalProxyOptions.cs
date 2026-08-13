using System.Globalization;

namespace AmneziaGeo.Routing;

/// <summary>
/// What the local proxy listens on and who may reach it. The ports are the ones the v2ray family uses, so a
/// client set up for a neighbouring application finds this one where it expects it.
/// </summary>
public sealed record LocalProxyOptions
{
    /// <summary>
    /// SOCKS5 port.
    /// </summary>
    public const int DefaultSocksPort = 10808;

    /// <summary>
    /// HTTP port.
    /// </summary>
    public const int DefaultHttpPort = 10809;

    /// <summary>
    /// Whether the listener runs.
    /// </summary>
    public bool Enabled { get; init; }

    /// <summary>
    /// SOCKS5 port; both ports take either protocol, so a client that speaks only one still works on both.
    /// </summary>
    public int SocksPort { get; init; } = DefaultSocksPort;

    /// <summary>
    /// HTTP port.
    /// </summary>
    public int HttpPort { get; init; } = DefaultHttpPort;

    /// <summary>
    /// Whether other machines on the local network may use the proxy; off keeps it on loopback.
    /// </summary>
    public bool AllowLan { get; init; }

    /// <summary>
    /// User clients authenticate as; empty asks for no credentials.
    /// </summary>
    public string User { get; init; } = string.Empty;

    /// <summary>
    /// Password that goes with the user.
    /// </summary>
    public string Password { get; init; } = string.Empty;

    /// <summary>
    /// Whether clients have to authenticate.
    /// </summary>
    public bool RequiresAuth => User.Length > 0;

    /// <summary>
    /// Ports the listener takes, without the duplicate when both settings name one port.
    /// </summary>
    public IReadOnlyList<int> Ports => SocksPort == HttpPort ? [SocksPort] : [SocksPort, HttpPort];

    /// <summary>
    /// Whether a port can be listened on.
    /// </summary>
    public static bool IsPort(int port)
    {
        return port is > 0 and <= 65535;
    }

    /// <summary>
    /// Whether the settings can be listened on as they stand.
    /// </summary>
    public bool IsValid()
    {
        return IsPort(SocksPort) && IsPort(HttpPort);
    }
}
