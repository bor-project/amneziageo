using System.Globalization;
using AmneziaGeo.Decl;

namespace AmneziaGeo.Geo;

/// <summary>
/// What taking the geo sources of a server did.
/// </summary>
/// <param name="Added">The sources the store held none of.</param>
/// <param name="Moved">The sources that took the address the server names.</param>
public sealed record TakenSources(IReadOnlyList<GeoSource> Added, IReadOnlyList<GeoSource> Moved)
{
    /// <summary>
    /// Nothing taken.
    /// </summary>
    public static TakenSources None { get; } = new([], []);

    /// <summary>
    /// Whether anything was taken.
    /// </summary>
    public bool Any => Added.Count > 0 || Moved.Count > 0;

    /// <summary>
    /// The sources whose files are to be fetched.
    /// </summary>
    public IReadOnlyList<GeoSource> Fetch => [.. Added, .. Moved];
}

/// <summary>
/// What taking the routing lists of a server did.
/// </summary>
/// <param name="Added">How many lists were added.</param>
/// <param name="Bound">How many lists held under the name were tied to the list of the server.</param>
/// <param name="Waiting">How many lists got a newer version that waits for the owner.</param>
/// <param name="InUse">The list put in use, null when none was.</param>
public sealed record TakenPresets(int Added, int Bound, int Waiting, long? InUse)
{
    /// <summary>
    /// Nothing taken.
    /// </summary>
    public static TakenPresets None { get; } = new(0, 0, 0, null);

    /// <summary>
    /// Whether anything was taken.
    /// </summary>
    public bool Any => Added > 0 || Bound > 0 || Waiting > 0 || InUse is not null;
}

/// <summary>
/// The newer version of a routing list that waits for the owner, the way the commands of the agent take it.
/// </summary>
/// <param name="ListId">The routing list.</param>
/// <param name="Name">The name the list takes: the one of the version, unless another list carries it.</param>
/// <param name="Rules">The rules of the version, each led by what it does with the traffic.</param>
/// <param name="Exclusions">The exclusions the list keeps.</param>
/// <param name="AllUdp">Whether every UDP packet goes through the tunnel.</param>
/// <param name="Full">Whether everything goes through the tunnel but what goes directly.</param>
public sealed record WaitingList(long ListId, string Name, IReadOnlyList<string> Rules, string Exclusions, bool AllUdp, bool Full)
{
    /// <summary>
    /// The arguments of the command that saves the list.
    /// </summary>
    public IReadOnlyList<string> SaveArgs => [ListId.ToString(CultureInfo.InvariantCulture), Name, .. Rules];

    /// <summary>
    /// The arguments of the command that sets the traffic settings of the list.
    /// </summary>
    public IReadOnlyList<string> SettingsArgs =>
        [ListId.ToString(CultureInfo.InvariantCulture), Exclusions, AllUdp ? "on" : "off", Full ? "full" : "split", Full ? "on" : "off"];
}

/// <summary>
/// Puts in the store the geo sources and the routing lists a server of ours hands its clients.
/// </summary>
public static class OfferedLists
{
    private const int MaxSourceName = 32;

    private const int MaxTakenAddresses = 128;

    /// <summary>
    /// Adds, after the ones held, the sources the store holds none of at their address, and moves a source held under
    /// the name of the server to the address the server names, once per address. A source the owner removed comes
    /// back, so the lists of the server keep the keys they name.
    /// </summary>
    public static async Task<TakenSources> TakeSourcesAsync(IStateStore store, IReadOnlyList<OfferedSource> offered, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(offered);

        if (offered.Count == 0)
        {
            return TakenSources.None;
        }

        var held = (await store.ListGeoSourcesAsync(ct).ConfigureAwait(false)).ToList();
        var taken = Lines(await store.GetSettingAsync(StateKeys.OfferedSourceAddresses, ct).ConfigureAwait(false));
        var known = taken.Count;
        var added = new List<GeoSource>();
        var moved = new List<GeoSource>();
        foreach (var source in offered)
        {
            if (held.Exists(row => string.Equals(row.Url, source.Url, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            var same = Fits(source.Name) ? held.FindIndex(row => SameSource(row, source)) : -1;
            if (same >= 0)
            {
                var line = $"{source.Name}\t{source.Url}";
                if (!taken.Contains(line, StringComparer.OrdinalIgnoreCase))
                {
                    taken.Add(line);
                    var row = held[same] with { Url = source.Url };
                    await store.SaveGeoSourceAsync(row, ct).ConfigureAwait(false);
                    held[same] = row;
                    moved.Add(row);
                }

                continue;
            }

            var position = held.Count == 0 ? 1 : held.Max(row => row.Position) + 1;
            var name = Fits(source.Name) && !held.Exists(row => string.Equals(row.Name, source.Name, StringComparison.Ordinal))
                ? source.Name
                : GeoSourceNames.Free(held, source.Kind, position);
            var fresh = new GeoSource(name, source.Kind, source.Url, position);
            await store.SaveGeoSourceAsync(fresh, ct).ConfigureAwait(false);
            held.Add(fresh);
            added.Add(fresh);
        }

        if (taken.Count != known)
        {
            await store.SetSettingAsync(StateKeys.OfferedSourceAddresses, string.Join('\n', taken.TakeLast(MaxTakenAddresses)), ct).ConfigureAwait(false);
        }

        return new TakenSources(added, moved);
    }

    /// <summary>
    /// Takes the routing lists a server hands out. A list the store knows by the identifier of the server keeps what
    /// it holds, and a newer version waits for the owner; a list the store holds none of is added, and put in use
    /// when the server marks it so and no list is in use; a list held under the name is tied to the list of the
    /// server. A server that names no identifiers has its lists added by name.
    /// </summary>
    public static async Task<TakenPresets> TakePresetsAsync(
        IStateStore store,
        GeoConfigurator geo,
        string config,
        IReadOnlyList<OfferedPreset> offered,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(geo);
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(offered);

        if (offered.Count == 0)
        {
            return TakenPresets.None;
        }

        var lists = (await store.ListRoutingListsAsync(ct).ConfigureAwait(false)).ToList();
        var origins = (await store.ListRoutingListOriginsAsync(ct).ConfigureAwait(false)).ToList();
        var names = new HashSet<string>(lists.Select(list => list.Name), StringComparer.Ordinal);
        var ids = new HashSet<long>(lists.Select(list => list.Id));
        var added = 0;
        var bound = 0;
        var waiting = 0;
        var inUse = default(long?);
        foreach (var preset in offered)
        {
            if (preset.Id.Length == 0)
            {
                if (names.Add(preset.Name))
                {
                    ids.Add(await AddListAsync(store, geo, preset.Name, preset.Rules, preset.AllUdp, preset.Full, ct).ConfigureAwait(false));
                    added++;
                }

                continue;
            }

            var version = preset with { Source = preset.Source.Length > 0 ? preset.Source : config };
            var when = preset.Updated ?? DateTimeOffset.MinValue;
            var at = origins.FindIndex(one => string.Equals(one.PresetId, preset.Id, StringComparison.Ordinal));
            if (at >= 0)
            {
                var origin = origins[at];
                if (when > Newest(origin) && lists.Find(list => list.Id == origin.ListId) is { } held)
                {
                    var same = await SameAsync(store, held, version, ct).ConfigureAwait(false);
                    origins[at] = same ? origin with { Updated = when, Pending = null } : origin with { Pending = version };
                    await store.SetRoutingListOriginAsync(origins[at], ct).ConfigureAwait(false);
                    waiting += same ? 0 : 1;
                }

                continue;
            }

            if (lists.Find(list => string.Equals(list.Name, preset.Name, StringComparison.Ordinal)) is { } named
                && !origins.Exists(one => one.ListId == named.Id))
            {
                var same = await SameAsync(store, named, version, ct).ConfigureAwait(false);
                var tied = same
                    ? new RoutingListOrigin(named.Id, preset.Id, version.Source, when)
                    : new RoutingListOrigin(named.Id, preset.Id, version.Source, DateTimeOffset.MinValue, version);
                await store.SetRoutingListOriginAsync(tied, ct).ConfigureAwait(false);
                origins.Add(tied);
                bound++;
                waiting += same ? 0 : 1;

                continue;
            }

            var name = FreeName(names, preset.Name);
            names.Add(name);
            var id = await AddListAsync(store, geo, name, preset.Rules, preset.AllUdp, preset.Full, ct).ConfigureAwait(false);
            var fresh = new RoutingListOrigin(id, preset.Id, version.Source, when);
            await store.SetRoutingListOriginAsync(fresh, ct).ConfigureAwait(false);
            origins.Add(fresh);
            ids.Add(id);
            added++;
            if (preset.Default && !await InUseAsync(store, ids, ct).ConfigureAwait(false))
            {
                await store.SetSelectedRoutingListAsync(id, ct).ConfigureAwait(false);
                inUse = id;
            }
        }

        return new TakenPresets(added, bound, waiting, inUse);
    }

    /// <summary>
    /// Returns the newer version of a routing list that waits for the owner, null when the list holds the newest.
    /// </summary>
    public static async Task<WaitingList?> WaitingAsync(IStateStore store, long listId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(store);

        var origin = await store.GetRoutingListOriginAsync(listId, ct).ConfigureAwait(false);
        var lists = await store.ListRoutingListsAsync(ct).ConfigureAwait(false);
        if (origin?.Pending is not { } pending || lists.FirstOrDefault(list => list.Id == listId) is not { } held)
        {
            return null;
        }

        var taken = lists.Any(list => list.Id != listId && string.Equals(list.Name, pending.Name, StringComparison.Ordinal));
        var settings = await store.GetRoutingSettingsAsync(listId, ct).ConfigureAwait(false);

        return new WaitingList(listId, taken ? held.Name : pending.Name, pending.Rules, settings?.Exclusions ?? string.Empty, pending.AllUdp, pending.Full);
    }

    /// <summary>
    /// Records that a routing list took the version that waited for it.
    /// </summary>
    public static async Task SettleAsync(IStateStore store, long listId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(store);

        var origin = await store.GetRoutingListOriginAsync(listId, ct).ConfigureAwait(false);
        if (origin?.Pending is not { } pending)
        {
            return;
        }

        await store.SetRoutingListOriginAsync(
            origin with
            {
                Source = pending.Source.Length > 0 ? pending.Source : origin.Source,
                Updated = pending.Updated ?? origin.Updated,
                Pending = null,
            },
            ct).ConfigureAwait(false);
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

    private static bool SameSource(GeoSource row, OfferedSource source) =>
        string.Equals(row.Name, source.Name, StringComparison.Ordinal)
        && string.Equals(row.Kind, source.Kind, StringComparison.OrdinalIgnoreCase);

    private static List<string> Lines(string? text) =>
        [.. (text ?? string.Empty).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];

    // The time of the newest version of the server the list knows of: the one it holds or the one that waits.
    private static DateTimeOffset Newest(RoutingListOrigin origin) =>
        origin.Pending?.Updated is { } waiting && waiting > origin.Updated ? waiting : origin.Updated;

    private static async Task<bool> InUseAsync(IStateStore store, HashSet<long> ids, CancellationToken ct) =>
        await store.GetSelectedRoutingListAsync(ct).ConfigureAwait(false) is { } selected && ids.Contains(selected);

    // Tells whether a list holds what a version of the server hands out.
    private static async Task<bool> SameAsync(IStateStore store, RoutingList list, OfferedPreset version, CancellationToken ct)
    {
        var settings = await store.GetRoutingSettingsAsync(list.Id, ct).ConfigureAwait(false);
        var offered = version.Rules.Select(GeoConfigurator.ParseRoleRule).OfType<GeoRule>().Select(GeoConfigurator.FormatWithRole);

        return string.Equals(list.Name, version.Name, StringComparison.Ordinal)
            && (settings?.AllUdp ?? false) == version.AllUdp
            && (settings?.UseGlobalProxy ?? false) == version.Full
            && list.Rules.Select(GeoConfigurator.FormatWithRole).SequenceEqual(offered, StringComparer.Ordinal);
    }

    private static string FreeName(HashSet<string> names, string name)
    {
        if (!names.Contains(name))
        {
            return name;
        }

        var number = 2;
        while (names.Contains($"{name} ({number})"))
        {
            number++;
        }

        return $"{name} ({number})";
    }
}
