namespace AmneziaGeo.Decl;

public interface IStateStore
{
    Task InitializeAsync(CancellationToken ct = default);

    Task<TunnelProfile?> GetProfileAsync(string name, CancellationToken ct = default);

    Task SaveProfileAsync(TunnelProfile profile, CancellationToken ct = default);

    Task<IReadOnlyList<string>> ListProfileNamesAsync(CancellationToken ct = default);
}
