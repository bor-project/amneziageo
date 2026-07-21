namespace AmneziaGeo.Windows.App;

/// <summary>
/// Parsed wstunnel server target for a config: bare host or wss:// URL with optional path token and basic-auth.
/// </summary>
internal readonly record struct WsEndpoint(string Host, int Port, string PathPrefix, string Credentials)
{
    /// <summary>
    /// Parses the per-config WebSocket host field: empty, a bare host, or a full ws(s):// URL.
    /// Falls back to the config's Endpoint host and the separate port field when absent.
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
            // UserInfo is percent-escaped by the UI; unescape back to the literal form wstunnel expects.
            var credentials = string.IsNullOrEmpty(uri.UserInfo) ? string.Empty : Uri.UnescapeDataString(uri.UserInfo);
            return new WsEndpoint(uri.Host, port, path, credentials);
        }

        return new WsEndpoint(value, fallbackPort, string.Empty, string.Empty);
    }
}
