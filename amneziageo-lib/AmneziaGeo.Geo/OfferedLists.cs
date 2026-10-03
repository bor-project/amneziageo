using AmneziaGeo.Decl;

namespace AmneziaGeo.Geo;

/// <summary>
/// Puts in the store the geo sources and the routing lists a server of ours hands its clients.
/// </summary>
public static class OfferedLists
{
    private const int MaxSourceName = 32;

    /// <summary>
    /// Adds, after the ones held, the sources the store holds none of at their address. A source the owner removed
    /// comes back, so the lists of the server keep the keys they name. Returns the sources added.
    /// </summary>
    public static async Task<IReadOnlyList<GeoSource>> AddSourcesAsync(IStateStore store, IReadOnlyList<OfferedSource> offered, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(offered);

        if (offered.Count == 0)
        {
            return [];
        }

        var held = (await store.ListGeoSourcesAsync(ct).ConfigureAwait(false)).ToList();
        var added = new List<GeoSource>();
        foreach (var source in offered)
        {
            if (held.Exists(row => string.Equals(row.Url, source.Url, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            var position = held.Count == 0 ? 1 : held.Max(row => row.Position) + 1;
            var name = Fits(source.Name) && !held.Exists(row => string.Equals(row.Name, source.Name, StringComparison.Ordinal))
                ? source.Name
                : GeoSourceNames.Free(held, source.Kind, position);
            var row = new GeoSource(name, source.Kind, source.Url, position);
            await store.SaveGeoSourceAsync(row, ct).ConfigureAwait(false);
            held.Add(row);
            added.Add(row);
        }

        return added;
    }

    /// <summary>
    /// Adds the routing lists the store holds none of under their name and leaves the ones it holds as the owner
    /// keeps them. None of them is put in use: the list in force is the owner's choice, and a store left without a
    /// list sends everything through the tunnel, which a list picked behind the owner's back would narrow. Returns
    /// how many lists were added.
    /// </summary>
    public static async Task<int> AddPresetsAsync(IStateStore store, GeoConfigurator geo, IReadOnlyList<OfferedPreset> offered, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(geo);
        ArgumentNullException.ThrowIfNull(offered);

        if (offered.Count == 0)
        {
            return 0;
        }

        var held = await store.ListRoutingListsAsync(ct).ConfigureAwait(false);
        var names = new HashSet<string>(held.Select(list => list.Name), StringComparer.Ordinal);
        var added = 0;
        foreach (var preset in offered)
        {
            if (!names.Add(preset.Name))
            {
                continue;
            }

            await AddListAsync(store, geo, preset.Name, preset.Rules, preset.AllUdp, preset.Full, ct).ConfigureAwait(false);
            added++;
        }

        return added;
    }

    /// <summary>
    /// Adds a routing list with its rules and the way the rest of the traffic goes. Returns its number.
    /// </summary>
    public static async Task<long> AddListAsync(
        IStateStore store,
        GeoConfigurator geo,
        string name,
        IReadOnlyList<string> rules,
        bool allUdp,
        bool full,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(geo);

        var id = await geo.ApplyToRoutingListAsync(0, name, rules, ct).ConfigureAwait(false);
        if (allUdp || full)
        {
            await store.SetRoutingSettingsAsync(new RoutingSettings(id, string.Empty, allUdp, full ? "full" : "split", full), ct).ConfigureAwait(false);
        }

        return id;
    }

    // A name the server gives a source is kept when it reads as one: lower-case letters, digits, dots and dashes.
    private static bool Fits(string name) =>
        name.Length is > 0 and <= MaxSourceName
        && char.IsAsciiLetterLower(name[0])
        && name.All(letter => char.IsAsciiLetterLower(letter) || char.IsAsciiDigit(letter) || letter is '.' or '-');
}
