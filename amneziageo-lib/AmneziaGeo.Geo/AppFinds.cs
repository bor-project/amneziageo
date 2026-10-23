using System.Text.Json;
using AmneziaGeo.Decl;
using AmneziaGeo.Ipc;

namespace AmneziaGeo.Geo;

/// <summary>
/// What one search of the programs of the device gave the names of the routing lists.
/// </summary>
/// <param name="Changed">The names whose programs are not the ones found before.</param>
/// <param name="Missing">The names no program of the device stands for.</param>
public sealed record AppSearch(IReadOnlyList<string> Changed, IReadOnlyList<string> Missing);

/// <summary>
/// Keeps in the settings of the store what the search of the device found for each app:&lt;name&gt; of the routing
/// lists, so a connect routes by the programs found last while the search runs again.
/// </summary>
public static class AppFinds
{
    /// <summary>
    /// The setting the finds are kept under.
    /// </summary>
    public const string Key = "app-finds";

    /// <summary>
    /// Returns what was found for each name searched, by the name in any case; a name no program stands for stands
    /// for nothing.
    /// </summary>
    public static async Task<IReadOnlyDictionary<string, IReadOnlyList<string>>> ReadAsync(IStateStore store, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(store);

        return Read(await store.GetSettingAsync(Key, ct).ConfigureAwait(false));
    }

    /// <summary>
    /// Returns the names the app rules carry.
    /// </summary>
    public static IReadOnlyList<string> NamesOf(IEnumerable<GeoRule> rules)
    {
        ArgumentNullException.ThrowIfNull(rules);

        return [.. rules.Where(rule => rule.Kind == GeoRuleKind.App && AppNames.IsName(rule.Value))
            .Select(rule => rule.Value.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>
    /// Returns the values a set of app rule values routes by, a name standing for the programs found for it.
    /// </summary>
    public static async Task<IReadOnlyList<string>> ExpandAsync(IStateStore store, IReadOnlyList<string> values, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(values);

        return values.Any(AppNames.IsName) ? AppNames.Expand(values, await ReadAsync(store, ct).ConfigureAwait(false)) : values;
    }

    /// <summary>
    /// Returns the list the way the device routes by it: each app rule that names a program stands for the programs
    /// found for it, for none when none was.
    /// </summary>
    public static async Task<RoutingList> ExpandListAsync(IStateStore store, RoutingList list, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(list);

        return NamesOf(list.Rules).Count == 0 ? list : Expand(list, await ReadAsync(store, ct).ConfigureAwait(false));
    }

    /// <summary>
    /// Returns the list with each app rule that names a program standing for the programs found for it.
    /// </summary>
    public static RoutingList Expand(RoutingList list, IReadOnlyDictionary<string, IReadOnlyList<string>> finds)
    {
        ArgumentNullException.ThrowIfNull(list);
        ArgumentNullException.ThrowIfNull(finds);

        return list with
        {
            Rules = ExpandRules(list.Rules, finds),
            Apps = AppNames.Expand(list.Apps, finds),
            DirectApps = AppNames.Expand(list.DirectApps, finds),
        };
    }

    /// <summary>
    /// Returns the stamp of a list with each app rule that names a program standing for the programs found for it.
    /// </summary>
    public static RoutingListStamp? ExpandStamp(RoutingListStamp? stamp, IReadOnlyDictionary<string, IReadOnlyList<string>> finds) =>
        stamp is null || NamesOf(stamp.Rules).Count == 0 ? stamp : stamp with { Rules = ExpandRules(stamp.Rules, finds) };

    /// <summary>
    /// Returns the rules with each app rule that names a program standing for the programs found for it, in its
    /// place and with its role.
    /// </summary>
    public static IReadOnlyList<GeoRule> ExpandRules(IReadOnlyList<GeoRule> rules, IReadOnlyDictionary<string, IReadOnlyList<string>> finds)
    {
        ArgumentNullException.ThrowIfNull(rules);
        ArgumentNullException.ThrowIfNull(finds);

        var expanded = new List<GeoRule>();
        foreach (var rule in rules)
        {
            if (rule.Kind != GeoRuleKind.App || !AppNames.IsName(rule.Value))
            {
                expanded.Add(rule);
            }
            else if (finds.TryGetValue(rule.Value.Trim(), out var values))
            {
                expanded.AddRange(values.Select(value => rule with { Value = value }));
            }
        }

        return expanded;
    }

    /// <summary>
    /// Returns the target a check asks about: app:&lt;name&gt; stands for the first program found for it.
    /// </summary>
    public static async Task<string> TargetAsync(IStateStore store, string target, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(target);

        var text = target.Trim();
        if (!text.StartsWith("app:", StringComparison.OrdinalIgnoreCase) || !AppNames.IsName(text[4..]))
        {
            return target;
        }

        return (await ReadAsync(store, ct).ConfigureAwait(false)).TryGetValue(text[4..].Trim(), out var values) && values.Count > 0
            ? "app:" + values[0]
            : target;
    }

    /// <summary>
    /// Returns the names the app rules of every routing list of the store carry.
    /// </summary>
    public static async Task<IReadOnlyList<string>> ListNamesAsync(IStateStore store, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(store);

        var names = new List<string>();
        foreach (var summary in await store.ListRoutingListSummariesAsync(ct).ConfigureAwait(false))
        {
            if (await store.GetRoutingListStampAsync(summary.Id, ct).ConfigureAwait(false) is { } stamp)
            {
                names.AddRange(NamesOf(stamp.Rules));
            }
        }

        return [.. names.Distinct(StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>
    /// Looks the names of every routing list up among the programs of the device, keeps what each stands for and
    /// notes each name no program stands for; the programs are listed only when a list names one. Returns what the
    /// search found, none when no list names a program.
    /// </summary>
    public static async Task<AppSearch?> SearchListsAsync(IStateStore store, Func<IReadOnlyList<NamedApp>> programs, Action<string>? note, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(programs);

        var names = await ListNamesAsync(store, ct).ConfigureAwait(false);
        if (names.Count == 0)
        {
            return null;
        }

        var search = await SearchAsync(store, names, programs(), ct).ConfigureAwait(false);
        foreach (var name in search.Missing)
        {
            note?.Invoke($"app:{name}: no program of this device goes by that name, so its rule is skipped");
        }

        return search;
    }

    /// <summary>
    /// Looks the names up among the programs of the device and keeps what each stands for.
    /// </summary>
    public static async Task<AppSearch> SearchAsync(IStateStore store, IReadOnlyList<string> names, IReadOnlyList<NamedApp> programs, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(names);
        ArgumentNullException.ThrowIfNull(programs);

        var held = new Dictionary<string, IReadOnlyList<string>>(await ReadAsync(store, ct).ConfigureAwait(false), StringComparer.OrdinalIgnoreCase);
        var changed = new List<string>();
        var missing = new List<string>();
        var written = false;
        foreach (var name in names.Select(one => one.Trim()).Where(one => one.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var found = AppNames.Pick(name, programs);
            if (found.Count == 0)
            {
                missing.Add(name);
            }

            var known = held.TryGetValue(name, out var before);
            if (known && before!.SequenceEqual(found, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!(before ?? []).SequenceEqual(found, StringComparer.OrdinalIgnoreCase))
            {
                changed.Add(name);
            }

            held[name] = found;
            written = true;
        }

        if (written)
        {
            await store.SetSettingAsync(Key, JsonSerializer.Serialize(held), ct).ConfigureAwait(false);
        }

        return new AppSearch(changed, missing);
    }

    private static Dictionary<string, IReadOnlyList<string>> Read(string? raw)
    {
        var finds = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return finds;
        }

        try
        {
            foreach (var (name, values) in JsonSerializer.Deserialize<Dictionary<string, string[]>>(raw) ?? [])
            {
                finds[name] = values ?? [];
            }
        }
        catch (JsonException)
        {
            // A kept value no build of this application wrote: the names are searched afresh.
        }

        return finds;
    }
}
