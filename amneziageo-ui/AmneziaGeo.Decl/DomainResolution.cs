namespace AmneziaGeo.Decl;

/// <summary>
/// A domain with its last resolved IPs. ResolvedAt is the freshest write time, null when unknown.
/// </summary>
public sealed record DomainResolution(string Domain, IReadOnlyList<string> Ips, DateTimeOffset? ResolvedAt = null);
