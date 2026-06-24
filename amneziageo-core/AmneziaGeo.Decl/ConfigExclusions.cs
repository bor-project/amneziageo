namespace AmneziaGeo.Decl;

/// <summary>
/// A config's bypass exclusions: entries kept OFF the tunnel — a domain suffix (e.g. <c>corp.local</c>)
/// kept on the LOCAL resolver, or an IP/CIDR (e.g. <c>192.168.50.0/24</c>) routed straight out the
/// physical gateway. One entry per line (or comma/semicolon separated); always combined with the built-in
/// defaults (loopback, RFC1918 LAN, common local suffixes). <see cref="AutoExcludeLan"/> additionally
/// auto-detects the machine's connected local subnets each connect and keeps them direct. Stored per config
/// (a profile binds one config), so each profile carries its own — moved here from the former global app
/// settings.
/// </summary>
public sealed record ConfigExclusions(
    string Name,
    string Exclusions,
    bool AutoExcludeLan);
