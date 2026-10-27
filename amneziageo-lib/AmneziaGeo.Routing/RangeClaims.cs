using System.Globalization;
using System.Numerics;

namespace AmneziaGeo.Routing;

/// <summary>
/// Settles which of overlapping address rules decides: the narrower range, and on the same range the block, then the
/// direct one, then the tunnel.
/// </summary>
public static class RangeClaims
{
    /// <summary>
    /// The three sets with every narrower range of another set cut out of the range it lies in and a range two sets
    /// share left to the stronger one, so no address falls into two sets.
    /// </summary>
    public static (IReadOnlyList<string> Proxy, IReadOnlyList<string> Direct, IReadOnlyList<string> Block) Settle(
        IReadOnlyList<string> proxy, IReadOnlyList<string> direct, IReadOnlyList<string> block)
    {
        var proxySpans = Parse(proxy);
        var directSpans = Parse(direct);
        var blockSpans = Parse(block);
        return (
            Carve(proxy, [.. directSpans, .. blockSpans], Keys(directSpans, blockSpans)),
            Carve(direct, [.. proxySpans, .. blockSpans], Keys(blockSpans)),
            Carve(block, [.. proxySpans, .. directSpans], []));
    }

    // The ranges of one set less the narrower ranges of the others inside them; a range a stronger set holds as well
    // is dropped.
    private static IReadOnlyList<string> Carve(IReadOnlyList<string> entries, List<(uint Start, uint End, int Bits)> others, HashSet<(uint, int)> stronger)
    {
        if (entries.Count == 0 || others.Count == 0)
        {
            return entries;
        }

        others.Sort(static (a, b) => a.Start != b.Start ? a.Start.CompareTo(b.Start) : a.Bits.CompareTo(b.Bits));
        var carved = new List<string>(entries.Count);
        var cut = false;
        foreach (var entry in entries)
        {
            if (!GeoIpRanges.TryParse(entry, out var start, out var end, out var bits))
            {
                carved.Add(entry);
                continue;
            }

            if (stronger.Contains((start, bits)))
            {
                cut = true;
                continue;
            }

            var holes = Holes(others, start, end, bits);
            if (holes.Count == 0)
            {
                carved.Add(entry);
                continue;
            }

            cut = true;
            AddPieces(carved, start, end, holes);
        }

        return cut ? carved : entries;
    }

    private static List<(uint Start, uint End, int Bits)> Parse(IReadOnlyList<string> entries)
    {
        var spans = new List<(uint Start, uint End, int Bits)>(entries.Count);
        foreach (var entry in entries)
        {
            if (GeoIpRanges.TryParse(entry, out var start, out var end, out var bits))
            {
                spans.Add((start, end, bits));
            }
        }

        return spans;
    }

    private static HashSet<(uint, int)> Keys(params List<(uint Start, uint End, int Bits)>[] sets)
    {
        var keys = new HashSet<(uint, int)>();
        foreach (var set in sets)
        {
            foreach (var (start, _, bits) in set)
            {
                keys.Add((start, bits));
            }
        }

        return keys;
    }

    // The ranges of the other sets inside this one and narrower than it, low to high.
    private static List<(uint Start, uint End)> Holes(List<(uint Start, uint End, int Bits)> others, uint start, uint end, int bits)
    {
        var holes = new List<(uint Start, uint End)>();
        for (var at = LowerBound(others, start); at < others.Count && others[at].Start <= end; at++)
        {
            if (others[at].Bits > bits)
            {
                holes.Add((others[at].Start, others[at].End));
            }
        }

        return holes;
    }

    // The first range starting at the address or above it.
    private static int LowerBound(List<(uint Start, uint End, int Bits)> others, uint start)
    {
        var low = 0;
        var high = others.Count;
        while (low < high)
        {
            var mid = low + ((high - low) / 2);
            if (others[mid].Start < start)
            {
                low = mid + 1;
            }
            else
            {
                high = mid;
            }
        }

        return low;
    }

    // The range less its holes, as the fewest prefixes that cover it.
    private static void AddPieces(List<string> carved, uint start, uint end, List<(uint Start, uint End)> holes)
    {
        var head = (ulong)start;
        foreach (var (holeStart, holeEnd) in holes)
        {
            if (holeStart > head)
            {
                AddPrefixes(carved, head, holeStart - 1UL);
            }

            head = Math.Max(head, holeEnd + 1UL);
        }

        if (head <= end)
        {
            AddPrefixes(carved, head, end);
        }
    }

    private static void AddPrefixes(List<string> carved, ulong start, ulong end)
    {
        var head = start;
        while (head <= end)
        {
            var size = BitOperations.TrailingZeroCount((uint)head);
            while (size > 0 && head + (1UL << size) - 1 > end)
            {
                size--;
            }

            carved.Add(GeoIpRanges.Format((uint)head) + "/" + (32 - size).ToString(CultureInfo.InvariantCulture));
            head += 1UL << size;
        }
    }
}
