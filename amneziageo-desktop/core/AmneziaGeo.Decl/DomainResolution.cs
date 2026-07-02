namespace AmneziaGeo.Decl;

/// <summary>
/// A domain together with its last resolved IP addresses. <paramref name="ResolvedAt"/> is the freshest
/// time any of those IPs was written (populated on read, null when unknown), so a caller can tell how old
/// the cached resolution is and re-resolve it once past its validity window (#83).
/// </summary>
public sealed record DomainResolution(string Domain, IReadOnlyList<string> Ips, DateTimeOffset? ResolvedAt = null);
