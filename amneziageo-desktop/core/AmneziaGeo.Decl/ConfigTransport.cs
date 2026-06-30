namespace AmneziaGeo.Decl;

/// <summary>
/// Per-config transport settings: whether the tunnel's UDP is carried over a WebSocket (wstunnel)
/// to defeat UDP blocking, the host of the WebSocket server (empty = reuse the config's own Endpoint
/// host), the TCP/TLS port it listens on (usually 443), and the tunnel MTU (default 1420, valid
/// 576-1500; written to [Interface] MTU, useful for encapsulated/lossy paths like WSS).
/// </summary>
public sealed record ConfigTransport(
    string Name,
    bool UseWebSocket,
    string WebSocketHost,
    int WebSocketPort,
    int Mtu = 1420);
