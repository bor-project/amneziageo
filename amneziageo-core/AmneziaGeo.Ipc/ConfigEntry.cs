namespace AmneziaGeo.Ipc;

/// <summary>
/// A configuration and its current connection status, as seen by the agent.
/// </summary>
public sealed record ConfigEntry(
    string Name,
    string Endpoint,
    bool GeoSplit,
    string Status,
    IReadOnlyList<string> Rules,
    bool WebSocket = false,
    string WebSocketHost = "",
    int WebSocketPort = 443,
    string Dns = "");
