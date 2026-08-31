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

    /// <summary>
    /// Direct ranges a tun that carries everything still leaves outside itself, widest first, as many as the route
    /// budget holds. What never reaches a relay - a datagram, a socket that ignores the proxy - is decided by the
    /// route table alone, so these ranges take the physical path even behind one.
    /// </summary>
    public static IReadOnlyList<string> Carve(
        IReadOnlyList<string> direct,
        IReadOnlyList<string> keep,
        IReadOnlyList<string> block,
        int budget)
    {
        var all = Spans(direct);
        if (all.Count == 0 || budget <= 0)
        {
            return [];
        }

        var blocked = Spans(block);
        var kept = Subtract(Spans(keep), blocked);
        var (spans, rank) = Narrow(all, budget);

        // A blocked part of a range stays inside the tun, so the count walks the ranges without it while the widest
        // ones are still chosen by the width they have as a whole.
        var pieces = Subtract(spans, blocked);
        var order = blocked.Count == 0 ? rank : Rerank(spans, pieces, rank);

        // Every range carved out costs routes, so the widest set that still fits is found by probing. Each probe
        // follows the rate the last one showed, and every other round halves the window so a rate that misleads
        // cannot stall the search. A set wider than the budget never fits: merged ranges leave a gap between them.
        var taken = 0;
        var rest = Math.Min(spans.Count, budget + 1);
        var mark = 0;
        var cost = 0;
        var half = false;
        while (taken < rest)
        {
            var mid = half || cost == 0 ? (taken + rest + 1) / 2 : Rate(taken, rest, mark, cost, budget);
            half = !half;
            cost = Routes(pieces, order, mid, kept, budget * 2);
            mark = mid;
            if (cost > budget)
            {
                rest = mid - 1;
                continue;
            }

            taken = mid;
        }

        return taken == 0 ? [] : ToCidrs(Head(spans, rank, taken));
    }

    // Routes such a tun carries once these ranges are left outside it, counted no further than the cap: a set that
    // overshoots is turned down whatever the exact number is. The two sets are walked side by side and the space
    // between them counted as it comes, so a round of the search builds nothing.
    private static int Routes(
        List<(ulong Start, ulong End)> spans,
        int[] rank,
        int count,
        List<(ulong Start, ulong End)> keep,
        int cap)
    {
        var routes = 0;
        var head = 0UL;
        var i = 0;
        var k = 0;
        while (true)
        {
            while (i < spans.Count && rank[i] >= count)
            {
                i++;
            }

            if (i == spans.Count && k == keep.Count)
            {
                break;
            }

            var (start, end) = i < spans.Count && (k == keep.Count || spans[i].Start <= keep[k].Start)
                ? spans[i++]
                : keep[k++];
            if (start > head)
            {
                routes += PrefixCount(head, start - 1);
                if (routes > cap)
                {
                    return routes;
                }
            }

            if (end + 1 > head)
            {
                head = end + 1;
            }
        }

        return head > uint.MaxValue ? routes : routes + PrefixCount(head, uint.MaxValue);
    }

    // Width order of the range every piece came from.
    private static int[] Rerank(
        List<(ulong Start, ulong End)> spans,
        List<(ulong Start, ulong End)> pieces,
        int[] rank)
    {
        var order = new int[pieces.Count];
        var i = 0;
        for (var p = 0; p < pieces.Count; p++)
        {
            while (i < spans.Count && spans[i].End < pieces[p].Start)
            {
                i++;
            }

            order[p] = i < spans.Count ? rank[i] : rank.Length;
        }

        return order;
    }

    // Prefixes a range takes, counted without listing them.
    private static int PrefixCount(ulong start, ulong end)
    {
        var count = 0;
        var head = start;
        while (head <= end)
        {
            var size = BitOperations.TrailingZeroCount((uint)head);
            while (size > 0 && head + (1UL << size) - 1 > end)
            {
                size--;
            }

            count++;
            head += 1UL << size;
        }

        return count;
    }

    // Count the rate of the last probe points at, when it lands inside the window.
    private static int Rate(int taken, int rest, int mark, int cost, int budget)
    {
        var guess = (int)((long)mark * budget / cost);
        return guess > taken && guess <= rest ? guess : (taken + rest + 1) / 2;
    }

    // The set without the ranges no budget could ever take, each with its place in width order: every range carved
    // out costs a route of its own.
    private static (List<(ulong Start, ulong End)> Spans, int[] Rank) Narrow(
        List<(ulong Start, ulong End)> spans,
        int budget)
    {
        var rank = Ranks(spans);
        if (spans.Count <= budget)
        {
            return (spans, rank);
        }

        var kept = new List<(ulong Start, ulong End)>(budget + 1);
        var order = new int[budget + 1];
        for (var i = 0; i < spans.Count; i++)
        {
            if (rank[i] <= budget)
            {
                order[kept.Count] = rank[i];
                kept.Add(spans[i]);
            }
        }

        return (kept, order);
    }

    // Place of every range in width order, so each round of the search takes the widest ones without sorting again.
    private static int[] Ranks(List<(ulong Start, ulong End)> spans)
    {
        // The key is the width counted down, so the widest range sorts first and the sort needs no comparer.
        var width = new ulong[spans.Count];
        var order = new int[spans.Count];
        for (var i = 0; i < order.Length; i++)
        {
            width[i] = ulong.MaxValue - (spans[i].End - spans[i].Start);
            order[i] = i;
        }

        Array.Sort(width, order);

        var rank = new int[spans.Count];
        for (var i = 0; i < order.Length; i++)
        {
            rank[order[i]] = i;
        }

        return rank;
    }

    // The widest ranges of the set, in address order.
    private static List<(ulong Start, ulong End)> Head(List<(ulong Start, ulong End)> spans, int[] rank, int count)
    {
        var head = new List<(ulong Start, ulong End)>(count);
        for (var i = 0; i < spans.Count; i++)
        {
            if (rank[i] < count)
            {
                head.Add(spans[i]);
            }
        }

        return head;
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

        // Both sides arrive sorted, so one pass folds them together and no sort is needed.
        var merged = new List<(ulong Start, ulong End)>(left.Count + right.Count);
        var l = 0;
        var r = 0;
        var (openStart, openEnd) = left[0].Start <= right[0].Start ? left[l++] : right[r++];
        while (l < left.Count || r < right.Count)
        {
            var (start, end) = r == right.Count || (l < left.Count && left[l].Start <= right[r].Start)
                ? left[l++]
                : right[r++];

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

    private static List<string> ToCidrs(List<(ulong Start, ulong End)> spans)
    {
        var result = new List<string>(spans.Count);
        foreach (var (head, size) in Prefixes(spans))
        {
            result.Add(GeoIpRanges.Format(head) + "/" + (32 - size).ToString(CultureInfo.InvariantCulture));
        }

        return result;
    }

    // Splits a range into the fewest prefixes that cover it exactly: the largest block aligned at the head that
    // still fits, over and over.
    private static IEnumerable<(uint Head, int Size)> Prefixes(List<(ulong Start, ulong End)> spans)
    {
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

                yield return ((uint)head, size);
                head += 1UL << size;
            }
        }
    }
}
