using System.Globalization;
using System.Numerics;

namespace AmneziaGeo.Routing;

/// <summary>
/// Settles which of two overlapping address rules decides: the narrower range, and the direct one on a tie.
/// </summary>
public static class RangeClaims
{
    /// <summary>
    /// The direct ranges without every tunnel range that lies inside one of them and is narrower than it.
    /// </summary>
    public static IReadOnlyList<string> CarveDirect(IReadOnlyList<string> direct, IReadOnlyList<string> proxy)
    {
        if (direct.Count == 0)
        {
            return direct;
        }

        var inner = new List<(uint Start, uint End, int Bits)>(proxy.Count);
        foreach (var entry in proxy)
        {
            if (GeoIpRanges.TryParse(entry, out var start, out var end, out var bits))
            {
                inner.Add((start, end, bits));
            }
        }

        if (inner.Count == 0)
        {
            return direct;
        }

        inner.Sort(static (a, b) => a.Start != b.Start ? a.Start.CompareTo(b.Start) : a.Bits.CompareTo(b.Bits));
        var carved = new List<string>(direct.Count);
        var cut = false;
        foreach (var entry in direct)
        {
            if (!GeoIpRanges.TryParse(entry, out var start, out var end, out var bits))
            {
                carved.Add(entry);
                continue;
            }

            var holes = Holes(inner, start, end, bits);
            if (holes.Count == 0)
            {
                carved.Add(entry);
                continue;
            }

            cut = true;
            AddPieces(carved, start, end, holes);
        }

        return cut ? carved : direct;
    }

    // The tunnel ranges inside the direct one and narrower than it, low to high.
    private static List<(uint Start, uint End)> Holes(List<(uint Start, uint End, int Bits)> inner, uint start, uint end, int bits)
    {
        var holes = new List<(uint Start, uint End)>();
        for (var at = LowerBound(inner, start); at < inner.Count && inner[at].Start <= end; at++)
        {
            if (inner[at].Bits > bits)
            {
                holes.Add((inner[at].Start, inner[at].End));
            }
        }

        return holes;
    }

    // The first tunnel range starting at the address or above it.
    private static int LowerBound(List<(uint Start, uint End, int Bits)> inner, uint start)
    {
        var low = 0;
        var high = inner.Count;
        while (low < high)
        {
            var mid = low + ((high - low) / 2);
            if (inner[mid].Start < start)
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

    // The direct range less its holes, as the fewest prefixes that cover it.
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
