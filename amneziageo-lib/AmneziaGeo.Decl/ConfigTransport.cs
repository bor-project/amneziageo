using System.Globalization;

namespace AmneziaGeo.Decl;

/// <summary>
/// Per-config transport: WebSocket (the tunnel carried inside a websocket to the front the server offers, else the one the config names, else the one of the settings), tunnel MTU (default 1420, valid 576-1500) with the mode that picks it, the IPv6 opt-in (off keeps the tunnel v4-only; on only when the server has an IPv6 address), the router (on decides every connection on its own; off leaves every verdict to the route table), inbound access (off refuses everything arriving from the tunnel; on accepts it from the server alone, or from the whole tunnel network), routing (off keeps the config off the routing list), and the websocket front of the settings (a host or a ws(s):// address, and a port; empty and zero take the host and the port of the Endpoint).
/// </summary>
public sealed record ConfigTransport(
    string Name,
    bool UseWebSocket,
    int Mtu = 1420,
    bool UseIpv6 = false,
    MtuMode MtuMode = MtuMode.Auto,
    bool UseRouter = true,
    bool AllowInbound = false,
    bool InboundNetwork = false,
    bool UseRouting = true,
    string WebSocketHost = "",
    int WebSocketPort = 0)
{
    /// <summary>
    /// Returns the port a text names: zero for an empty text, minus one for a text that is not a port.
    /// </summary>
    public static int PortOf(string? text)
    {
        var value = text?.Trim() ?? string.Empty;
        if (value.Length == 0)
        {
            return 0;
        }

        return int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var port) && port is >= 1 and <= 65535
            ? port
            : -1;
    }

    /// <summary>
    /// Returns the port a command sends: zero where it names none, minus one for a text that is not a port.
    /// </summary>
    public static int PortSent(string? text) => (text ?? string.Empty).Trim() is "0" ? 0 : PortOf(text);
}
