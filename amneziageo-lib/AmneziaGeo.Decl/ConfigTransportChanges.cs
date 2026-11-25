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
        bool inboundApplied = false, bool holdApplied = false, bool? ipv6Available = null) =>
        Session(previous ?? new ConfigTransport(updated.Name, false, 0) { Ipv6Mode = Ipv6Mode.Auto }, inboundApplied, holdApplied, ipv6Available)
        != Session(updated, inboundApplied, holdApplied, ipv6Available);

    private static ConfigTransport Session(ConfigTransport transport, bool inboundApplied, bool holdApplied, bool? ipv6Available) => transport with
    {
        Ipv6Mode = (Ipv6Modes.Of(transport), ipv6Available) switch
        {
            (Ipv6Mode.Auto, true) => Ipv6Mode.On,
            (Ipv6Mode.Auto, false) => Ipv6Mode.Off,
            (var mode, _) => mode,
        },
        UseIpv6 = false,
        UseWebSocket = false,
        WebSocketHost = string.Empty,
        WebSocketPort = 0,
        ServicesPort = 0,
        Mtu = transport.MtuMode == MtuMode.Custom ? transport.Mtu : 0,
        AllowInbound = !inboundApplied && transport.AllowInbound,
        InboundNetwork = !inboundApplied && transport.AllowInbound && transport.InboundNetwork,
        HoldTraffic = !holdApplied && transport.HoldTraffic,
    };
}
