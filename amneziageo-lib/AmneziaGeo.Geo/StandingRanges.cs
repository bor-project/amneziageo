using AmneziaGeo.Decl;

namespace AmneziaGeo.Geo;

/// <summary>
/// The ranges a tunnel keeps on its adapter for the list in force, beside its own infrastructure.
/// </summary>
public sealed record StandingRanges(IReadOnlyList<string> Return, IReadOnlyList<string> Named, IReadOnlyList<string> Networks)
{
    /// <summary>
    /// No ranges at all.
    /// </summary>
    public static StandingRanges None { get; } = new([], [], []);

    /// <summary>
    /// The three sets together, each range once.
    /// </summary>
    public IReadOnlyList<string> All => [.. Return.Concat(Named).Concat(Networks).Distinct(StringComparer.Ordinal)];

    /// <summary>
    /// Works the ranges out from the share of the list the tunnel carries, the rules of the list and the networks
    /// the configuration reaches; a range of the infrastructure is left out.
    /// </summary>
    public static StandingRanges Of(IReadOnlyList<string> carried, IReadOnlyList<GeoRule> rules, bool split, bool inbound, IReadOnlyList<string> ownNetworks, IReadOnlyCollection<string> infrastructure)
    {
        var taken = new HashSet<string>(infrastructure, StringComparer.Ordinal);
        var returned = inbound ? Take(carried.Where(PrivateNetworks.IsNetwork), taken) : [];
        if (!split)
        {
            return new StandingRanges(returned, [], []);
        }

        var named = Take(GeoMaterializer.NamedRanges(rules, RouteRole.Proxy, carried), taken);
        var listNamed = GeoMaterializer.NamedRanges(rules);
        var networks = Take(ownNetworks.Where(network => !PrivateNetworks.Overlaps(network, listNamed)), taken);
        return new StandingRanges(returned, named, networks);
    }

    /// <summary>
    /// The ranges of <paramref name="fresh"/> missing from <paramref name="standing"/>, and the ranges of
    /// <paramref name="standing"/> gone from <paramref name="fresh"/>.
    /// </summary>
    public static (IReadOnlyList<string> Added, IReadOnlyList<string> Removed) Diff(IReadOnlyCollection<string> standing, IReadOnlyCollection<string> fresh)
    {
        var before = new HashSet<string>(standing, StringComparer.Ordinal);
        var after = new HashSet<string>(fresh, StringComparer.Ordinal);
        return ([.. fresh.Distinct(StringComparer.Ordinal).Where(range => !before.Contains(range))],
            [.. standing.Distinct(StringComparer.Ordinal).Where(range => !after.Contains(range))]);
    }

    // The ranges not taken yet, each marked taken.
    private static IReadOnlyList<string> Take(IEnumerable<string> ranges, HashSet<string> taken)
    {
        var fresh = new List<string>();
        foreach (var range in ranges)
        {
            if (taken.Add(range))
            {
                fresh.Add(range);
            }
        }

        return fresh;
    }
}
