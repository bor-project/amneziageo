using System.Globalization;
using System.Numerics;

namespace AmneziaGeo.Routing;

/// <summary>
/// Turns a rule set into the two CIDR lists a tunnel is built from: what the tun captures and what the peer is
/// allowed to carry. Block wins over direct, direct over proxy - the precedence the verdict cache follows.
/// </summary>
public static class SystemRoutes
{
    private const string Everything = "0.0.0.0/0";

    /// <summary>
    /// Addresses the tun captures. What it does not capture leaves through the physical path, which is what makes a
    /// direct rule free: the packet never reaches the tunnel at all.
    /// </summary>
    public static IReadOnlyList<string> Tunneled(
        bool fullTunnel,
        IReadOnlyList<string> proxy,
        IReadOnlyList<string> direct,
        IReadOnlyList<string> block)
    {
        var blocked = Spans(block);
        var bypassed = Subtract(Spans(direct), blocked);
        var captured = fullTunnel
            ? Subtract(Whole(), bypassed)
            : Union(Subtract(Spans(proxy), bypassed), blocked);

        return captured.Count == 0 ? [] : ToCidrs(captured);
    }

    /// <summary>
    /// Addresses the peer carries. A destination the tun captured but the peer may not carry is dropped by the
    /// engine's own address lookup, which is how a block rule is enforced without anyone reading the packet.
    /// </summary>
    public static IReadOnlyList<string> Allowed(IReadOnlyList<string> block)
    {
        var blocked = Spans(block);
        return blocked.Count == 0 ? [Everything] : ToCidrs(Subtract(Whole(), blocked));
    }

    private static List<(ulong Start, ulong End)> Spans(IReadOnlyList<string> entries)
    {
        var result = new List<(ulong Start, ulong End)>();
        foreach (var (start, end) in GeoIpRanges.Build(entries).Spans)
        {
            result.Add((start, end));
        }

        return result;
    }

    private static List<(ulong Start, ulong End)> Whole() => [(0UL, uint.MaxValue)];

    // Both sides arrive sorted and merged, so one forward pass over the cut list serves every span.
    private static List<(ulong Start, ulong End)> Subtract(List<(ulong Start, ulong End)> from, List<(ulong Start, ulong End)> cut)
    {
        if (cut.Count == 0)
        {
            return from;
        }

        var result = new List<(ulong Start, ulong End)>(from.Count);
        var index = 0;
        foreach (var (start, end) in from)
        {
            var head = start;
            while (index < cut.Count && cut[index].End < head)
            {
                index++;
            }

            var scan = index;
            while (scan < cut.Count && cut[scan].Start <= end)
            {
                if (cut[scan].Start > head)
                {
                    result.Add((head, cut[scan].Start - 1));
                }

                if (cut[scan].End + 1 > head)
                {
                    head = cut[scan].End + 1;
                }

                if (head > end)
                {
                    break;
                }

                scan++;
            }

            if (head <= end)
            {
                result.Add((head, end));
            }
        }

        return result;
    }

    private static List<(ulong Start, ulong End)> Union(List<(ulong Start, ulong End)> left, List<(ulong Start, ulong End)> right)
    {
        if (right.Count == 0)
        {
            return left;
        }

        if (left.Count == 0)
        {
            return right;
        }

        var all = new List<(ulong Start, ulong End)>(left.Count + right.Count);
        all.AddRange(left);
        all.AddRange(right);
        all.Sort(static (a, b) => a.Start.CompareTo(b.Start));

        var merged = new List<(ulong Start, ulong End)>(all.Count);
        var (openStart, openEnd) = all[0];
        for (var i = 1; i < all.Count; i++)
        {
            var (start, end) = all[i];
            // Adjacency folds too, so two halves of a block leave as one route.
            if (start <= openEnd + 1)
            {
                if (end > openEnd)
                {
                    openEnd = end;
                }

                continue;
            }

            merged.Add((openStart, openEnd));
            (openStart, openEnd) = (start, end);
        }

        merged.Add((openStart, openEnd));
        return merged;
    }

    // Splits a range into the fewest prefixes that cover it exactly: the largest block aligned at the head that
    // still fits, over and over.
    private static List<string> ToCidrs(List<(ulong Start, ulong End)> spans)
    {
        var result = new List<string>(spans.Count);
        foreach (var (start, end) in spans)
        {
            var head = start;
            while (head <= end)
            {
                var size = BitOperations.TrailingZeroCount((uint)head);
                while (size > 0 && head + (1UL << size) - 1 > end)
                {
                    size--;
                }

                result.Add(GeoIpRanges.Format((uint)head) + "/" + (32 - size).ToString(CultureInfo.InvariantCulture));
                head += 1UL << size;
            }
        }

        return result;
    }
}
