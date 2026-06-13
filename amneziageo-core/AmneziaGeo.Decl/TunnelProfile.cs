namespace AmneziaGeo.Decl;

public sealed record TunnelProfile(
    string Name,
    string PrivateKey,
    string PublicKey,
    string Endpoint,
    IReadOnlyList<GeoRule> Rules);
