namespace AmneziaGeo.Decl;

/// <summary>
/// Per-routing-list traffic policy. Exclusions: bypass entries off the tunnel. AllUdp: wrap all outbound UDP
/// through the tunnel. Mode: "split" applies the rules; "full" routes everything while keeping bypass.
/// Absent row = defaults. UseIpv6: route IPv6 for this list (off keeps the tunnel v4-only).
/// </summary>
public sealed record RoutingSettings(
    long ListId,
    string Exclusions,
    bool AllUdp,
    string Mode = "split",
    bool UseIpv6 = false);
