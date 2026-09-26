using AmneziaGeo.Decl;

namespace AmneziaGeo.Geo;

/// <summary>
/// Names a geo source so that it takes no name another source already holds.
/// </summary>
public static class GeoSourceNames
{
    /// <summary>
    /// Returns the first name of the kind and a number, from the place on, that no held source carries.
    /// </summary>
    public static string Free(IEnumerable<GeoSource> held, string kind, int position)
    {
        ArgumentNullException.ThrowIfNull(held);

        var taken = new HashSet<string>(held.Select(row => row.Name), StringComparer.Ordinal);
        var number = position;
        while (taken.Contains($"{kind}-{number}"))
        {
            number++;
        }

        return $"{kind}-{number}";
    }
}
