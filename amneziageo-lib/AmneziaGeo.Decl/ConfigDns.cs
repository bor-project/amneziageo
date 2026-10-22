namespace AmneziaGeo.Decl;

/// <summary>
/// The name servers of a config: preferred DNS servers for local (non-tunneled) name resolution, empty auto-detects
/// system resolvers, and what the names that go through the VPN are asked of.
/// </summary>
public sealed record ConfigDns(
    string Name,
    string Servers,
    VpnDnsMode VpnMode = VpnDnsMode.Default,
    string VpnServers = "");
