namespace AmneziaGeo.Decl;

/// <summary>
/// Per-routing-list traffic policy. LocalDns: preferred resolver for non-tunneled names (empty = auto-detect).
/// Exclusions: bypass entries off the tunnel. AllUdp: wrap all outbound UDP through the tunnel.
/// Mode: "split" applies the rules; "full" routes everything while keeping bypass/DNS. Absent row = defaults.
/// </summary>
public sealed record RoutingSettings(
    long ListId,
    string LocalDns,
    string Exclusions,
    bool AllUdp,
    string Mode = "split");
