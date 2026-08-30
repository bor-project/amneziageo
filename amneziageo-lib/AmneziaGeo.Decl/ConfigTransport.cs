namespace AmneziaGeo.Decl;

/// <summary>
/// Per-config transport: WebSocket (wstunnel) host/port to carry UDP over TCP, tunnel MTU (default 1420, valid 576-1500) with the mode that picks it, and the IPv6 opt-in (off keeps the tunnel v4-only; on only when the server has an IPv6 address).
/// </summary>
public sealed record ConfigTransport(
    string Name,
    bool UseWebSocket,
    string WebSocketHost,
    int WebSocketPort,
    int Mtu = 1420,
    bool UseIpv6 = false,
    MtuMode MtuMode = MtuMode.Auto);
