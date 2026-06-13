namespace AmneziaGeo.Decl;

/// <summary>
/// A domain together with its last resolved IP addresses.
/// </summary>
public sealed record DomainResolution(string Domain, IReadOnlyList<string> Ips);
