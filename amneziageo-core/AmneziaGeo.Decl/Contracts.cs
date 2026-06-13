namespace AmneziaGeo.Decl;

public enum GeoRuleKind
{
    GeoSite,
    GeoIp,
    Domain,
    Cidr,
}

public sealed record GeoRule(GeoRuleKind Kind, string Value);

public sealed record TunnelProfile(
    string Name,
    string PrivateKey,
    string PublicKey,
    string Endpoint,
    IReadOnlyList<GeoRule> Rules);

public interface IStateStore
{
    Task InitializeAsync(CancellationToken ct = default);

    Task<TunnelProfile?> GetProfileAsync(string name, CancellationToken ct = default);

    Task SaveProfileAsync(TunnelProfile profile, CancellationToken ct = default);

    Task<IReadOnlyList<string>> ListProfileNamesAsync(CancellationToken ct = default);
}
