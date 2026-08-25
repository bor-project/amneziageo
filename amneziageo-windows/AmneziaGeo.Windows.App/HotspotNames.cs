using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;

namespace AmneziaGeo.Windows.App;

/// <summary>
/// Stand-in addresses the access point's clients are answered with instead of the real ones. A client that opens
/// one is terminated on this machine, which opens the name again as a socket of its own, so the rules of this
/// machine decide where the traffic leaves and the sharing NAT never does. The addresses come out of a range
/// reserved for benchmarks: it carries no real destination and appears in no geo list, so a stand-in can never
/// cover something a rule already names.
/// </summary>
internal sealed class HotspotNames
{
    /// <summary>
    /// Prefix the stand-in addresses are taken from.
    /// </summary>
    public const string Prefix = "198.18.0.0/15";

    /// <summary>
    /// How long a client keeps the address it was answered with. The answer carries a far shorter TTL, so a
    /// client asking again gets the same address and a connection opened late still finds its name.
    /// </summary>
    public const int LeaseSeconds = 1800;

    private const uint RangeFirst = 0xC6120000; // 198.18.0.0
    private const uint RangeLast = 0xC613FFFF;  // 198.19.255.255
    private const uint First = RangeFirst + 1;
    private const uint Last = RangeLast - 1;
    // Cap far below the range, so a sweep always frees something and the pool never has to be walked far.
    private const int MaxLeases = 16384;

    private readonly ConcurrentDictionary<string, Lease> _byName = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<uint, string> _byAddress = new();
    private readonly object _sync = new();
    private uint _next = First;

    /// <summary>
    /// Names holding an address right now.
    /// </summary>
    public int Count => _byName.Count;

    /// <summary>
    /// Address a name is answered with, taken once and renewed on every use.
    /// </summary>
    public IPAddress Take(string name)
    {
        var key = Key(name);
        if (_byName.TryGetValue(key, out var live))
        {
            live.Renew();
            return Address(live.Value);
        }

        lock (_sync)
        {
            if (_byName.TryGetValue(key, out var raced))
            {
                raced.Renew();
                return Address(raced.Value);
            }

            if (_byName.Count >= MaxLeases)
            {
                Sweep();
            }

            var lease = new Lease(Free());
            _byName[key] = lease;
            _byAddress[lease.Value] = key;
            return Address(lease.Value);
        }
    }

    /// <summary>
    /// Name a stand-in address was handed out for, or null when it stands for nothing.
    /// </summary>
    public string? Name(IPAddress address)
    {
        if (!_byAddress.TryGetValue(Raw(address), out var name))
        {
            return null;
        }

        if (!_byName.TryGetValue(name, out var lease) || lease.Expired)
        {
            return null;
        }

        lease.Renew();
        return name;
    }

    /// <summary>
    /// Drops every address handed out.
    /// </summary>
    public void Clear()
    {
        lock (_sync)
        {
            _byName.Clear();
            _byAddress.Clear();
            _next = First;
        }
    }

    /// <summary>
    /// Whether an address is one of these stand-ins.
    /// </summary>
    public static bool Covers(IPAddress address)
    {
        if (address.AddressFamily != AddressFamily.InterNetwork)
        {
            return false;
        }

        var raw = Raw(address);
        return raw >= RangeFirst && raw <= RangeLast;
    }

    // Takes the next address nothing holds, wrapping at the end of the range.
    private uint Free()
    {
        for (var step = 0u; step <= Last - First; step++)
        {
            var candidate = _next;
            _next = _next >= Last ? First : _next + 1;
            if (!_byAddress.ContainsKey(candidate))
            {
                return candidate;
            }
        }

        _byName.Clear();
        _byAddress.Clear();
        _next = First + 1;
        return First;
    }

    // Drops the leases nothing has used within their lifetime; a wholesale clear when none has expired.
    private void Sweep()
    {
        var freed = 0;
        foreach (var pair in _byName)
        {
            if (!pair.Value.Expired)
            {
                continue;
            }

            if (_byName.TryRemove(new KeyValuePair<string, Lease>(pair.Key, pair.Value)))
            {
                _byAddress.TryRemove(pair.Value.Value, out _);
                freed++;
            }
        }

        if (freed == 0)
        {
            _byName.Clear();
            _byAddress.Clear();
        }
    }

    private static string Key(string name) => name.TrimEnd('.').ToLowerInvariant();

    private static uint Raw(IPAddress address)
    {
        Span<byte> octets = stackalloc byte[4];
        return address.TryWriteBytes(octets, out var written) && written == 4
            ? BinaryPrimitives.ReadUInt32BigEndian(octets)
            : 0;
    }

    private static IPAddress Address(uint raw)
    {
        Span<byte> octets = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(octets, raw);
        return new IPAddress(octets);
    }

    /// <summary>
    /// One address held by one name until it goes unused for its lifetime.
    /// </summary>
    private sealed class Lease
    {
        private long _expiry;

        /// <summary>
        /// ctor
        /// </summary>
        public Lease(uint value)
        {
            Value = value;
            Renew();
        }

        /// <summary>
        /// The address held.
        /// </summary>
        public uint Value { get; }

        /// <summary>
        /// Whether the lifetime ran out.
        /// </summary>
        public bool Expired => Interlocked.Read(ref _expiry) <= Environment.TickCount64;

        /// <summary>
        /// Pushes the lifetime out from now.
        /// </summary>
        public void Renew()
        {
            Interlocked.Exchange(ref _expiry, Environment.TickCount64 + (LeaseSeconds * 1000L));
        }
    }
}
