using System.Net;
using System.Net.Sockets;

namespace AmneziaGeo.Routing;

/// <summary>
/// Sorted IPv4 range set over a geo rule set; answers membership by binary search instead of materializing routes.
/// </summary>
public sealed class GeoIpRanges
{
    private readonly uint[] _starts;
    private readonly uint[] _ends;

    private GeoIpRanges(uint[] starts, uint[] ends)
    {
        _starts = starts;
        _ends = ends;
    }

    /// <summary>
    /// Empty set; matches nothing.
    /// </summary>
    public static GeoIpRanges Empty { get; } = new([], []);

    /// <summary>
    /// Range count after merging.
    /// </summary>
    public int Count => _starts.Length;

    /// <summary>
    /// Merged ranges, low to high.
    /// </summary>
    public IEnumerable<(uint Start, uint End)> Spans
    {
        get
        {
            for (var i = 0; i < _starts.Length; i++)
            {
                yield return (_starts[i], _ends[i]);
            }
        }
    }

    /// <summary>
    /// Builds the set from CIDRs or bare addresses, merging overlapping and adjacent ranges. Non-IPv4 entries are skipped.
    /// </summary>
    public static GeoIpRanges Build(IReadOnlyList<string> entries)
    {
        var ranges = new List<(uint Start, uint End)>(entries.Count);
        foreach (var entry in entries)
        {
            if (TryParse(entry, out var start, out var end))
            {
                ranges.Add((start, end));
            }
        }

        if (ranges.Count == 0)
        {
            return Empty;
        }

        ranges.Sort(static (a, b) => a.Start != b.Start ? a.Start.CompareTo(b.Start) : a.End.CompareTo(b.End));

        var starts = new List<uint>(ranges.Count);
        var ends = new List<uint>(ranges.Count);
        var openStart = ranges[0].Start;
        var openEnd = ranges[0].End;

        for (var i = 1; i < ranges.Count; i++)
        {
            var (start, end) = ranges[i];
            // Adjacency folds too: 1.0.0.0/24 followed by 1.0.1.0/24 becomes one range, halving the search space.
            var joins = start <= openEnd || (openEnd < uint.MaxValue && start == openEnd + 1);
            if (joins)
            {
                if (end > openEnd)
                {
                    openEnd = end;
                }

                continue;
            }

            starts.Add(openStart);
            ends.Add(openEnd);
            openStart = start;
            openEnd = end;
        }

        starts.Add(openStart);
        ends.Add(openEnd);
        return new GeoIpRanges([.. starts], [.. ends]);
    }

    /// <summary>
    /// Returns whether the host-order address falls in any range.
    /// </summary>
    public bool Contains(uint address)
    {
        var low = 0;
        var high = _starts.Length - 1;
        while (low <= high)
        {
            var mid = low + ((high - low) / 2);
            if (address < _starts[mid])
            {
                high = mid - 1;
            }
            else if (address > _ends[mid])
            {
                low = mid + 1;
            }
            else
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Converts an IPv4 address to host-order numeric form.
    /// </summary>
    public static bool TryToNumeric(IPAddress address, out uint value)
    {
        value = 0;
        if (address.AddressFamily != AddressFamily.InterNetwork)
        {
            return false;
        }

        var bytes = address.GetAddressBytes();
        value = ((uint)bytes[0] << 24) | ((uint)bytes[1] << 16) | ((uint)bytes[2] << 8) | bytes[3];
        return true;
    }

    /// <summary>
    /// Formats a host-order address as dotted quad.
    /// </summary>
    public static string Format(uint address)
    {
        return $"{(address >> 24) & 0xFF}.{(address >> 16) & 0xFF}.{(address >> 8) & 0xFF}.{address & 0xFF}";
    }

    private static bool TryParse(string entry, out uint start, out uint end)
    {
        start = 0;
        end = 0;
        var slash = entry.IndexOf('/');
        var host = slash < 0 ? entry : entry[..slash];
        if (!IPAddress.TryParse(host, out var address) || !TryToNumeric(address, out var network))
        {
            return false;
        }

        // A bare address is a single-host range.
        var bits = (byte)32;
        if (slash >= 0 && (!byte.TryParse(entry[(slash + 1)..], out bits) || bits > 32))
        {
            return false;
        }

        var mask = bits == 0 ? 0u : uint.MaxValue << (32 - bits);
        start = network & mask;
        end = start | ~mask;
        return true;
    }
}
