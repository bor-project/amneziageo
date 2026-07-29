using System.Net;
using System.Net.Sockets;

namespace AmneziaGeo.Windows.App;

/// <summary>
/// Collapses IPv4 CIDRs into the smallest equivalent set; other entries pass through unchanged.
/// </summary>
internal static class CidrAggregator
{
    /// <summary>
    /// Drops nested prefixes and pairs aligned siblings, never widening the covered set.
    /// </summary>
    public static IReadOnlyList<string> Aggregate(IReadOnlyList<string> cidrs)
    {
        var blocks = new List<(uint Network, byte Prefix)>(cidrs.Count);
        var passthrough = new List<string>();

        foreach (var cidr in cidrs)
        {
            if (TryParse(cidr, out var network, out var prefix))
            {
                blocks.Add((network, prefix));
            }
            else
            {
                passthrough.Add(cidr);
            }
        }

        if (blocks.Count == 0)
        {
            return cidrs;
        }

        // Widest first at a shared network, so a covering block is always already on the stack.
        blocks.Sort(static (a, b) => a.Network != b.Network
            ? a.Network.CompareTo(b.Network)
            : a.Prefix.CompareTo(b.Prefix));

        var merged = new List<(uint Network, byte Prefix)>(blocks.Count);
        foreach (var block in blocks)
        {
            var current = block;
            var absorbed = false;

            while (merged.Count > 0)
            {
                var top = merged[^1];
                if (Covers(top, current))
                {
                    absorbed = true;
                    break;
                }

                if (!CanPair(top, current))
                {
                    break;
                }

                // The pair becomes one prefix shorter and may pair again with what precedes it.
                merged.RemoveAt(merged.Count - 1);
                current = (top.Network, (byte)(top.Prefix - 1));
            }

            if (!absorbed)
            {
                merged.Add(current);
            }
        }

        var result = new List<string>(merged.Count + passthrough.Count);
        foreach (var block in merged)
        {
            result.Add(Format(block.Network, block.Prefix));
        }

        result.AddRange(passthrough);
        return result;
    }

    private static bool Covers((uint Network, byte Prefix) outer, (uint Network, byte Prefix) inner)
    {
        return outer.Prefix <= inner.Prefix && (inner.Network & MaskOf(outer.Prefix)) == outer.Network;
    }

    private static bool CanPair((uint Network, byte Prefix) left, (uint Network, byte Prefix) right)
    {
        if (left.Prefix != right.Prefix || left.Prefix == 0)
        {
            return false;
        }

        var parentMask = MaskOf((byte)(left.Prefix - 1));
        return (left.Network & parentMask) == left.Network
            && right.Network - left.Network == SizeOf(left.Prefix);
    }

    private static uint MaskOf(byte prefix)
    {
        return prefix == 0 ? 0u : uint.MaxValue << (32 - prefix);
    }

    private static uint SizeOf(byte prefix)
    {
        return prefix == 0 ? 0u : 1u << (32 - prefix);
    }

    private static bool TryParse(string cidr, out uint network, out byte prefix)
    {
        network = 0;
        prefix = 0;
        var slash = cidr.IndexOf('/');
        if (slash < 0
            || !IPAddress.TryParse(cidr[..slash], out var address)
            || address.AddressFamily != AddressFamily.InterNetwork
            || !byte.TryParse(cidr[(slash + 1)..], out var bits)
            || bits > 32)
        {
            return false;
        }

        var bytes = address.GetAddressBytes();
        network = (((uint)bytes[0] << 24) | ((uint)bytes[1] << 16) | ((uint)bytes[2] << 8) | bytes[3]) & MaskOf(bits);
        prefix = bits;
        return true;
    }

    private static string Format(uint network, byte prefix)
    {
        return $"{(network >> 24) & 0xFF}.{(network >> 16) & 0xFF}.{(network >> 8) & 0xFF}.{network & 0xFF}/{prefix}";
    }
}
