using AmneziaGeo.Decl;

namespace AmneziaGeo.Routing;

/// <summary>
/// What the local proxy listens on and who may reach it. It is offered to the local network as a matter of
/// course - an application of this machine follows the routing rules on its own - so what the settings decide
/// is the ports and whether an account is asked for. The ports are the ones the v2ray family uses, so a client
/// set up for a neighbouring application finds this one where it expects it.
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
    /// Whether a client is admitted without an account.
    /// </summary>
    public bool AllowAnonymous { get; init; }

    /// <summary>
    /// Accounts clients authenticate as, one "user:password" per line.
    /// </summary>
    public string Credentials { get; init; } = string.Empty;

    /// <summary>
    /// Whether clients have to authenticate.
    /// </summary>
    public bool RequiresAuth => !AllowAnonymous;

    /// <summary>
    /// Whether the settings admit nobody: a password is asked for and no account answers it.
    /// </summary>
    public bool AdmitsNobody => RequiresAuth && Accounts().Count == 0;

    /// <summary>
    /// Ports the listener takes, without the duplicate when both settings name one port.
    /// </summary>
    public IReadOnlyList<int> Ports => SocksPort == HttpPort ? [SocksPort] : [SocksPort, HttpPort];

    /// <summary>
    /// The accounts the credentials name.
    /// </summary>
    public IReadOnlyList<ProxyAccount> Accounts()
    {
        return ProxyCredentials.Parse(Credentials);
    }

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
