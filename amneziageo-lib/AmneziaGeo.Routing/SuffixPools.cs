using System.Net;

namespace AmneziaGeo.Routing;

/// <summary>
/// The resolvers the names under a DNS suffix are answered by: the ones of the adapter or the VPN the suffix came with.
/// </summary>
public static class SuffixPools
{
    /// <summary>
    /// The pools with their suffixes trimmed and lower-cased, the longest suffix first, without empty ones.
    /// </summary>
    public static IReadOnlyList<(string Suffix, IReadOnlyList<IPAddress> Pool)> Normalize(IEnumerable<(string Suffix, IReadOnlyList<IPAddress> Pool)> pools)
    {
        return
        [
            .. pools
                .Select(pool => (Suffix: pool.Suffix.Trim().Trim('.').ToLowerInvariant(), pool.Pool))
                .Where(pool => pool.Suffix.Length > 0 && pool.Pool.Count > 0)
                .OrderByDescending(pool => pool.Suffix.Length),
        ];
    }

    /// <summary>
    /// Whether two normalized sets name the same suffixes with the same resolvers.
    /// </summary>
    public static bool Same(IReadOnlyList<(string Suffix, IReadOnlyList<IPAddress> Pool)> first, IReadOnlyList<(string Suffix, IReadOnlyList<IPAddress> Pool)> second)
    {
        return first.Count == second.Count
            && first.Zip(second).All(pair => pair.First.Suffix == pair.Second.Suffix && pair.First.Pool.SequenceEqual(pair.Second.Pool));
    }

    /// <summary>
    /// The pool of the longest suffix the name falls under; null where none does.
    /// </summary>
    public static IReadOnlyList<IPAddress>? PoolOf(IReadOnlyList<(string Suffix, IReadOnlyList<IPAddress> Pool)> pools, string name)
    {
        var trimmed = name.TrimEnd('.').ToLowerInvariant();
        foreach (var (suffix, pool) in pools)
        {
            if (trimmed == suffix || trimmed.EndsWith("." + suffix, StringComparison.Ordinal))
            {
                return pool;
            }
        }

        return null;
    }
}
