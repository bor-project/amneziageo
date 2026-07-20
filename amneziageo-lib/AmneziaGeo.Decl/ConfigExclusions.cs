namespace AmneziaGeo.Decl;

/// <summary>
/// Bypass entries kept off the tunnel: domain suffixes stay on the local resolver, IP/CIDR routes out the gateway.
/// </summary>
public sealed record ConfigExclusions(
    string Name,
    string Exclusions);
