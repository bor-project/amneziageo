namespace AmneziaGeo.Decl;

/// <summary>
/// Per-routing-list traffic policy. Exclusions: bypass entries off the tunnel. AllUdp: wrap all outbound UDP
/// through the tunnel. Mode: "split" applies the rules; "full" routes everything while keeping bypass.
/// Absent row = defaults. UseGlobalProxy: route everything through the tunnel except the Direct bucket (full);
/// off tunnels only the Proxy bucket (split). IPv6 is per-config now (ConfigTransport.UseIpv6), not per-list.
/// </summary>
public sealed record RoutingSettings(
    long ListId,
    string Exclusions,
    bool AllUdp,
    string Mode = "split",
    bool UseGlobalProxy = false);
