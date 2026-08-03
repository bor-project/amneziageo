using System.Net;
using System.Net.Sockets;

namespace AmneziaGeo.Linux.App;

/// <summary>
/// Holds IPv4 ranges of a routing bucket and answers whether an address falls in one.
/// </summary>
internal sealed class IpRangeSet
{
    private readonly uint[] _starts;
    private readonly uint[] _ends;

    /// <summary>
    /// ctor
    /// </summary>
    public IpRangeSet(IEnumerable<string> cidrs)
    {
        var ranges = new List<(uint Start, uint End)>();
        foreach (var cidr in cidrs)
        {
            if (TryParse(cidr, out var start, out var end))
            {
                ranges.Add((start, end));
            }
        }

        ranges.Sort((left, right) => left.Start.CompareTo(right.Start));
        var merged = Merge(ranges);
        _starts = [.. merged.Select(range => range.Start)];
        _ends = [.. merged.Select(range => range.End)];
    }

    /// <summary>
    /// Number of ranges after merging.
    /// </summary>
    public int Count => _starts.Length;

    /// <summary>
    /// Whether the address falls in one of the ranges.
    /// </summary>
    public bool Contains(IPAddress address)
    {
        if (_starts.Length == 0 || address.AddressFamily != AddressFamily.InterNetwork)
        {
            return false;
        }

        var value = ToUint(address);
        var index = Array.BinarySearch(_starts, value);
        if (index >= 0)
        {
            return true;
        }

        index = ~index - 1;
        return index >= 0 && value <= _ends[index];
    }

    // Folds overlapping and touching ranges into one another.
    private static List<(uint Start, uint End)> Merge(List<(uint Start, uint End)> sorted)
    {
        var merged = new List<(uint Start, uint End)>();
        foreach (var range in sorted)
        {
            if (merged.Count > 0 && range.Start <= merged[^1].End)
            {
                if (range.End > merged[^1].End)
                {
                    merged[^1] = (merged[^1].Start, range.End);
                }

                continue;
            }

            merged.Add(range);
        }

        return merged;
    }

    // Parses an IPv4 CIDR or bare address into its first and last address; IPv6 entries are skipped.
    private static bool TryParse(string cidr, out uint start, out uint end)
    {
        start = 0;
        end = 0;
        var text = cidr.Trim();
        if (text.Length == 0 || text.Contains(':', StringComparison.Ordinal))
        {
            return false;
        }

        var slash = text.IndexOf('/');
        var host = slash < 0 ? text : text[..slash];
        if (!IPAddress.TryParse(host, out var address) || address.AddressFamily != AddressFamily.InterNetwork)
        {
            return false;
        }

        var prefix = 32;
        if (slash >= 0 && (!int.TryParse(text[(slash + 1)..], out prefix) || prefix is < 0 or > 32))
        {
            return false;
        }

        var mask = prefix == 0 ? 0u : uint.MaxValue << (32 - prefix);
        start = ToUint(address) & mask;
        end = start | ~mask;
        return true;
    }

    private static uint ToUint(IPAddress address)
    {
        var bytes = address.GetAddressBytes();
        return ((uint)bytes[0] << 24) | ((uint)bytes[1] << 16) | ((uint)bytes[2] << 8) | bytes[3];
    }
}
