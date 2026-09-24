using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;

namespace AmneziaGeo.Windows.App;

/// <summary>
/// Addresses the system got for a name in place of the ones the resolver behind the tunnel gives, the way a
/// corporate resolver answers a blocked site with a page of its own. Connections to them go into the gateway, which
/// opens each again by the name it carries.
/// </summary>
internal sealed class SubstitutedAddresses
{
    // A resolver that answers blocked names with a page of its own gives a handful of addresses for all of them.
    private const int MaxHeld = 256;

    private readonly ConcurrentDictionary<IPAddress, byte> _held = new();

    /// <summary>
    /// Raised once for every address taken in.
    /// </summary>
    public event Action<IPAddress>? Added;

    /// <summary>
    /// Addresses taken in so far.
    /// </summary>
    public IReadOnlyList<IPAddress> All => [.. _held.Keys];

    /// <summary>
    /// Whether the system got this address in place of a real one.
    /// </summary>
    public bool Contains(IPAddress address)
    {
        return _held.ContainsKey(address);
    }

    /// <summary>
    /// Takes an address in; false when it is there already, is not a public IPv4 address or the list is full.
    /// </summary>
    public bool Add(IPAddress address)
    {
        if (!Public(address) || _held.Count >= MaxHeld || !_held.TryAdd(address, 0))
        {
            return false;
        }

        Added?.Invoke(address);
        return true;
    }

    /// <summary>
    /// The addresses the system got in place of the real ones: its public IPv4 addresses when none of them is among
    /// those the resolver behind the tunnel gives, none when one is or when the tunnel gave nothing.
    /// </summary>
    public static IReadOnlyList<IPAddress> Of(IReadOnlyList<IPAddress> system, IReadOnlyList<IPAddress> tunnel)
    {
        var real = tunnel.Where(address => address.AddressFamily == AddressFamily.InterNetwork).ToHashSet();
        var got = system.Where(address => address.AddressFamily == AddressFamily.InterNetwork).Distinct().ToList();
        if (real.Count == 0 || got.Count == 0 || got.Any(real.Contains))
        {
            return [];
        }

        return [.. got.Where(Public)];
    }

    // Whether an address is a public IPv4 one: a private, loopback or reserved address the resolver gave belongs to
    // the network the machine stands in, and taking it off its path would cut that network off.
    private static bool Public(IPAddress address)
    {
        if (address.AddressFamily != AddressFamily.InterNetwork)
        {
            return false;
        }

        var b = address.GetAddressBytes();
        return b[0] switch
        {
            0 or 10 or 127 => false,
            100 when b[1] is >= 64 and <= 127 => false,
            169 when b[1] == 254 => false,
            172 when b[1] is >= 16 and <= 31 => false,
            192 when b[1] == 168 => false,
            198 when b[1] is 18 or 19 => false,
            >= 224 => false,
            _ => true,
        };
    }
}
