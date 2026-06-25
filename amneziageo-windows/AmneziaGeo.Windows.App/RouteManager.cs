using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace AmneziaGeo.Windows.App;

/// <summary>
/// Manages tunnel route-table entries and the tunnel adapter's IPv6 resolvers entirely through the
/// IP Helper API (iphlpapi) — no process spawns. IPv4 routes (endpoint-exclusion host route and the
/// per-domain /32 routes injected for geo split tunneling) go through CreateIpForwardEntry2 /
/// DeleteIpForwardEntry2 / GetIpForwardTable2, the same modern API the reference daemon uses. These
/// tunnels are IPv4-only (AAAA is denied at the proxy), so IPv6 routes are vestigial and are a no-op.
/// Routes are persisted per tunnel so they can be reverted even after a crash, from another process.
/// </summary>
internal sealed partial class RouteManager
{
    private const ushort AfInet = 2;
    private const ushort AfInet6 = 23;
    private const uint NoError = 0;
    private const uint ErrorObjectAlreadyExists = 5010;
    private const uint MibIpProtoNetMgmt = 3; // NL_ROUTE_PROTOCOL RouteProtocolNetMgmt — tags our routes

    // Routable private IPv4 ranges kept OFF the tunnel in full-tunnel mode so the local network — RDP,
    // SSH, printers, including hosts one hop away in another local subnet — stays reachable in parallel
    // with the tunnel. Link-local / multicast / broadcast are on-link or special and need no exclusion
    // route (mirrors the reference's excludeLocalNetworks, which silently skips those).
    private static readonly (string Network, byte Prefix)[] LanExclusions =
    [
        ("10.0.0.0", 8),
        ("172.16.0.0", 12),
        ("192.168.0.0", 16),
    ];

    // The IPv6 LAN range kept off the tunnel on a dual-stack tunnel: ULA (the v6 analogue of RFC1918).
    // Link-local (fe80::/10) and multicast are on-link/special and need no exclusion route. Only added
    // when a physical v6 route to it exists (else skipped), so v4-only networks are unaffected.
    private static readonly (string Network, byte Prefix)[] LanExclusionsV6 =
    [
        ("fc00::", 7),
    ];

    /// <summary>
    /// Adds a host route for the endpoint via the current physical gateway, so the tunnel's underlay
    /// packets to the server are not themselves routed into the tunnel. Persisted (keyed by tunnel
    /// name) so it can be reverted even if this process exits without teardown.
    /// </summary>
    public bool AddEndpointExclusion(string name, IPAddress endpoint)
    {
        var (gateway, interfaceIndex) = FindPhysicalGateway(endpoint);
        if (gateway is null)
        {
            return false;
        }

        var row = NewRow(endpoint, 32, interfaceIndex, gateway);
        var result = CreateIpForwardEntry2(ref row);
        if (result is NoError or ErrorObjectAlreadyExists)
        {
            // The endpoint route sits on the physical adapter and does not vanish with the tunnel.
            UpdateState(name, endpoint.ToString(), add: true);
            return true;
        }

        return false;
    }

    /// <summary>
    /// Removes the endpoint host route.
    /// </summary>
    public void RemoveEndpointExclusion(string name, IPAddress endpoint)
    {
        DeleteManagedRoutes(endpoint, ifIndex: null);
        UpdateState(name, endpoint.ToString(), add: false);
    }

    /// <summary>
    /// Removes any endpoint-exclusion routes persisted by a previous run (any tunnel), even from
    /// another process; no-op if none are recorded. Used to revert leftovers from a killed tunnel.
    /// </summary>
    public void RestoreSavedExclusions()
    {
        foreach (var file in TunnelPaths.RouteStateFiles())
        {
            foreach (var endpoint in ReadStateFile(file))
            {
                if (IPAddress.TryParse(endpoint, out var ip) && ip.AddressFamily == AddressFamily.InterNetwork)
                {
                    DeleteManagedRoutes(ip, ifIndex: null);
                }
            }

            TryDelete(file);
        }
    }

    /// <summary>
    /// In full-tunnel mode the engine routes the default (split into two /1 halves) into the tunnel,
    /// which would swallow the local network too. This pins the private LAN ranges to the physical
    /// gateway with a more-specific route so local access (RDP/SSH/printers, including a host one hop
    /// away in another local subnet) keeps working alongside the tunnel. The connected subnet stays
    /// direct anyway via its on-link route; this adds the routed local subnets the on-link route misses.
    /// Each range is persisted (keyed by tunnel name) so it can be reverted even after a crash. Must be
    /// called before the tunnel adapter comes up so the best next-hop resolves to the physical gateway,
    /// not the tunnel. Returns true if any exclusion was installed.
    /// </summary>
    public bool AddLanExclusions(string name, bool dualStack, IReadOnlyList<string> extraCidrs)
    {
        var any = false;

        // Bypass entries — the runtime default set (RFC1918 ranges + connected subnets, see
        // DefaultExclusionEntries) when the config has no exclusions row, otherwise the user's saved list.
        // Route each straight out the physical gateway so the chosen hosts/subnets stay direct in full
        // tunnel, persisted so they are reverted together. IPv4 only — the common LAN/bypass case.
        foreach (var cidr in extraCidrs)
        {
            var slash = cidr.IndexOf('/');
            if (slash < 0
                || !IPAddress.TryParse(cidr[..slash], out var dest)
                || dest.AddressFamily != AddressFamily.InterNetwork
                || !byte.TryParse(cidr[(slash + 1)..], out var prefix))
            {
                continue;
            }

            var (gateway, interfaceIndex) = FindPhysicalGateway(dest);
            if (gateway is null)
            {
                continue;
            }

            var row = NewRow(dest, prefix, interfaceIndex, gateway);
            var result = CreateIpForwardEntry2(ref row);
            if (result is NoError or ErrorObjectAlreadyExists)
            {
                UpdateStateFile(TunnelPaths.LanStateFile(name), $"{dest}/{prefix}", add: true);
                any = true;
            }
        }

        // On a dual-stack tunnel the engine also routes ::/0 (as ::/1 + 8000::/1) into the tunnel, so the
        // v6 LAN needs the same exclusion. Use the kernel's best physical v6 route as the next-hop; if no
        // v6 route to ULA exists, skip it (v4-only / no-v6 networks stay untouched).
        if (dualStack)
        {
            foreach (var (network, prefix) in LanExclusionsV6)
            {
                var dest = IPAddress.Parse(network);
                if (FindBestV6Route(dest) is not { } best)
                {
                    continue;
                }

                var row = NewRowV6(dest, prefix, best.InterfaceIndex, best.NextHop);
                var result = CreateIpForwardEntry2(ref row);
                if (result is NoError or ErrorObjectAlreadyExists)
                {
                    UpdateStateFile(TunnelPaths.LanStateFile(name), $"{network}/{prefix}", add: true);
                    any = true;
                }
            }
        }

        return any;
    }

    /// <summary>
    /// Detects the machine's currently-connected local IPv4 subnets: the on-link networks of up, physical
    /// adapters (loopback, tunnels, our own WireGuard/wintun adapters, and APIPA link-local skipped),
    /// returned as "network/prefix". Includes the ordinary RFC1918 home/office ranges so the user can see
    /// exactly which subnets are kept direct, not just the rare non-RFC1918 corporate / CGNAT LANs.
    /// Re-evaluated on each call so DHCP / roaming changes are reflected.
    /// </summary>
    public IReadOnlyList<string> LocalSubnets()
    {
        var result = new List<string>();
        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.OperationalStatus != OperationalStatus.Up
                || ni.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
            {
                continue;
            }

            // Never harvest our own tunnel adapter's subnet — excluding it would break the tunnel.
            if (ni.Name.StartsWith("AmneziaGeo", StringComparison.OrdinalIgnoreCase)
                || ni.Description.Contains("WireGuard", StringComparison.OrdinalIgnoreCase)
                || ni.Description.Contains("AmneziaWG", StringComparison.OrdinalIgnoreCase)
                || ni.Description.Contains("Wintun", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            foreach (var ua in ni.GetIPProperties().UnicastAddresses)
            {
                if (ua.Address.AddressFamily != AddressFamily.InterNetwork)
                {
                    continue;
                }

                var prefix = ua.PrefixLength;
                if (prefix is <= 0 or >= 31)
                {
                    continue; // /31-/32 is a point-to-point/host, /0 a default — none describe a LAN
                }

                var network = NetworkAddress(ua.Address, prefix);
                if (IsLinkLocal(network, prefix))
                {
                    continue; // APIPA auto-config address — no real LAN behind it, not worth listing
                }

                var cidr = $"{network}/{prefix}";
                if (!result.Contains(cidr))
                {
                    result.Add(cidr);
                }
            }
        }

        return result;
    }

    /// <summary>
    /// The default bypass set, materialised as list entries: the RFC1918 LAN ranges plus any currently
    /// connected subnet outside them (CGNAT / public-IP LAN). Seeds a fresh config's exclusions and backs the
    /// "add local networks" button, so what used to be an implicit full-tunnel floor is visible and editable.
    /// </summary>
    public IReadOnlyList<string> DefaultExclusionEntries()
    {
        var list = LanExclusions.Select(r => $"{r.Network}/{r.Prefix}").ToList();
        foreach (var subnet in LocalSubnets())
        {
            if (!IsWithinDefaultRanges(subnet) && !list.Contains(subnet))
            {
                list.Add(subnet);
            }
        }

        return list;
    }

    // Whether a CIDR's network falls inside one of the RFC1918 default ranges, so a connected subnet already
    // covered by the defaults is not also listed redundantly (e.g. 192.168.1.0/24 under 192.168.0.0/16).
    private static bool IsWithinDefaultRanges(string cidr)
    {
        var slash = cidr.IndexOf('/');
        if (slash < 0 || !IPAddress.TryParse(cidr[..slash], out var addr))
        {
            return false;
        }

        return LanExclusions.Any(r => InRange(addr, IPAddress.Parse(r.Network), r.Prefix));
    }

    // True for an APIPA link-local 169.254/16 network — a DHCP-failed auto-config address with no real LAN
    // behind it, so listing it as a "local subnet" would only be noise.
    private static bool IsLinkLocal(IPAddress network, int prefix)
        => prefix >= 16 && InRange(network, IPAddress.Parse("169.254.0.0"), 16);

    private static IPAddress NetworkAddress(IPAddress ip, int prefix)
    {
        var bytes = ip.GetAddressBytes();
        var mask = PrefixToMask(prefix);
        for (var i = 0; i < bytes.Length; i++)
        {
            bytes[i] &= mask[i];
        }

        return new IPAddress(bytes);
    }

    private static bool InRange(IPAddress addr, IPAddress network, int prefix)
    {
        var a = addr.GetAddressBytes();
        var n = network.GetAddressBytes();
        var mask = PrefixToMask(prefix);
        for (var i = 0; i < 4; i++)
        {
            if ((a[i] & mask[i]) != (n[i] & mask[i]))
            {
                return false;
            }
        }

        return true;
    }

    private static byte[] PrefixToMask(int prefix)
    {
        var mask = new byte[4];
        for (var i = 0; i < prefix && i < 32; i++)
        {
            mask[i / 8] |= (byte)(0x80 >> (i % 8));
        }

        return mask;
    }

    /// <summary>
    /// Removes the LAN-bypass exclusion routes installed for a tunnel.
    /// </summary>
    public void RemoveLanExclusions(string name)
    {
        var path = TunnelPaths.LanStateFile(name);
        foreach (var cidr in ReadStateFile(path))
        {
            DeleteCidrRoute(cidr);
        }

        TryDelete(path);
    }

    /// <summary>
    /// Removes any LAN-bypass exclusion routes persisted by a previous run (any tunnel), even from
    /// another process; reverts leftovers from a killed full-tunnel session. No-op if none are recorded.
    /// </summary>
    public void RestoreSavedLanExclusions()
    {
        foreach (var file in TunnelPaths.LanStateFiles())
        {
            foreach (var cidr in ReadStateFile(file))
            {
                DeleteCidrRoute(cidr);
            }

            TryDelete(file);
        }
    }

    private static void DeleteCidrRoute(string cidr)
    {
        var slash = cidr.IndexOf('/');
        var network = slash >= 0 ? cidr[..slash] : cidr;
        if (!IPAddress.TryParse(network, out var ip))
        {
            return;
        }

        // Match the exact prefix length too: a LAN exclusion shares its network address with no host
        // route we create, but matching by address alone would over-delete a same-network route at a
        // different prefix. The CIDR string carries the length, so use it.
        byte? prefix = slash >= 0 && byte.TryParse(cidr[(slash + 1)..], out var p) ? p : null;
        if (ip.AddressFamily == AddressFamily.InterNetworkV6)
        {
            DeleteManagedV6Routes(ip, prefix ?? 128);
        }
        else if (ip.AddressFamily == AddressFamily.InterNetwork)
        {
            DeleteManagedRoutes(ip, ifIndex: null, prefixLength: prefix);
        }
    }

    /// <summary>
    /// Adds an on-link host route for an IP through the tunnel interface (a /32 for IPv4, a /128 for
    /// IPv6), so a geo-matched destination is carried by the tunnel. On a v4-only tunnel the caller
    /// never hands an IPv6 address (AAAA is denied at the proxy); on a dual-stack tunnel both apply.
    /// </summary>
    public bool AddTunnelRoute(IPAddress ip, uint tunnelInterfaceIndex)
    {
        var row = ip.AddressFamily == AddressFamily.InterNetworkV6
            ? NewRowV6(ip, 128, tunnelInterfaceIndex, nextHop: null) // on-link (no gateway)
            : NewRow(ip, 32, tunnelInterfaceIndex, nextHop: null);
        var result = CreateIpForwardEntry2(ref row);
        return result is NoError or ErrorObjectAlreadyExists;
    }

    /// <summary>
    /// Removes a host route for an IP from the tunnel interface (v4 /32 or v6 /128).
    /// </summary>
    public void RemoveTunnelRoute(IPAddress ip, uint tunnelInterfaceIndex)
    {
        if (ip.AddressFamily == AddressFamily.InterNetworkV6)
        {
            DeleteManagedV6Routes(ip, 128);
            return;
        }

        DeleteManagedRoutes(ip, tunnelInterfaceIndex);
    }

    /// <summary>
    /// Returns the IPv4 interface index of a network adapter by name.
    /// </summary>
    public uint? FindInterfaceIndex(string adapterName)
    {
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.Name != adapterName)
            {
                continue;
            }

            var index = Ipv4Index(nic);
            if (index is not null)
            {
                return (uint)index.Value;
            }
        }

        return null;
    }

    /// <summary>
    /// Builds an IPv4 MIB_IPFORWARD_ROW2 for a host/prefix route, initialized to safe static-route
    /// defaults. A null next hop yields an on-link route; otherwise the route is via that gateway.
    /// </summary>
    private static MIB_IPFORWARD_ROW2 NewRow(IPAddress destination, byte prefixLength, uint interfaceIndex, IPAddress? nextHop)
    {
        var row = new MIB_IPFORWARD_ROW2();
        InitializeIpForwardEntry(ref row);
        row.InterfaceIndex = interfaceIndex;
        row.DestinationPrefix.Prefix.si_family = AfInet;
        row.DestinationPrefix.Prefix.sin_addr = ToRouteAddress(destination);
        row.DestinationPrefix.PrefixLength = prefixLength;
        row.NextHop.si_family = AfInet;
        row.NextHop.sin_addr = nextHop is null ? 0 : ToRouteAddress(nextHop);
        row.Protocol = MibIpProtoNetMgmt;
        row.Metric = 1;
        return row;
    }

    /// <summary>
    /// Reinterprets an IPv4 address as the network-order DWORD the forwarding table stores; the
    /// IPAddress bytes are already in network order, matching in_addr.S_addr.
    /// </summary>
    private static uint ToRouteAddress(IPAddress ip)
    {
        return BitConverter.ToUInt32(ip.GetAddressBytes(), 0);
    }

    /// <summary>
    /// Builds an IPv6 MIB_IPFORWARD_ROW2 for a host/prefix route. A null next hop yields an on-link
    /// route (::); otherwise the route is via the given next-hop (which may be a link-local gateway).
    /// </summary>
    private static MIB_IPFORWARD_ROW2 NewRowV6(IPAddress destination, byte prefixLength, uint interfaceIndex, SOCKADDR_INET? nextHop)
    {
        var row = new MIB_IPFORWARD_ROW2();
        InitializeIpForwardEntry(ref row);
        row.InterfaceIndex = interfaceIndex;
        row.DestinationPrefix.Prefix.si_family = AfInet6;
        WriteV6(ref row.DestinationPrefix.Prefix, destination);
        row.DestinationPrefix.PrefixLength = prefixLength;
        if (nextHop is { } hop)
        {
            row.NextHop = hop;
        }
        else
        {
            row.NextHop.si_family = AfInet6; // :: (on-link)
        }

        row.Protocol = MibIpProtoNetMgmt;
        row.Metric = 1;
        return row;
    }

    /// <summary>
    /// Writes an IPv6 address into a SOCKADDR_INET's 16-byte address field. The IPAddress bytes are in
    /// network order; stored as two little-endian ulongs so the native struct holds them byte-for-byte.
    /// </summary>
    private static void WriteV6(ref SOCKADDR_INET sa, IPAddress ip)
    {
        var b = ip.GetAddressBytes();
        sa.sin6_addr_0 = BitConverter.ToUInt32(b, 0);
        sa.sin6_addr_1 = BitConverter.ToUInt32(b, 4);
        sa.sin6_addr_2 = BitConverter.ToUInt32(b, 8);
        sa.sin6_addr_3 = BitConverter.ToUInt32(b, 12);
    }

    private static bool V6Equals(SOCKADDR_INET sa, IPAddress ip)
    {
        var b = ip.GetAddressBytes();
        return sa.sin6_addr_0 == BitConverter.ToUInt32(b, 0)
            && sa.sin6_addr_1 == BitConverter.ToUInt32(b, 4)
            && sa.sin6_addr_2 == BitConverter.ToUInt32(b, 8)
            && sa.sin6_addr_3 == BitConverter.ToUInt32(b, 12);
    }

    /// <summary>
    /// Asks the kernel for the best (longest-prefix-match) route to an IPv6 destination and returns its
    /// outgoing interface and next-hop, or null if there is no v6 route. Called before the tunnel comes
    /// up, so the result is the physical path — used to pin a v6 LAN exclusion off the tunnel.
    /// </summary>
    private static (uint InterfaceIndex, SOCKADDR_INET NextHop)? FindBestV6Route(IPAddress destination)
    {
        var dest = new SOCKADDR_INET { si_family = AfInet6 };
        WriteV6(ref dest, destination);
        var best = new MIB_IPFORWARD_ROW2();
        var bestSource = new SOCKADDR_INET();
        if (GetBestRoute2(IntPtr.Zero, 0, IntPtr.Zero, ref dest, 0, ref best, ref bestSource) != NoError)
        {
            return null;
        }

        return (best.InterfaceIndex, best.NextHop);
    }

    private static void DeleteManagedV6Routes(IPAddress destination, byte prefixLength)
    {
        foreach (var row in ReadForwardTable(AfInet6))
        {
            if (row.DestinationPrefix.Prefix.si_family != AfInet6
                || row.Protocol != MibIpProtoNetMgmt
                || row.DestinationPrefix.PrefixLength != prefixLength
                || !V6Equals(row.DestinationPrefix.Prefix, destination))
            {
                continue;
            }

            var copy = row;
            DeleteIpForwardEntry2(ref copy);
        }
    }

    /// <summary>
    /// Deletes our own management routes to an IPv4 destination (optionally restricted to one
    /// interface) by matching the live routing table, so the exact entry is removed regardless of how
    /// the next hop changed.
    /// </summary>
    private static void DeleteManagedRoutes(IPAddress destination, uint? ifIndex, byte? prefixLength = null)
    {
        var dest = ToRouteAddress(destination);
        foreach (var row in ReadForwardTable(AfInet))
        {
            if (row.DestinationPrefix.Prefix.si_family != AfInet
                || row.DestinationPrefix.Prefix.sin_addr != dest
                || row.Protocol != MibIpProtoNetMgmt)
            {
                continue;
            }

            if (ifIndex is not null && row.InterfaceIndex != ifIndex.Value)
            {
                continue;
            }

            if (prefixLength is not null && row.DestinationPrefix.PrefixLength != prefixLength.Value)
            {
                continue;
            }

            var copy = row;
            DeleteIpForwardEntry2(ref copy);
        }
    }

    private static List<MIB_IPFORWARD_ROW2> ReadForwardTable(ushort family)
    {
        var rows = new List<MIB_IPFORWARD_ROW2>();
        if (GetIpForwardTable2(family, out var table) != NoError || table == IntPtr.Zero)
        {
            return rows;
        }

        try
        {
            // MIB_IPFORWARD_TABLE2: ULONG NumEntries; then (8-aligned) the MIB_IPFORWARD_ROW2 array.
            var count = Marshal.ReadInt32(table);
            var stride = Marshal.SizeOf<MIB_IPFORWARD_ROW2>();
            for (var i = 0; i < count; i++)
            {
                rows.Add(Marshal.PtrToStructure<MIB_IPFORWARD_ROW2>(table + 8 + (i * stride)));
            }
        }
        finally
        {
            FreeMibTable(table);
        }

        return rows;
    }

    private static int? Ipv4Index(NetworkInterface nic)
    {
        try
        {
            return nic.GetIPProperties().GetIPv4Properties()?.Index;
        }
        catch (NetworkInformationException)
        {
            return null;
        }
    }

    private static (IPAddress? Gateway, uint InterfaceIndex) FindPhysicalGateway(IPAddress endpoint)
    {
        var destination = ToRouteAddress(endpoint);
        if (GetBestInterface(destination, out var interfaceIndex) != 0)
        {
            return (null, 0);
        }

        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (Ipv4Index(nic) != interfaceIndex)
            {
                continue;
            }

            var properties = nic.GetIPProperties();
            foreach (var gateway in properties.GatewayAddresses)
            {
                if (gateway.Address.AddressFamily == AddressFamily.InterNetwork)
                {
                    return (gateway.Address, interfaceIndex);
                }
            }
        }

        return (null, 0);
    }

    private static void UpdateState(string name, string endpoint, bool add)
    {
        UpdateStateFile(TunnelPaths.RouteStateFile(name), endpoint, add);
    }

    private static void UpdateStateFile(string path, string entry, bool add)
    {
        var saved = ReadStateFile(path);
        if (add)
        {
            if (!saved.Contains(entry))
            {
                saved.Add(entry);
            }
        }
        else
        {
            saved.Remove(entry);
        }

        if (saved.Count == 0)
        {
            TryDelete(path);
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllLines(path, saved);
    }

    private static List<string> ReadStateFile(string path)
    {
        if (!File.Exists(path))
        {
            return [];
        }

        var saved = new List<string>();
        foreach (var line in File.ReadAllLines(path))
        {
            var endpoint = line.Trim();
            if (endpoint.Length > 0 && !saved.Contains(endpoint))
            {
                saved.Add(endpoint);
            }
        }

        return saved;
    }

    private static void TryDelete(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    // SOCKADDR_INET is the v4/v6 union (28 bytes). The native struct is 4-byte aligned (its address is a
    // UCHAR[16]); holding the v6 address as ulong fields would give THIS struct 8-byte alignment, which
    // shifts every field after InterfaceIndex in the embedding MIB_IPFORWARD_ROW2 — iphlpapi then gets a
    // misaligned row and CreateIpForwardEntry2 fails (breaking the per-domain /32 routes). Represent the
    // v6 address as four uints (4-aligned) so the layout matches native byte-for-byte.
    [StructLayout(LayoutKind.Explicit, Size = 28)]
    private struct SOCKADDR_INET
    {
        [FieldOffset(0)] public ushort si_family;
        [FieldOffset(2)] public ushort sin_port;
        [FieldOffset(4)] public uint sin_addr;        // sockaddr_in.sin_addr (IPv4)
        [FieldOffset(8)] public uint sin6_addr_0;     // sockaddr_in6.sin6_addr bytes 0..3
        [FieldOffset(12)] public uint sin6_addr_1;    // bytes 4..7
        [FieldOffset(16)] public uint sin6_addr_2;    // bytes 8..11
        [FieldOffset(20)] public uint sin6_addr_3;    // bytes 12..15
        [FieldOffset(24)] public uint sin6_scope_id;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IP_ADDRESS_PREFIX
    {
        public SOCKADDR_INET Prefix;
        public byte PrefixLength;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MIB_IPFORWARD_ROW2
    {
        public ulong InterfaceLuid;
        public uint InterfaceIndex;
        public IP_ADDRESS_PREFIX DestinationPrefix;
        public SOCKADDR_INET NextHop;
        public byte SitePrefixLength;
        public uint ValidLifetime;
        public uint PreferredLifetime;
        public uint Metric;
        public uint Protocol;
        public byte Loopback;
        public byte AutoconfigureAddress;
        public byte Publish;
        public byte Immortal;
        public uint Age;
        public uint Origin;
    }

    [LibraryImport("iphlpapi.dll")]
    private static partial uint GetBestInterface(uint destAddr, out uint bestIfIndex);

    [LibraryImport("iphlpapi.dll")]
    private static partial uint GetBestRoute2(IntPtr interfaceLuid, uint interfaceIndex, IntPtr sourceAddress, ref SOCKADDR_INET destinationAddress, uint addressSortOptions, ref MIB_IPFORWARD_ROW2 bestRoute, ref SOCKADDR_INET bestSourceAddress);

    [LibraryImport("iphlpapi.dll")]
    private static partial void InitializeIpForwardEntry(ref MIB_IPFORWARD_ROW2 row);

    [LibraryImport("iphlpapi.dll")]
    private static partial uint CreateIpForwardEntry2(ref MIB_IPFORWARD_ROW2 row);

    [LibraryImport("iphlpapi.dll")]
    private static partial uint DeleteIpForwardEntry2(ref MIB_IPFORWARD_ROW2 row);

    [LibraryImport("iphlpapi.dll")]
    private static partial uint GetIpForwardTable2(ushort family, out IntPtr table);

    [LibraryImport("iphlpapi.dll")]
    private static partial void FreeMibTable(IntPtr table);
}
