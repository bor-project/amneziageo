using System.Collections.Concurrent;
using System.Net;

namespace AmneziaGeo.Windows.App;

/// <summary>
/// Classifies destination addresses against the Direct and Block rule sets, memoizing the answer per address so a
/// repeat destination costs a dictionary hit instead of a search.
/// </summary>
internal sealed class VerdictResolver
{
    // Bounds the memo; clearing it costs one binary search per address to refill.
    private const int CacheCap = 65536;

    private readonly GeoIpRanges _direct;
    private readonly GeoIpRanges _block;
    private readonly ConcurrentDictionary<uint, RouteVerdict> _cache = new();
    private int _cached;

    /// <summary>
    /// ctor
    /// </summary>
    public VerdictResolver(GeoIpRanges direct, GeoIpRanges block)
    {
        _direct = direct;
        _block = block;
    }

    /// <summary>
    /// Builds a resolver over Direct and Block CIDR sets.
    /// </summary>
    public static VerdictResolver Build(IReadOnlyList<string> direct, IReadOnlyList<string> block)
    {
        return new VerdictResolver(GeoIpRanges.Build(direct), GeoIpRanges.Build(block));
    }

    /// <summary>
    /// Whether any rule can match.
    /// </summary>
    public bool HasRules => _direct.Count > 0 || _block.Count > 0;

    /// <summary>
    /// Merged range counts, for logging.
    /// </summary>
    public (int Direct, int Block) RangeCounts => (_direct.Count, _block.Count);

    /// <summary>
    /// Returns the verdict for a host-order address, from the memo when already seen.
    /// </summary>
    public RouteVerdict Classify(uint address)
    {
        if (_cache.TryGetValue(address, out var cached))
        {
            return cached;
        }

        var verdict = Evaluate(address);
        if (_cache.TryAdd(address, verdict) && Interlocked.Increment(ref _cached) >= CacheCap)
        {
            _cache.Clear();
            Interlocked.Exchange(ref _cached, 0);
        }

        return verdict;
    }

    /// <summary>
    /// Returns the verdict for an address; anything but IPv4 is Proxy.
    /// </summary>
    public RouteVerdict Classify(IPAddress address)
    {
        return GeoIpRanges.TryToNumeric(address, out var value) ? Classify(value) : RouteVerdict.Proxy;
    }

    /// <summary>
    /// Drops the memo after a rule edit.
    /// </summary>
    public void Invalidate()
    {
        _cache.Clear();
        Interlocked.Exchange(ref _cached, 0);
    }

    // Block wins over Direct: a blocked address must never earn a bypass route.
    private RouteVerdict Evaluate(uint address)
    {
        if (_block.Contains(address))
        {
            return RouteVerdict.Block;
        }

        return _direct.Contains(address) ? RouteVerdict.Direct : RouteVerdict.Proxy;
    }
}
