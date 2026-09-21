using System.Globalization;

namespace AmneziaGeo.Decl;

/// <summary>
/// Parsed wstunnel server target for a config: bare host or wss:// URL with optional path token and basic-auth.
/// </summary>
public readonly record struct WsEndpoint(string Host, int Port, string PathPrefix, string Credentials)
{
    /// <summary>
    /// The port of a front when neither the settings, the config nor its Endpoint name one.
    /// </summary>
    public const int DefaultPort = 443;

    /// <summary>
    /// The comment a server of ours names its websocket front under.
    /// </summary>
    public const string FrontLine = "AmneziaGeo WebSocket";

    /// <summary>
    /// Returns the front the tunnel is carried to: the address and the port of the settings where they hold, else
    /// the front the config names, else the host and the port of its Endpoint.
    /// </summary>
    public static WsEndpoint Of(string? hostOrUrl, int port, string? endpoint, string? front)
    {
        var fallback = Default(endpoint, front);
        var own = Read(hostOrUrl);
        var chosen = own is { Port: > 0 } named
            ? named.Port
            : port is > 0 and <= 65535 ? port : fallback.Port;

        return (own ?? fallback) with { Port = chosen };
    }

    /// <summary>
    /// Returns the front that stands while the settings name none: the one the config names where it holds, else
    /// the host and the port of the Endpoint.
    /// </summary>
    public static WsEndpoint Default(string? endpoint, string? front)
    {
        var (host, port) = Split(endpoint);
        var taken = port > 0 ? port : DefaultPort;
        if (Read(front) is { } named)
        {
            return named with { Port = named.Port > 0 ? named.Port : taken };
        }

        return new WsEndpoint(host, taken, string.Empty, string.Empty);
    }

    /// <summary>
    /// Returns what the line "# AmneziaGeo WebSocket" of a config text names, empty when the text carries none.
    /// </summary>
    public static string FrontOf(string? text)
    {
        foreach (var line in (text ?? string.Empty).Split('\n'))
        {
            var trimmed = line.Trim();
            if (!trimmed.StartsWith('#'))
            {
                continue;
            }

            var comment = trimmed[1..].TrimStart();
            var equals = comment.IndexOf('=');
            if (equals > 0 && string.Equals(comment[..equals].Trim(), FrontLine, StringComparison.OrdinalIgnoreCase))
            {
                return comment[(equals + 1)..].Trim();
            }
        }

        return string.Empty;
    }

    /// <summary>
    /// Returns the front the tunnel of a config text is carried to, against its Endpoint and the front it names.
    /// </summary>
    public static WsEndpoint For(string? hostOrUrl, int port, string? text) =>
        Of(hostOrUrl, port, EndpointOf(text), FrontOf(text));

    /// <summary>
    /// Tells whether an address is one the tunnel can be carried to; an empty one leaves the front to the config.
    /// </summary>
    public static bool Dials(string? hostOrUrl) =>
        (hostOrUrl ?? string.Empty).Trim().Length == 0 || Read(hostOrUrl) is not null;

    /// <summary>
    /// Host and port for display, without the path token and the basic-auth credentials.
    /// </summary>
    public string Display()
    {
        var host = Host.Contains(':', StringComparison.Ordinal) && !Host.StartsWith('[') ? $"[{Host}]" : Host;

        return host.Length == 0 ? string.Empty : string.Create(CultureInfo.InvariantCulture, $"{host}:{Port}");
    }

    // Reads an address: a bare host, a host with its port, or a ws(s):// URL; null when it names no host to dial.
    private static WsEndpoint? Read(string? value)
    {
        var text = value?.Trim() ?? string.Empty;
        if (text.Length == 0)
        {
            return null;
        }

        var url = text.Contains("://", StringComparison.Ordinal) ? text : "wss://" + text;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || uri.Scheme is not ("wss" or "ws")
            || uri.HostNameType is not (UriHostNameType.Dns or UriHostNameType.IPv4 or UriHostNameType.IPv6)
            || uri.Host.Length == 0
            || uri.Query.Length > 0
            || uri.Fragment.Length > 0)
        {
            return null;
        }

        var port = Named(url) ? uri.Port : 0;
        if (port is < 0 or > 65535)
        {
            return null;
        }

        // UserInfo is percent-escaped by the UI; unescape back to the literal form wstunnel expects.
        var credentials = uri.UserInfo.Length == 0 ? string.Empty : Uri.UnescapeDataString(uri.UserInfo);

        return new WsEndpoint(uri.Host, port, uri.AbsolutePath.Trim('/'), credentials);
    }

    // Tells whether a URL names its port rather than leaving it to the scheme.
    private static bool Named(string url)
    {
        var authority = url[(url.IndexOf("://", StringComparison.Ordinal) + 3)..];
        var end = authority.IndexOfAny(['/', '?', '#']);
        if (end >= 0)
        {
            authority = authority[..end];
        }

        authority = authority[(authority.LastIndexOf('@') + 1)..];

        return authority.StartsWith('[')
            ? authority.Contains("]:", StringComparison.Ordinal)
            : authority.Contains(':', StringComparison.Ordinal);
    }

    // Reads the Endpoint a config text dials.
    private static string EndpointOf(string? text)
    {
        foreach (var line in (text ?? string.Empty).Split('\n'))
        {
            var trimmed = line.Trim();
            var equals = trimmed.IndexOf('=');
            if (equals > 0 && string.Equals(trimmed[..equals].Trim(), "Endpoint", StringComparison.OrdinalIgnoreCase))
            {
                return trimmed[(equals + 1)..].Trim();
            }
        }

        return string.Empty;
    }

    // Splits an Endpoint into its host, brackets aside, and its port.
    private static (string Host, int Port) Split(string? endpoint)
    {
        var value = endpoint?.Trim() ?? string.Empty;
        var colon = value.LastIndexOf(':');
        var bracketed = value.StartsWith('[') && colon > value.LastIndexOf(']');
        if (colon <= 0 || (!bracketed && value.IndexOf(':') != colon))
        {
            return (value.Trim('[', ']'), 0);
        }

        var port = int.TryParse(value[(colon + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out var parsed)
            && parsed is > 0 and <= 65535
                ? parsed
                : 0;

        return (value[..colon].Trim('[', ']'), port);
    }
}
