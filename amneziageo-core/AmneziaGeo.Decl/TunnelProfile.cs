namespace AmneziaGeo.Decl;

/// <summary>
/// A named AmneziaWG tunnel profile with its split-tunnel rules.
/// </summary>
public sealed record TunnelProfile(
    string Name,
    string PrivateKey,
    string PublicKey,
    string Endpoint,
    IReadOnlyList<GeoRule> Rules);
