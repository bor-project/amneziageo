namespace AmneziaGeo.Decl;

/// <summary>
/// Per-config transport: WebSocket (the tunnel carried inside a websocket to the front the server offers), tunnel MTU (default 1420, valid 576-1500) with the mode that picks it, the IPv6 opt-in (off keeps the tunnel v4-only; on only when the server has an IPv6 address), the router (on decides every connection on its own; off leaves every verdict to the route table), inbound access (off refuses everything arriving from the tunnel; on accepts it from the server alone, or from the whole tunnel network), and routing (off keeps the config off the routing list).
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
    bool UseRouting = true);
