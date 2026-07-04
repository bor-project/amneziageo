namespace AmneziaGeo.Decl;

/// <summary>
/// Per-config transport: WebSocket (wstunnel) host/port to carry UDP over TCP, and tunnel MTU (default 1380, valid 576-1500).
/// </summary>
public sealed record ConfigTransport(
    string Name,
    bool UseWebSocket,
    string WebSocketHost,
    int WebSocketPort,
    int Mtu = 1380);
