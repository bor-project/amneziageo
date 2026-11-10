namespace AmneziaGeo.Decl;

/// <summary>
/// Describes signed HTTP requests.
/// </summary>
public sealed record OfferedProxyHttp(int Version, string Method, string DataPlacement);

/// <summary>
/// Describes QUIC framing.
/// </summary>
public sealed record OfferedProxyQuic(int Version, string Protocol, string Datagrams);

/// <summary>
/// Validates transport modes and protocol versions.
/// </summary>
public static class ProxyTransportModes
{
    public static bool IsHttp(OfferedProxy proxy) => proxy.Transport is "http" or "xhttp";
    public static bool UsesCertificate(OfferedProxy proxy) => proxy.Transport == "xhttp"
        ? proxy.Xhttp?.Scheme != "http" : proxy.Tls.Enabled;
    public static string Method(OfferedProxy proxy) => proxy.Http?.Method ?? proxy.Xhttp?.UplinkHttpMethod ?? "";
    public static bool Unconfirmed(OfferedProxy proxy) => proxy.Transport == "xhttp" && proxy.Xhttp?.Version != 2;
    public static bool IsGet(OfferedProxy proxy) => IsHttp(proxy) && Method(proxy) == "GET";
    public static bool RuntimeSupported(OfferedProxy proxy, bool fullRuntime) => proxy.Transport == "websocket" || IsGet(proxy)
        || fullRuntime && (IsHttp(proxy) || proxy.Transport == "quic");

    public static OfferedProxy Normalize(OfferedProxy proxy) => Normalize(proxy, null);

    public static OfferedProxy Normalize(OfferedProxy proxy, OfferedProxy? previous) => proxy.Transport == "xhttp" && Valid(proxy)
        && (proxy.Xhttp?.Version == 2 || previous is { Transport: "http", Http: { Version: 2, Method: "GET" } }
            && TransportProxyChanges.SameDefinition(previous, ConfirmGet(proxy)))
        ? ConfirmGet(proxy) : proxy;

    public static OfferedProxy ConfirmGet(OfferedProxy proxy) => proxy with
    {
        Transport = "http", Tls = proxy.Tls with { Enabled = UsesCertificate(proxy) },
        Auth = proxy.Auth with { Scheme = "hmac-sha256-v2" }, Xhttp = null,
        Http = new OfferedProxyHttp(2, "GET", "header"), Quic = null,
    };

    public static bool Valid(OfferedProxy proxy) => proxy is
        { Tls: { ServerName: not null, Pin: not null }, Auth: { Scheme: not null, Credential: not null } }
        && proxy.Auth.Credential.Length is > 0 and <= 512
        && proxy.Tls.Enabled && UsesCertificate(proxy)
        && (UsesCertificate(proxy)
            ? Uri.CheckHostName(proxy.Tls.ServerName) != UriHostNameType.Unknown
                && (proxy.Tls.Pin.Length == 0 || proxy.Tls.Pin.Length == 64 && proxy.Tls.Pin.All(char.IsAsciiHexDigit))
            : proxy.Tls.Pin.Length == 0)
        && (proxy.Transport switch
        {
            "websocket" => proxy.Auth.Scheme == "bearer" && Bearer(proxy.Auth.Credential)
                && proxy.Http is null && proxy.Quic is null && proxy.Xhttp is null,
            "http" => proxy.Auth.Scheme == "hmac-sha256-v2" && Credential(proxy.Auth.Credential)
                && proxy.Http is { Version: 2, Method: "GET", DataPlacement: "header" }
                    or { Version: 2, Method: "POST", DataPlacement: "body" }
                && proxy.Quic is null && proxy.Xhttp is null,
            "quic" => UsesCertificate(proxy) && proxy.Auth.Scheme == "bearer" && Bearer(proxy.Auth.Credential)
                && proxy.Quic is { Version: 1, Protocol: "webtransport", Datagrams: "framed-stream" }
                && proxy.Http is null && proxy.Xhttp is null,
            "xhttp" => proxy.Auth.Scheme == "vless" && Credential(proxy.Auth.Credential)
                && proxy.Xhttp is { Mode: "packet-up", UplinkHttpMethod: "GET", UplinkDataPlacement: "header", HttpVersion: "2",
                    ScMaxEachPostBytes: > 0 and <= 2048, ScMinPostsIntervalMs: >= 1 and <= 1000,
                    UplinkChunkSize: > 0 and <= 2048, XPaddingBytes: "100-100", Scheme: null or "https" or "http", Version: 0 or 2 }
                && proxy.Http is null && proxy.Quic is null,
            _ => false,
        });

    public static bool Credential(string value) => Guid.TryParse(value, out var id) && id != Guid.Empty;
    public static bool Bearer(string value) => value.Length is > 0 and <= 512
        && value.All(letter => char.IsAsciiLetterOrDigit(letter) || letter is '-' or '.' or '_' or '~' or '+' or '/' or '=');

    /// <summary>
    /// Projects signed GET onto the existing runtime profile.
    /// </summary>
    public static OfferedProxy GetRuntime(OfferedProxy proxy)
    {
        if (!Valid(proxy))
        {
            throw new TransportProxyFault("bad-proxy");
        }

        if (!IsGet(proxy))
        {
            throw new TransportProxyFault("proxy-platform-unsupported");
        }

        return proxy.Transport == "xhttp" ? proxy : proxy with
        {
            Transport = "xhttp", Http = null, Quic = null, Auth = proxy.Auth with { Scheme = "vless" },
            Xhttp = new OfferedXHttp("packet-up", "GET", "header", Scheme: "https", Version: 2),
        };
    }
}
