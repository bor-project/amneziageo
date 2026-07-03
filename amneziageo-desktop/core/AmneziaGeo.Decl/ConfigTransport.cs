namespace AmneziaGeo.Decl;

/// <summary>
/// Per-config transport settings: whether the tunnel's UDP is carried over a WebSocket (wstunnel)
/// to defeat UDP blocking, the host of the WebSocket server (empty = reuse the config's own Endpoint
/// host), the TCP/TLS port it listens on (usually 443), and the tunnel MTU (default 1280, valid
/// 576-1500; written to [Interface] MTU, useful for encapsulated/lossy paths like WSS). The default is
/// the conservative 1280 (IPv6 minimum) so a path with a sub-1500 real MTU does not black-hole the large
/// TLS-handshake packets; a clean 1500 path can raise it to 1420 for a little more throughput (#82/#109).
/// </summary>
public sealed record ConfigTransport(
    string Name,
    bool UseWebSocket,
    string WebSocketHost,
    int WebSocketPort,
    int Mtu = 1280);
