using System.Globalization;

namespace AmneziaGeo.Decl;

/// <summary>
/// The websocket front a config carries its tunnel to: the host of its Endpoint and the port its server offers.
/// </summary>
public readonly record struct WsEndpoint(string Host, int Port)
{
    /// <summary>
    /// Returns the front of a config text, or null when its server offers none.
    /// </summary>
    public static WsEndpoint? Of(string? text, ServerOffer? offer)
    {
        var host = ConfigServices.Host(text);
        var port = offer?.WebSocketPort ?? 0;

        return host.Length > 0 && port > 0 ? new WsEndpoint(host, port) : null;
    }

    /// <summary>
    /// Host and port for display.
    /// </summary>
    public string Display()
    {
        var host = Host.Contains(':', StringComparison.Ordinal) && !Host.StartsWith('[') ? $"[{Host}]" : Host;

        return host.Length == 0 ? string.Empty : string.Create(CultureInfo.InvariantCulture, $"{host}:{Port}");
    }
}
