namespace AmneziaGeo.Decl;

/// <summary>
/// Compares transport settings applied on connection.
/// </summary>
public static class ConfigTransportChanges
{
    /// <summary>
    /// Detects changes to session settings outside the proxy catalog.
    /// </summary>
    public static bool RequiresRestart(ConfigTransport? previous, ConfigTransport updated,
        bool inboundApplied = false, bool holdApplied = false) =>
        Session(previous ?? new ConfigTransport(updated.Name, false, 0), inboundApplied, holdApplied)
        != Session(updated, inboundApplied, holdApplied);

    private static ConfigTransport Session(ConfigTransport transport, bool inboundApplied, bool holdApplied) => transport with
    {
        UseWebSocket = false,
        WebSocketHost = string.Empty,
        WebSocketPort = 0,
        Mtu = transport.MtuMode == MtuMode.Custom ? transport.Mtu : 0,
        AllowInbound = !inboundApplied && transport.AllowInbound,
        InboundNetwork = !inboundApplied && transport.AllowInbound && transport.InboundNetwork,
        HoldTraffic = !holdApplied && transport.HoldTraffic,
    };
}
