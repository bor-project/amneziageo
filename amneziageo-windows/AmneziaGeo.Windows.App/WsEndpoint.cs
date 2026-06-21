namespace AmneziaGeo.Windows.App;

/// <summary>
/// The parsed wstunnel server target for a config: a bare host uses the separate port field and no auth,
/// while a full <c>wss://[user:pass@]host[:port]/[token]</c> URL carries the port, an optional path token
/// (matched server-side by --restrict-http-upgrade-path-prefix), and optional basic-auth credentials in
/// one string. Both auth forms are optional and independent.
/// </summary>
internal readonly record struct WsEndpoint(string Host, int Port, string PathPrefix, string Credentials)
{
    /// <summary>
    /// Parses the per-config WebSocket host field. <paramref name="hostOrUrl"/> may be empty (reuse the
    /// config's own Endpoint host), a bare host, or a full ws(s):// URL. <paramref name="fallbackPort"/>
    /// is the separate port field, used when the URL omits a port or the input is a bare host;
    /// <paramref name="fallbackHost"/> is the config's Endpoint host, used when the field is empty.
    /// </summary>
    public static WsEndpoint Parse(string? hostOrUrl, int fallbackPort, string fallbackHost)
    {
        var value = hostOrUrl?.Trim() ?? string.Empty;
        if (value.Length == 0)
        {
            return new WsEndpoint(fallbackHost, fallbackPort, string.Empty, string.Empty);
        }

        if (value.Contains("://", StringComparison.Ordinal)
            && Uri.TryCreate(value, UriKind.Absolute, out var uri)
            && !string.IsNullOrEmpty(uri.Host))
        {
            var port = uri.Port > 0 ? uri.Port : fallbackPort;
            var path = uri.AbsolutePath.Trim('/');
            return new WsEndpoint(uri.Host, port, path, uri.UserInfo);
        }

        return new WsEndpoint(value, fallbackPort, string.Empty, string.Empty);
    }
}
