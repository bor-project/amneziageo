namespace AmneziaGeo.Decl;

/// <summary>
/// Per-config transport settings: whether the tunnel's UDP is carried over a WebSocket (wstunnel)
/// to defeat UDP blocking, the host of the WebSocket server (empty = reuse the config's own Endpoint
/// host), and the TCP/TLS port it listens on (usually 443).
/// </summary>
public sealed record ConfigTransport(
    string Name,
    bool UseWebSocket,
    string WebSocketHost,
    int WebSocketPort);
