using System.Globalization;

using AmneziaGeo.Decl;

namespace AmneziaGeo.Geo;

/// <summary>
/// Routing lists in a bundle: which lists a selection picks, and which held list a block of the bundle is.
/// </summary>
public static class BundleLists
{
    /// <summary>
    /// Name a block without one lands under.
    /// </summary>
    public const string FallbackName = "Список";

    /// <summary>
    /// Picks the lists a selection names, each list once, with the word that picked it. A word that is the number
    /// of a list picks that list; any other word picks every list under that name. Words that pick nothing come
    /// back apart.
    /// </summary>
    public static (IReadOnlyList<(string Key, RoutingList List)> Picked, IReadOnlyList<string> Missing) Pick(
        IReadOnlyList<RoutingList> lists,
        IEnumerable<string> keys)
    {
        ArgumentNullException.ThrowIfNull(lists);
        ArgumentNullException.ThrowIfNull(keys);

        var picked = new List<(string Key, RoutingList List)>();
        var missing = new List<string>();
        var seen = new HashSet<long>();
        foreach (var key in keys)
        {
            var found = Found(lists, key);
            if (found.Count == 0)
            {
                missing.Add(key);
                continue;
            }

            foreach (var list in found)
            {
                if (seen.Add(list.Id))
                {
                    picked.Add((key, list));
                }
            }
        }

        return (picked, missing);
    }

    /// <summary>
    /// The held list a block of the bundle is: the one under the lasting id the block carries. A block without an
    /// id, or with one no list holds, is a list of its own.
    /// </summary>
    public static RoutingList? Held(PortableBundle.RoutingBlock block, IReadOnlyList<RoutingList> lists)
    {
        ArgumentNullException.ThrowIfNull(block);
        ArgumentNullException.ThrowIfNull(lists);

        return string.IsNullOrEmpty(block.Id)
            ? null
            : lists.FirstOrDefault(list => string.Equals(list.Uid, block.Id, StringComparison.Ordinal));
    }

    /// <summary>
    /// The name a block of the bundle lands under.
    /// </summary>
    public static string NameOf(PortableBundle.RoutingBlock block)
    {
        ArgumentNullException.ThrowIfNull(block);

        var name = block.Name.Trim();
        return name.Length > 0 ? name : FallbackName;
    }

    // The lists one word of a selection names.
    private static IReadOnlyList<RoutingList> Found(IReadOnlyList<RoutingList> lists, string key)
    {
        if (long.TryParse(key, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id)
            && lists.FirstOrDefault(list => list.Id == id) is { } numbered)
        {
            return [numbered];
        }

        return [.. lists.Where(list => string.Equals(list.Name, key, StringComparison.Ordinal))];
    }
}
