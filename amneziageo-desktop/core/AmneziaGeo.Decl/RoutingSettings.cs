namespace AmneziaGeo.Decl;

/// <summary>
/// Per-routing-list traffic policy, keyed by the routing list id. Consolidates onto the routing preset
/// (#87) what is today scattered across per-config and global settings:
/// <list type="bullet">
/// <item><see cref="LocalDns"/> — the preferred resolver for NON-tunneled (local) names, comma/space
/// separated; empty auto-detects the system resolvers. Moved here from the per-config <see cref="ConfigDns"/>.
/// The tunnel-side resolver (geo-matched names) stays in the config, so geo resolution is never weakened.</item>
/// <item><see cref="Exclusions"/> — bypass entries kept OFF the tunnel (a domain suffix kept on the local
/// resolver, or an IP/CIDR routed straight out the gateway), combined with the built-in defaults. Moved here
/// from the per-config <see cref="ConfigExclusions"/>.</item>
/// <item><see cref="AllUdp"/> — when set, every outbound UDP datagram's destination is wrapped through the
/// tunnel (catch-all for real-time media whose server IPs never appear in DNS). Moved here from the global
/// tunnel-all-udp setting.</item>
/// <item><see cref="Mode"/> — <c>"split"</c> applies the list's rules; <c>"full"</c> routes everything and
/// still carries these bypass/DNS settings, so a full tunnel keeps its LAN/corp exclusions.</item>
/// </list>
/// The WebSocket transport (and the tunnel-side DNS) stay per-config: they describe how to reach the server,
/// not how to split local traffic. Absent row = defaults (split mode, runtime-default exclusions, no all-UDP).
/// </summary>
public sealed record RoutingSettings(
    long ListId,
    string LocalDns,
    string Exclusions,
    bool AllUdp,
    string Mode = "split");
