using System.Globalization;

namespace AmneziaGeo.Decl;

/// <summary>
/// Per-config transport: WebSocket (wstunnel) host/port to carry UDP over TCP (empty and zero take the front the config names, else the host and the port of the Endpoint), tunnel MTU (default 1420, valid 576-1500) with the mode that picks it, the IPv6 opt-in (off keeps the tunnel v4-only; on only when the server has an IPv6 address), the router (on decides every connection on its own; off leaves every verdict to the route table), and inbound access (off refuses everything arriving from the tunnel; on accepts it from the server alone, or from the whole tunnel network).
/// </summary>
public sealed record ConfigTransport(
    string Name,
    bool UseWebSocket,
    string WebSocketHost,
    int WebSocketPort,
    int Mtu = 1420,
    bool UseIpv6 = false,
    MtuMode MtuMode = MtuMode.Auto,
    bool UseRouter = true,
    bool AllowInbound = false,
    bool InboundNetwork = false)
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
