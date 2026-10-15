using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace AmneziaGeo.Windows.App;

/// <summary>
/// Manages tunnel route-table entries via iphlpapi.
/// </summary>
internal sealed partial class RouteManager
{
    private const ushort AfInet = 2;
    private const ushort AfInet6 = 23;
    private const uint NoError = 0;
    private const uint ErrorObjectAlreadyExists = 5010;
    private const uint ErrorNotFound = 1168;
    private const uint MibIpProtoNetMgmt = 3; // RouteProtocolNetMgmt

    // What the route log says of a route that is not the client's to take back.
    private const string StoodBefore = "it stood there before the tunnel, so it stays when the tunnel goes";

    // Connected subnets change only with the host's network configuration, while the enumeration behind them costs
    // hundreds of milliseconds on an adapter- and route-heavy host - and the status snapshot asks every 2 seconds.
    // The TTL is a backstop for a missed change event, not the refresh path.
    private const long LocalSubnetsTtlMs = 60_000;
    private readonly Lock _subnetsLock = new();
    private IReadOnlyList<string>? _localSubnets;
    private long _localSubnetsStamp;
    private int _subnetsWatched;
    // Connected networks with the adapter each is on; dropped together with the local subnets.
    private IReadOnlyList<(string Cidr, uint Index)>? _localNetworks;
    private long _localNetworksStamp;

    // Interface indexes and whether each is an adapter of ours, read again when old or when an index is missing.
    private const long AdapterKindsTtlMs = 30_000;
    private const long AdapterKindsRetryMs = 2_000;
    private readonly Lock _adapterKindsLock = new();
    private Dictionary<uint, bool> _adapterKinds = [];
    private long _adapterKindsStamp;

    // Tunnel routes this instance installed, so a delete calls DeleteIpForwardEntry2 on the remembered row (O(1))
    // instead of reading and scanning the whole OS forwarding table. The scan stays the fallback for a route we
    // did not install this session (a previous run's) or one the OS has since altered. Guarded by _addedLock.
    private readonly Dictionary<RouteKey, MIB_IPFORWARD_ROW2> _added = [];
    private readonly object _addedLock = new();

    // The tunnel adapter's own addresses: Windows keeps the routes to them, so none is put or taken off here.
    private volatile IReadOnlyCollection<IPAddress> _own = [];

    private readonly record struct RouteKey(ushort Family, uint A0, uint A1, uint A2, uint A3, byte Prefix, uint IfIndex);

    private static RouteKey KeyOf(IPAddress ip, byte prefix, uint ifIndex)
    {
        if (ip.AddressFamily == AddressFamily.InterNetworkV6)
        {
            var b = ip.GetAddressBytes();
            return new RouteKey(AfInet6, BitConverter.ToUInt32(b, 0), BitConverter.ToUInt32(b, 4), BitConverter.ToUInt32(b, 8), BitConverter.ToUInt32(b, 12), prefix, ifIndex);
        }

        return new RouteKey(AfInet, ToRouteAddress(ip), 0, 0, 0, prefix, ifIndex);
    }

    private void Remember(IPAddress ip, byte prefix, uint ifIndex, in MIB_IPFORWARD_ROW2 row)
    {
        lock (_addedLock)
        {
            _added[KeyOf(ip, prefix, ifIndex)] = row;
        }
    }

    // Deletes the remembered route for this exact (dest, prefix, interface). Returns false when we did not install
    // it (caller falls back to the table scan) or when the remembered row no longer matches the OS entry.
    private bool TryDeleteRemembered(IPAddress ip, byte prefix, uint ifIndex)
    {
        MIB_IPFORWARD_ROW2 row;
        lock (_addedLock)
        {
            if (!_added.Remove(KeyOf(ip, prefix, ifIndex), out row))
            {
                return false;
            }
        }

        return DeleteIpForwardEntry2(ref row) == NoError;
    }

    /// <summary>
    /// Adds a host route for the endpoint via the physical gateway.
    /// </summary>
    public bool AddEndpointExclusion(string name, IPAddress endpoint)
    {
        using var turn = TakeRoutesTurn();
        var (gateway, interfaceIndex) = FindPhysicalGateway(endpoint);
        if (gateway is null)
        {
            RouteLog.Write("endpoint-excl", $"{endpoint}/32", "physical gw", ok: false, "no gateway found");
            return false;
        }

        var row = NewRow(endpoint, 32, interfaceIndex, gateway);
        var result = CreateIpForwardEntry2(ref row);
        var ok = result is NoError or ErrorObjectAlreadyExists;
        var record = RouteStateFile.Through(endpoint.ToString(), interfaceIndex, gateway.ToString());
        var ours = Ours(result, new Lazy<RouteStateFile.Held>(() => RouteStateFile.Recorded(TunnelPaths.RouteStateFiles())), record);
        RouteLog.Write("endpoint-excl", $"{endpoint}/32", $"{gateway} if{interfaceIndex}", ok, ok && !ours ? StoodBefore : null);
        if (ours)
        {
            PersistStateAdds(TunnelPaths.RouteStateFile(name), [record]);
        }

        return ok;
    }

    /// <summary>
    /// Removes the endpoint host route.
    /// </summary>
    public void RemoveEndpointExclusion(string name, IPAddress endpoint)
    {
        using var turn = TakeRoutesTurn();
        var path = TunnelPaths.RouteStateFile(name);
        var held = RouteStateFile.HeldElsewhere(path, TunnelPaths.RouteStateFiles());
        var entry = endpoint.ToString();
        foreach (var record in RouteStateFile.Read(path).Where(line => RouteStateFile.Entry(line) == entry))
        {
            if (held.Holds(record))
            {
                RouteLog.Write("keep endpoint", $"{endpoint}/32", HopOf(record), ok: true, "another tunnel stands on it");
            }
            else
            {
                var result = DeleteRecorded(record);
                RouteLog.Write("rm endpoint", $"{endpoint}/32", HopOf(record), result is NoError or ErrorNotFound, Said(result));
            }

            UpdateState(name, record, add: false);
        }
    }

    /// <summary>
    /// Removes the endpoint-exclusion routes the named tunnel left.
    /// </summary>
    public void RemoveEndpointExclusions(string name)
    {
        using var turn = TakeRoutesTurn();
        RestoreExclusionFile(TunnelPaths.RouteStateFile(name));
    }

    /// <summary>
    /// Removes endpoint-exclusion routes left by a previous run. <paramref name="abortIf"/> stands the cleanup
    /// down once a tunnel bring-up is requested, so a boot pass cannot remove a connect's live exclusion.
    /// <paramref name="keep"/> names the tunnels whose records the sweep leaves alone.
    /// </summary>
    public void RestoreSavedExclusions(Func<bool>? abortIf = null, IEnumerable<string>? keep = null)
    {
        using var turn = TakeRoutesTurn();
        var owned = TunnelPaths.Owned(keep, TunnelPaths.RouteStateFile);
        foreach (var file in TunnelPaths.RouteStateFiles())
        {
            if (abortIf?.Invoke() == true)
            {
                return;
            }

            if (owned.Contains(file))
            {
                continue;
            }

            RestoreExclusionFile(file);
        }
    }

    // Reverts one endpoint-exclusion record and drops it; a route another tunnel records stays.
    private static void RestoreExclusionFile(string path)
    {
        var held = RouteStateFile.HeldElsewhere(path, TunnelPaths.RouteStateFiles());
        foreach (var record in RouteStateFile.Read(path))
        {
            if (!held.Holds(record))
            {
                DeleteRecorded(record);
            }
        }

        TryDelete(path);
    }

    /// <summary>
    /// Pins LAN ranges to the physical gateway in full-tunnel mode.
    /// </summary>
    public bool AddLanExclusions(string name, bool dualStack, IReadOnlyList<string> extraCidrs)
    {
        using var turn = TakeRoutesTurn();
        var any = false;
        var added = new List<string>();
        var recorded = new Lazy<RouteStateFile.Held>(() => RouteStateFile.Recorded(TunnelPaths.LanStateFiles()));

        // IPv4 bypass CIDRs routed out the physical gateway.
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
            var ok = result is NoError or ErrorObjectAlreadyExists;
            var record = RouteStateFile.Through($"{dest}/{prefix}", interfaceIndex, gateway.ToString());
            var ours = Ours(result, recorded, record);
            RouteLog.Write("lan-excl", $"{dest}/{prefix}", $"{gateway} if{interfaceIndex}", ok, ok && !ours ? StoodBefore : null);
            if (ours)
            {
                added.Add(record);
            }

            any |= ok;
        }

        // v6 LAN exclusion on a dual-stack tunnel: the unique-local subnets this host sits on, not the range
        // they come from - a range-wide pin also takes the addresses another VPN carries.
        if (dualStack)
        {
            foreach (var (dest, prefix) in ScanLocalV6Subnets())
            {
                if (FindBestV6Route(dest) is not { } best)
                {
                    continue;
                }

                var row = NewRowV6(dest, prefix, best.InterfaceIndex, best.NextHop);
                var result = CreateIpForwardEntry2(ref row);
                var ok = result is NoError or ErrorObjectAlreadyExists;
                var record = RouteStateFile.Through($"{dest}/{prefix}", best.InterfaceIndex, ToAddress(best.NextHop).ToString());
                var ours = Ours(result, recorded, record);
                RouteLog.Write("lan-excl6", $"{dest}/{prefix}", $"if{best.InterfaceIndex}", ok, ok && !ours ? StoodBefore : null);
                if (ours)
                {
                    added.Add(record);
                }

                any |= ok;
            }
        }

        PersistStateAdds(TunnelPaths.LanStateFile(name), added);
        return any;
    }

    /// <summary>
    /// Adds a host route for one address through the given physical hop.
    /// </summary>
    public bool AddDirectHost(IPAddress address, IPAddress? gateway, uint interfaceIndex)
    {
        if (address.AddressFamily != AddressFamily.InterNetwork || interfaceIndex == 0)
        {
            return false;
        }

        var row = NewRow(address, 32, interfaceIndex, gateway);
        var result = CreateIpForwardEntry2(ref row);
        var ok = result is NoError or ErrorObjectAlreadyExists;
        if (ok)
        {
            Remember(address, 32, interfaceIndex, row);
        }

        return ok;
    }

    /// <summary>
    /// Removes a host route added by <see cref="AddDirectHost"/>.
    /// </summary>
    public void RemoveDirectHost(IPAddress address, uint interfaceIndex)
    {
        if (!TryDeleteRemembered(address, 32, interfaceIndex))
        {
            DeleteManagedRoutes(address, interfaceIndex, 32);
        }
    }

    /// <summary>
    /// Adds a host route for one address into the gateway adapter.
    /// </summary>
    public bool AddGatewayHost(IPAddress address, uint interfaceIndex, IPAddress hop)
    {
        if (address.AddressFamily != AddressFamily.InterNetwork || interfaceIndex == 0)
        {
            return false;
        }

        var row = NewRow(address, 32, interfaceIndex, hop);
        row.Metric = 0;
        var result = CreateIpForwardEntry2(ref row);
        var ok = result is NoError or ErrorObjectAlreadyExists;
        if (ok)
        {
            Remember(address, 32, interfaceIndex, row);
        }

        RouteLog.Write("gateway +host", $"{address}/32", $"{hop} if{interfaceIndex}", ok);
        return ok;
    }

    /// <summary>
    /// Removes a host route added by <see cref="AddGatewayHost"/>.
    /// </summary>
    public void RemoveGatewayHost(IPAddress address, uint interfaceIndex)
    {
        RemoveDirectHost(address, interfaceIndex);
    }

    /// <summary>
    /// Whether another program keeps this address on a host route of its own through an adapter other than the
    /// tunnel, the way a VPN pins its server to the physical path.
    /// </summary>
    public bool HeldByAnother(IPAddress address, uint? tunnelIndex)
    {
        return HolderOf(address, tunnelIndex) is not null;
    }

    /// <summary>
    /// The adapter of the host route another program keeps this address on; null when there is no such route.
    /// </summary>
    public uint? HolderOf(IPAddress address, uint? tunnelIndex)
    {
        var v6 = address.AddressFamily == AddressFamily.InterNetworkV6;
        var dest = new SOCKADDR_INET { si_family = v6 ? AfInet6 : AfInet };
        if (v6)
        {
            WriteV6(ref dest, address);
        }
        else
        {
            dest.sin_addr = ToRouteAddress(address);
        }

        var best = new MIB_IPFORWARD_ROW2();
        var bestSource = new SOCKADDR_INET();
        if (GetBestRoute2(IntPtr.Zero, 0, IntPtr.Zero, ref dest, 0, ref best, ref bestSource) != NoError)
        {
            return null;
        }

        var found = best.DestinationPrefix.Prefix;
        var same = v6
            ? found.si_family == AfInet6 && V6Equals(found, address)
            : found.si_family == AfInet && found.sin_addr == dest.sin_addr;
        var installed = Installed(address, best.DestinationPrefix.PrefixLength, best.InterfaceIndex);
        return ForeignHostRoute(v6, same, best.DestinationPrefix.PrefixLength, best.InterfaceIndex, tunnelIndex, installed)
            ? best.InterfaceIndex
            : null;
    }

    /// <summary>
    /// Whether a best route is a host route to the address itself that neither the tunnel nor this agent holds.
    /// </summary>
    internal static bool ForeignHostRoute(bool v6, bool sameAddress, byte prefixLength, uint interfaceIndex, uint? tunnelIndex, bool installedHere)
    {
        return sameAddress
            && prefixLength == (v6 ? 128 : 32)
            && interfaceIndex != tunnelIndex
            && !installedHere;
    }

    // Whether this agent installed the route for this exact destination, prefix and interface.
    private bool Installed(IPAddress ip, byte prefix, uint ifIndex)
    {
        lock (_addedLock)
        {
            return _added.ContainsKey(KeyOf(ip, prefix, ifIndex));
        }
    }

    /// <summary>
    /// Physical next hop toward a probe address. Probe with a destination that already routes off-tunnel (the
    /// endpoint), so the answer stays the underlay hop after the tunnel's default halves are installed.
    /// </summary>
    public static (IPAddress? Gateway, uint InterfaceIndex) UnderlayHop(IPAddress probe)
    {
        return FindPhysicalGateway(probe);
    }

    /// <summary>
    /// The gateway the adapter leads out through toward the address; null when it reaches the address on-link or not
    /// at all.
    /// </summary>
    public static IPAddress? GatewayOn(IPAddress address, uint interfaceIndex)
    {
        if (address.AddressFamily != AddressFamily.InterNetwork || interfaceIndex == 0)
        {
            return null;
        }

        var dest = ToSockaddr(address);
        var best = new MIB_IPFORWARD_ROW2();
        var source = new SOCKADDR_INET();
        if (GetBestRoute2(IntPtr.Zero, interfaceIndex, IntPtr.Zero, ref dest, 0, ref best, ref source) != NoError
            || best.InterfaceIndex != interfaceIndex
            || best.NextHop.si_family != AfInet
            || best.NextHop.sin_addr == 0)
        {
            return null;
        }

        return new IPAddress(BitConverter.GetBytes(best.NextHop.sin_addr));
    }

    /// <summary>
    /// The path the machine keeps for an address beside the tunnel; null when only a default route or an adapter of
    /// ours leads there.
    /// </summary>
    public (IPAddress? Gateway, uint InterfaceIndex)? OwnPath(IPAddress address)
    {
        if (address.AddressFamily != AddressFamily.InterNetwork)
        {
            return null;
        }

        var dest = new SOCKADDR_INET { si_family = AfInet, sin_addr = ToRouteAddress(address) };
        var best = new MIB_IPFORWARD_ROW2();
        var bestSource = new SOCKADDR_INET();
        if (GetBestRoute2(IntPtr.Zero, 0, IntPtr.Zero, ref dest, 0, ref best, ref bestSource) != NoError
            || !KeepsOwnPath(best.DestinationPrefix.PrefixLength, IsOwnAdapter(best.InterfaceIndex)))
        {
            return null;
        }

        var gateway = best.NextHop.si_family != AfInet || best.NextHop.sin_addr == 0
            ? null
            : new IPAddress(BitConverter.GetBytes(best.NextHop.sin_addr));
        return (gateway, best.InterfaceIndex);
    }

    /// <summary>
    /// Whether a best route is a path of the machine's own: narrower than a default route and its halves, and not
    /// through an adapter of ours.
    /// </summary>
    internal static bool KeepsOwnPath(byte prefixLength, bool ownAdapter)
    {
        return prefixLength > 1 && !ownAdapter;
    }

    /// <summary>
    /// Whether an adapter that is not ours has a route to the address.
    /// </summary>
    public bool ReachesBeside(IPAddress address)
    {
        if (BestRouteIndex(address, 0) is not { } best)
        {
            return false;
        }

        if (!IsOwnAdapter(best))
        {
            return true;
        }

        foreach (var nic in NetworkAdapters.All())
        {
            if (nic.OperationalStatus != OperationalStatus.Up
                || nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel
                || IsTunnelAdapter(nic)
                || AdapterIndex(nic, address.AddressFamily) is not { } index)
            {
                continue;
            }

            if (BestRouteIndex(address, index) == index)
            {
                return true;
            }
        }

        return false;
    }

    // The interface of the best route to the address, kept to the named interface when one is given.
    private static uint? BestRouteIndex(IPAddress address, uint interfaceIndex)
    {
        var dest = ToSockaddr(address);
        var best = new MIB_IPFORWARD_ROW2();
        var source = new SOCKADDR_INET();
        if (GetBestRoute2(IntPtr.Zero, interfaceIndex, IntPtr.Zero, ref dest, 0, ref best, ref source) != NoError)
        {
            return null;
        }

        return best.InterfaceIndex;
    }

    // The address as the routing calls take it.
    private static SOCKADDR_INET ToSockaddr(IPAddress address)
    {
        if (address.AddressFamily != AddressFamily.InterNetworkV6)
        {
            return new SOCKADDR_INET { si_family = AfInet, sin_addr = ToRouteAddress(address) };
        }

        var bytes = address.GetAddressBytes();
        return new SOCKADDR_INET
        {
            si_family = AfInet6,
            sin6_addr_0 = BitConverter.ToUInt32(bytes, 0),
            sin6_addr_1 = BitConverter.ToUInt32(bytes, 4),
            sin6_addr_2 = BitConverter.ToUInt32(bytes, 8),
            sin6_addr_3 = BitConverter.ToUInt32(bytes, 12),
        };
    }

    // The adapter's interface index for the family.
    private static uint? AdapterIndex(NetworkInterface nic, AddressFamily family)
    {
        try
        {
            var properties = nic.GetIPProperties();
            var index = family == AddressFamily.InterNetworkV6 ? properties.GetIPv6Properties()?.Index : properties.GetIPv4Properties()?.Index;
            return index is { } found ? (uint)found : null;
        }
        catch (NetworkInformationException)
        {
            return null;
        }
    }

    // Whether the interface index is an adapter of ours.
    private bool IsOwnAdapter(uint interfaceIndex)
    {
        var now = Environment.TickCount64;
        lock (_adapterKindsLock)
        {
            var age = now - _adapterKindsStamp;
            var known = _adapterKinds.TryGetValue(interfaceIndex, out var own);
            if (_adapterKindsStamp != 0 && age < (known ? AdapterKindsTtlMs : AdapterKindsRetryMs))
            {
                return known && own;
            }
        }

        var kinds = new Dictionary<uint, bool>();
        foreach (var nic in NetworkAdapters.All())
        {
            if (Ipv4Index(nic) is { } index)
            {
                kinds[(uint)index] = IsTunnelAdapter(nic);
            }
        }

        lock (_adapterKindsLock)
        {
            _adapterKinds = kinds;
            _adapterKindsStamp = now;
        }

        return kinds.TryGetValue(interfaceIndex, out var fresh) && fresh;
    }

    /// <summary>
    /// Returns whether the adapter is one of ours.
    /// </summary>
    public static bool IsTunnelAdapter(NetworkInterface ni)
    {
        return ni.Name.StartsWith("AmneziaGeo", StringComparison.OrdinalIgnoreCase)
            || ni.Description.Contains("WireGuard", StringComparison.OrdinalIgnoreCase)
            || ni.Description.Contains("AmneziaWG", StringComparison.OrdinalIgnoreCase)
            || ni.Description.Contains("Wintun", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Lists connected local IPv4 subnets.
    /// </summary>
    public IReadOnlyList<string> LocalSubnets()
    {
        WatchAddressChanges();
        lock (_subnetsLock)
        {
            if (_localSubnets is not null && Environment.TickCount64 - _localSubnetsStamp < LocalSubnetsTtlMs)
            {
                return _localSubnets;
            }
        }

        // Scanned outside the lock: a concurrent caller repeats the work instead of stalling behind it.
        var scanned = ScanLocalSubnets();
        lock (_subnetsLock)
        {
            _localSubnets = scanned;
            _localSubnetsStamp = Environment.TickCount64;
        }

        return scanned;
    }

    /// <summary>
    /// Whether the address falls inside any of the IPv4 CIDRs.
    /// </summary>
    public static bool IsWithinSubnets(IPAddress address, IReadOnlyList<string> cidrs)
    {
        if (address.AddressFamily != AddressFamily.InterNetwork)
        {
            return false;
        }

        foreach (var cidr in cidrs)
        {
            var slash = cidr.IndexOf('/');
            if (slash <= 0
                || !IPAddress.TryParse(cidr[..slash], out var network)
                || network.AddressFamily != AddressFamily.InterNetwork
                || !int.TryParse(cidr[(slash + 1)..], out var prefix)
                || prefix is < 0 or > 32)
            {
                continue;
            }

            if (InRange(address, network, prefix))
            {
                return true;
            }
        }

        return false;
    }

    // Drops the cached subnets on any address or availability change.
    private void WatchAddressChanges()
    {
        if (Interlocked.Exchange(ref _subnetsWatched, 1) == 1)
        {
            return;
        }

        try
        {
            NetworkChange.NetworkAddressChanged += (_, _) => InvalidateLocalSubnets();
            NetworkChange.NetworkAvailabilityChanged += (_, _) => InvalidateLocalSubnets();
        }
        catch (NetworkInformationException)
        {
            // No change notifications: the TTL alone keeps the cache fresh.
        }
    }

    private void InvalidateLocalSubnets()
    {
        lock (_subnetsLock)
        {
            _localSubnets = null;
            _localNetworks = null;
        }
    }

    /// <summary>
    /// The adapter whose connected network holds the address; null when no connected network does.
    /// </summary>
    public uint? OnLinkIndex(IPAddress address)
    {
        WatchAddressChanges();
        foreach (var (cidr, index) in CachedNetworks() ?? RefreshNetworks())
        {
            if (IsWithinSubnets(address, [cidr]))
            {
                return index;
            }
        }

        return null;
    }

    // The connected networks read before, while they are fresh.
    private IReadOnlyList<(string Cidr, uint Index)>? CachedNetworks()
    {
        lock (_subnetsLock)
        {
            return _localNetworks is not null && Environment.TickCount64 - _localNetworksStamp < LocalSubnetsTtlMs
                ? _localNetworks
                : null;
        }
    }

    // Reads the connected networks again and keeps them.
    private IReadOnlyList<(string Cidr, uint Index)> RefreshNetworks()
    {
        var scanned = ScanLocalNetworks();
        lock (_subnetsLock)
        {
            _localNetworks = scanned;
            _localNetworksStamp = Environment.TickCount64;
        }

        return scanned;
    }

    // Connected IPv4 networks of the adapters that are not ours, each with its adapter.
    private static IReadOnlyList<(string Cidr, uint Index)> ScanLocalNetworks()
    {
        var result = new List<(string Cidr, uint Index)>();
        foreach (var ni in NetworkAdapters.All())
        {
            if (ni.OperationalStatus != OperationalStatus.Up
                || ni.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel
                || IsTunnelAdapter(ni)
                || Ipv4Index(ni) is not { } index)
            {
                continue;
            }

            foreach (var ua in UnicastAddresses(ni))
            {
                var prefix = ua.PrefixLength;
                if (ua.Address.AddressFamily != AddressFamily.InterNetwork || prefix is <= 0 or >= 31)
                {
                    continue;
                }

                var network = NetworkAddress(ua.Address, prefix);
                if (!IsLinkLocal(network, prefix))
                {
                    result.Add(($"{network}/{prefix}", (uint)index));
                }
            }
        }

        return result;
    }

    private static IReadOnlyList<string> ScanLocalSubnets()
    {
        var result = new List<string>();
        foreach (var ni in NetworkAdapters.All())
        {
            if (ni.OperationalStatus != OperationalStatus.Up
                || ni.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel
                || IsTunnelAdapter(ni))
            {
                continue;
            }

            foreach (var ua in UnicastAddresses(ni))
            {
                if (ua.Address.AddressFamily != AddressFamily.InterNetwork)
                {
                    continue;
                }

                var prefix = ua.PrefixLength;
                if (prefix is <= 0 or >= 31)
                {
                    continue; // /31-/32 is host, /0 is default - neither is a LAN
                }

                var network = NetworkAddress(ua.Address, prefix);
                if (IsLinkLocal(network, prefix))
                {
                    continue; // APIPA link-local, no real LAN
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

    // Connected unique-local IPv6 subnets: the v6 counterpart of the connected IPv4 subnets, and the only v6
    // addresses a LAN bypass has any business pinning to the physical link.
    private static IReadOnlyList<(IPAddress Network, byte Prefix)> ScanLocalV6Subnets()
    {
        var result = new List<(IPAddress Network, byte Prefix)>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var ni in NetworkAdapters.All())
        {
            if (ni.OperationalStatus != OperationalStatus.Up
                || ni.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel
                || IsTunnelAdapter(ni))
            {
                continue;
            }

            foreach (var ua in UnicastAddresses(ni))
            {
                if (ua.Address.AddressFamily != AddressFamily.InterNetworkV6 || !IsUniqueLocal(ua.Address))
                {
                    continue;
                }

                var prefix = ua.PrefixLength;
                if (prefix is <= 0 or >= 127)
                {
                    continue; // /127-/128 is host, /0 is default - neither is a LAN
                }

                var network = NetworkAddress(ua.Address, prefix);
                if (seen.Add($"{network}/{prefix}"))
                {
                    result.Add((network, (byte)prefix));
                }
            }
        }

        return result;
    }

    // fc00::/7, the IPv6 range that answers to RFC1918.
    private static bool IsUniqueLocal(IPAddress address)
    {
        return (address.GetAddressBytes()[0] & 0xFE) == 0xFC;
    }

    /// <summary>
    /// Default bypass set: the subnets this host is connected to. A whole private range is never pinned - it
    /// would outrank the default route of another VPN and take its traffic off it, and it would send every
    /// address inside it past the tunnel in the clear.
    /// </summary>
    public IReadOnlyList<string> DefaultExclusionEntries()
    {
        return LocalSubnets();
    }

    // APIPA link-local 169.254/16 has no real LAN.
    private static bool IsLinkLocal(IPAddress network, int prefix)
        => prefix >= 16 && InRange(network, IPAddress.Parse("169.254.0.0"), 16);

    private static IPAddress NetworkAddress(IPAddress ip, int prefix)
    {
        var bytes = ip.GetAddressBytes();
        var mask = PrefixToMask(prefix, bytes.Length);
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
        return PrefixToMask(prefix, 4);
    }

    private static byte[] PrefixToMask(int prefix, int length)
    {
        var mask = new byte[length];
        for (var i = 0; i < prefix && i < length * 8; i++)
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
        using var turn = TakeRoutesTurn();
        RestoreLanFile(TunnelPaths.LanStateFile(name));
    }

    /// <summary>
    /// Removes LAN-bypass exclusion routes left by a previous run. <paramref name="abortIf"/> stands the cleanup
    /// down once a tunnel bring-up is requested, so a boot pass cannot remove a connect's live exclusions.
    /// <paramref name="keep"/> names the tunnels whose records the sweep leaves alone.
    /// </summary>
    public void RestoreSavedLanExclusions(Func<bool>? abortIf = null, IEnumerable<string>? keep = null)
    {
        using var turn = TakeRoutesTurn();
        var owned = TunnelPaths.Owned(keep, TunnelPaths.LanStateFile);
        foreach (var file in TunnelPaths.LanStateFiles())
        {
            if (abortIf?.Invoke() == true)
            {
                return;
            }

            if (owned.Contains(file))
            {
                continue;
            }

            RestoreLanFile(file);
        }
    }

    // Reverts one LAN-bypass record and drops it; a route another tunnel records stays.
    private static void RestoreLanFile(string path)
    {
        var held = RouteStateFile.HeldElsewhere(path, TunnelPaths.LanStateFiles());
        var kept = 0;
        foreach (var record in RouteStateFile.Read(path))
        {
            if (held.Holds(record))
            {
                kept++;
                continue;
            }

            var result = DeleteRecorded(record);
            RouteLog.Write("rm lan-excl", RouteStateFile.Entry(record), HopOf(record), result is NoError or ErrorNotFound, Said(result));
        }

        if (kept > 0)
        {
            RouteLog.Write("keep lan-excl", $"{kept} route(s)", "physical", ok: true, "another tunnel stands on them");
        }

        TryDelete(path);
    }

    /// <summary>
    /// Whether a route asked for is the client's to take back: one it put, or one already there that a tunnel of
    /// the client records.
    /// </summary>
    internal static bool Ours(uint result, Lazy<RouteStateFile.Held> recorded, string record)
    {
        return result == NoError || (result == ErrorObjectAlreadyExists && recorded.Value.Holds(record));
    }

    // Takes back the route a record names.
    private static uint DeleteRecorded(string record)
    {
        var entry = RouteStateFile.Entry(record);
        if (RouteStateFile.Hop(record) is not { } through || !IPAddress.TryParse(through.Hop, out var hop))
        {
            DeleteCidrRoute(entry);
            return NoError;
        }

        var slash = entry.IndexOf('/');
        if (!IPAddress.TryParse(slash >= 0 ? entry[..slash] : entry, out var ip) || ip.AddressFamily != hop.AddressFamily)
        {
            return ErrorNotFound;
        }

        var v6 = ip.AddressFamily == AddressFamily.InterNetworkV6;
        var prefix = slash >= 0 && byte.TryParse(entry[(slash + 1)..], out var p) ? p : (byte)(v6 ? 128 : 32);
        var row = v6 ? NewRowV6(ip, prefix, through.Interface, ToHop(hop)) : NewRow(ip, prefix, through.Interface, hop);
        return DeleteIpForwardEntry2(ref row);
    }

    // The hop of a record as the route log words it.
    private static string HopOf(string record)
    {
        return RouteStateFile.Hop(record) is { } through ? $"{through.Hop} if{through.Interface}" : "physical";
    }

    // What the route log adds about a route that was not taken back.
    private static string? Said(uint result)
    {
        return result switch
        {
            NoError => null,
            ErrorNotFound => "it was gone already",
            _ => $"error {result}",
        };
    }

    // The hop of a record as the routing calls take it.
    private static SOCKADDR_INET ToHop(IPAddress hop)
    {
        var address = ToSockaddr(hop);
        address.sin6_scope_id = (uint)hop.ScopeId;
        return address;
    }

    // The address the routing calls gave.
    private static IPAddress ToAddress(SOCKADDR_INET address)
    {
        if (address.si_family == AfInet)
        {
            return new IPAddress(BitConverter.GetBytes(address.sin_addr));
        }

        var bytes = new byte[16];
        BitConverter.TryWriteBytes(bytes.AsSpan(0), address.sin6_addr_0);
        BitConverter.TryWriteBytes(bytes.AsSpan(4), address.sin6_addr_1);
        BitConverter.TryWriteBytes(bytes.AsSpan(8), address.sin6_addr_2);
        BitConverter.TryWriteBytes(bytes.AsSpan(12), address.sin6_addr_3);
        return new IPAddress(bytes, address.sin6_scope_id);
    }

    private static void DeleteCidrRoute(string cidr)
    {
        var slash = cidr.IndexOf('/');
        var network = slash >= 0 ? cidr[..slash] : cidr;
        if (!IPAddress.TryParse(network, out var ip))
        {
            return;
        }

        // Match prefix length to avoid over-deleting a same-network route at a different prefix.
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
    /// Names the tunnel adapter's own addresses, whose routes are left to Windows.
    /// </summary>
    public void LeaveToSystem(IReadOnlyCollection<IPAddress> addresses)
    {
        _own = [.. addresses];
    }

    /// <summary>
    /// Whether the address is one of the tunnel adapter's own.
    /// </summary>
    internal static bool Own(IPAddress address, IReadOnlyCollection<IPAddress> own)
    {
        var plain = address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;
        return own.Contains(plain);
    }

    /// <summary>
    /// Whether a route that was already there is one a client put, not one Windows keeps to an address of its own.
    /// </summary>
    internal static bool Managed(uint protocol)
    {
        return protocol == MibIpProtoNetMgmt;
    }

    // The protocol of the route standing where the given one was to go; 0 when it cannot be read.
    private static uint ProtocolThere(MIB_IPFORWARD_ROW2 row)
    {
        return GetIpForwardEntry2(ref row) == NoError ? row.Protocol : 0;
    }

    /// <summary>
    /// Adds an on-link host route for an IP through the tunnel interface.
    /// </summary>
    public bool AddTunnelRoute(IPAddress ip, uint tunnelInterfaceIndex)
    {
        if (Own(ip, _own))
        {
            return true;
        }

        var prefix = ip.AddressFamily == AddressFamily.InterNetworkV6 ? 128 : 32;
        var row = ip.AddressFamily == AddressFamily.InterNetworkV6
            ? NewRowV6(ip, 128, tunnelInterfaceIndex, nextHop: null) // on-link (no gateway)
            : NewRow(ip, 32, tunnelInterfaceIndex, nextHop: null);
        var result = CreateIpForwardEntry2(ref row);
        var ok = result is NoError or ErrorObjectAlreadyExists;
        if (result == NoError || (ok && Managed(ProtocolThere(row))))
        {
            Remember(ip, (byte)prefix, tunnelInterfaceIndex, row);
        }

        RouteLog.Write("tunnel +host", $"{ip}/{prefix}", $"if{tunnelInterfaceIndex}", ok);
        return ok;
    }

    /// <summary>
    /// Adds an on-link prefix route for a CIDR through the tunnel interface.
    /// </summary>
    public bool AddTunnelCidr(string cidr, uint tunnelInterfaceIndex)
    {
        var slash = cidr.IndexOf('/');
        var network = slash >= 0 ? cidr[..slash] : cidr;
        if (!IPAddress.TryParse(network, out var ip))
        {
            return false;
        }

        if (ip.AddressFamily == AddressFamily.InterNetworkV6)
        {
            var prefixV6 = slash >= 0 && byte.TryParse(cidr[(slash + 1)..], out var pv6) ? pv6 : (byte)128;
            var rowV6 = NewRowV6(ip, prefixV6, tunnelInterfaceIndex, nextHop: null);
            var resultV6 = CreateIpForwardEntry2(ref rowV6);
            var okV6 = resultV6 is NoError or ErrorObjectAlreadyExists;
            if (resultV6 == NoError || (okV6 && Managed(ProtocolThere(rowV6))))
            {
                Remember(ip, prefixV6, tunnelInterfaceIndex, rowV6);
            }

            RouteLog.Write("tunnel +cidr", $"{ip}/{prefixV6}", $"if{tunnelInterfaceIndex}", okV6);
            return okV6;
        }

        if (ip.AddressFamily != AddressFamily.InterNetwork)
        {
            return false;
        }

        var prefix = slash >= 0 && byte.TryParse(cidr[(slash + 1)..], out var p) ? p : (byte)32;
        var row = NewRow(ip, prefix, tunnelInterfaceIndex, nextHop: null);
        var result = CreateIpForwardEntry2(ref row);
        var ok = result is NoError or ErrorObjectAlreadyExists;
        if (result == NoError || (ok && Managed(ProtocolThere(row))))
        {
            Remember(ip, prefix, tunnelInterfaceIndex, row);
        }

        RouteLog.Write("tunnel +cidr", $"{ip}/{prefix}", $"if{tunnelInterfaceIndex}", ok);
        return ok;
    }

    /// <summary>
    /// Adds a default route through an adapter at a metric nothing on this machine takes. Sharing looks for a way
    /// out only on the connection the access point was raised over, so without one it drops what its clients send
    /// anywhere but the addresses routed into that connection.
    /// </summary>
    public bool AddCarriedDefault(uint interfaceIndex, IPAddress nextHop, uint metric)
    {
        var row = NewRow(IPAddress.Any, 0, interfaceIndex, nextHop);
        row.Metric = metric;
        var result = CreateIpForwardEntry2(ref row);
        var ok = result is NoError or ErrorObjectAlreadyExists;
        if (ok)
        {
            Remember(IPAddress.Any, 0, interfaceIndex, row);
        }

        RouteLog.Write("carried +default", $"0.0.0.0/0 metric {metric}", $"if{interfaceIndex}", ok);
        return ok;
    }

    /// <summary>
    /// Removes the default route that carried the clients of the access point.
    /// </summary>
    public void RemoveCarriedDefault(uint interfaceIndex)
    {
        if (!TryDeleteRemembered(IPAddress.Any, 0, interfaceIndex))
        {
            DeleteManagedRoutes(IPAddress.Any, interfaceIndex, 0);
        }

        RouteLog.Write("carried -default", "0.0.0.0/0", $"if{interfaceIndex}", ok: true);
    }

    /// <summary>
    /// Removes a host route for an IP from the tunnel interface (v4 /32 or v6 /128).
    /// </summary>
    public void RemoveTunnelRoute(IPAddress ip, uint tunnelInterfaceIndex)
    {
        if (Own(ip, _own))
        {
            return;
        }

        if (ip.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if (!TryDeleteRemembered(ip, 128, tunnelInterfaceIndex))
            {
                DeleteManagedV6Routes(ip, 128);
            }

            RouteLog.Write("tunnel -host", $"{ip}/128", $"if{tunnelInterfaceIndex}", ok: true);
            return;
        }

        if (!TryDeleteRemembered(ip, 32, tunnelInterfaceIndex))
        {
            DeleteManagedRoutes(ip, tunnelInterfaceIndex);
        }

        RouteLog.Write("tunnel -host", $"{ip}/32", $"if{tunnelInterfaceIndex}", ok: true);
    }

    /// <summary>
    /// Removes host routes for many IPs.
    /// </summary>
    public void RemoveTunnelRoutes(IReadOnlyCollection<IPAddress> ips, uint tunnelInterfaceIndex)
    {
        if (ips.Count == 0)
        {
            return;
        }

        RouteLog.Write("tunnel -hosts", $"{ips.Count} route(s)", $"if{tunnelInterfaceIndex}", ok: true);

        // Fast-path each remembered route; routes we did not install this session fall through to a targeted lookup.
        foreach (var ip in ips)
        {
            if (Own(ip, _own))
            {
                continue;
            }

            if (ip.AddressFamily == AddressFamily.InterNetworkV6)
            {
                if (!TryDeleteRemembered(ip, 128, tunnelInterfaceIndex))
                {
                    DeleteManagedV6Routes(ip, 128);
                }
            }
            else if (ip.AddressFamily == AddressFamily.InterNetwork && !TryDeleteRemembered(ip, 32, tunnelInterfaceIndex))
            {
                DeleteManagedRoutes(ip, tunnelInterfaceIndex, 32);
            }
        }
    }

    /// <summary>
    /// Removes the on-link prefix route for a CIDR from the tunnel interface (only our managed route with the
    /// exact destination + prefix on this interface is deleted).
    /// </summary>
    public void RemoveTunnelCidr(string cidr, uint tunnelInterfaceIndex)
    {
        var slash = cidr.IndexOf('/');
        var network = slash >= 0 ? cidr[..slash] : cidr;
        if (!IPAddress.TryParse(network, out var ip))
        {
            return;
        }

        if (ip.AddressFamily == AddressFamily.InterNetworkV6)
        {
            var prefixV6 = slash >= 0 && byte.TryParse(cidr[(slash + 1)..], out var pv6) ? pv6 : (byte)128;
            if (!TryDeleteRemembered(ip, prefixV6, tunnelInterfaceIndex))
            {
                DeleteManagedV6Routes(ip, prefixV6);
            }

            RouteLog.Write("tunnel -cidr", $"{ip}/{prefixV6}", $"if{tunnelInterfaceIndex}", ok: true);
            return;
        }

        if (ip.AddressFamily != AddressFamily.InterNetwork)
        {
            return;
        }

        var prefix = slash >= 0 && byte.TryParse(cidr[(slash + 1)..], out var p) ? p : (byte)32;
        if (!TryDeleteRemembered(ip, prefix, tunnelInterfaceIndex))
        {
            DeleteManagedRoutes(ip, tunnelInterfaceIndex, prefix);
        }

        RouteLog.Write("tunnel -cidr", $"{ip}/{prefix}", $"if{tunnelInterfaceIndex}", ok: true);
    }

    /// <summary>
    /// Whether the adapter carries on what it receives for another one. Windows turns this on per adapter
    /// and the managed reading answers the machine-wide switch instead, which is off here.
    /// </summary>
    public bool Forwards(uint interfaceIndex)
    {
        var row = new MIB_IPINTERFACE_ROW
        {
            Family = AfInet,
            InterfaceIndex = interfaceIndex,
        };
        return GetIpInterfaceEntry(ref row) == NoError && row.ForwardingEnabled != 0;
    }

    /// <summary>
    /// Returns the IPv4 interface index of a tunnel, by the name of its configuration.
    /// </summary>
    public uint? FindTunnelIndex(string tunnelName)
    {
        return FindInterfaceIndex(TunnelDevice.NameOf(tunnelName));
    }

    /// <summary>
    /// Returns whether the tunnel adapter has addresses and none of them is still in duplicate address detection.
    /// </summary>
    public bool TunnelAddressesSettled(string tunnelName)
    {
        var adapterName = TunnelDevice.NameOf(tunnelName);
        foreach (var nic in NetworkAdapters.All())
        {
            if (nic.Name != adapterName)
            {
                continue;
            }

            var addresses = UnicastAddresses(nic);
            foreach (var address in addresses)
            {
                if (address.DuplicateAddressDetectionState == DuplicateAddressDetectionState.Tentative)
                {
                    return false;
                }
            }

            return addresses.Count > 0;
        }

        return false;
    }

    /// <summary>
    /// Returns the IPv4 interface index of a network adapter by name.
    /// </summary>
    public uint? FindInterfaceIndex(string adapterName)
    {
        foreach (var nic in NetworkAdapters.All())
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

    private static uint ToRouteAddress(IPAddress ip)
    {
        return BitConverter.ToUInt32(ip.GetAddressBytes(), 0);
    }

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
        var dest = new SOCKADDR_INET { si_family = AfInet6 };
        WriteV6(ref dest, destination);
        var best = new MIB_IPFORWARD_ROW2();
        var bestSource = new SOCKADDR_INET();
        if (GetBestRoute2(IntPtr.Zero, 0, IntPtr.Zero, ref dest, 0, ref best, ref bestSource) != NoError)
        {
            return;
        }

        if (best.DestinationPrefix.Prefix.si_family != AfInet6
            || best.Protocol != MibIpProtoNetMgmt
            || best.DestinationPrefix.PrefixLength != prefixLength
            || !V6Equals(best.DestinationPrefix.Prefix, destination))
        {
            return;
        }

        DeleteIpForwardEntry2(ref best);
    }

    // Deletes our managed route to a destination via one GetBestRoute2 lookup, avoiding the GetIpForwardTable2 table
    // read that stalls in session 0. The Protocol==NetMgmt and destination match keep it off non-managed routes.
    private static void DeleteManagedRoutes(IPAddress destination, uint? ifIndex, byte? prefixLength = null)
    {
        var target = ToRouteAddress(destination);
        var dest = new SOCKADDR_INET { si_family = AfInet, sin_addr = target };
        var best = new MIB_IPFORWARD_ROW2();
        var bestSource = new SOCKADDR_INET();
        if (GetBestRoute2(IntPtr.Zero, 0, IntPtr.Zero, ref dest, 0, ref best, ref bestSource) != NoError)
        {
            return;
        }

        if (best.DestinationPrefix.Prefix.si_family != AfInet
            || best.DestinationPrefix.Prefix.sin_addr != target
            || best.Protocol != MibIpProtoNetMgmt)
        {
            return;
        }

        if (ifIndex is not null && best.InterfaceIndex != ifIndex.Value)
        {
            return;
        }

        if (prefixLength is not null && best.DestinationPrefix.PrefixLength != prefixLength.Value)
        {
            return;
        }

        DeleteIpForwardEntry2(ref best);
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

    // Adapter unicast addresses, empty when the adapter can't be queried.
    private static IReadOnlyList<UnicastIPAddressInformation> UnicastAddresses(NetworkInterface nic)
    {
        try
        {
            return [.. nic.GetIPProperties().UnicastAddresses];
        }
        catch (NetworkInformationException)
        {
            return [];
        }
    }

    // Physical next hop for a destination read straight from the OS routing table in one native lookup, skipping
    // the GetAllNetworkInterfaces + per-adapter GetIPProperties enumeration that stalls on adapter-heavy hosts.
    private static (IPAddress? Gateway, uint InterfaceIndex) FindPhysicalGateway(IPAddress endpoint)
    {
        var dest = new SOCKADDR_INET { si_family = AfInet, sin_addr = ToRouteAddress(endpoint) };
        var best = new MIB_IPFORWARD_ROW2();
        var bestSource = new SOCKADDR_INET();
        if (GetBestRoute2(IntPtr.Zero, 0, IntPtr.Zero, ref dest, 0, ref best, ref bestSource) != NoError)
        {
            return (null, 0);
        }

        // NextHop 0.0.0.0 = on-link: the destination is directly reachable and needs no gateway route.
        if (best.NextHop.si_family != AfInet || best.NextHop.sin_addr == 0)
        {
            return (null, best.InterfaceIndex);
        }

        return (new IPAddress(BitConverter.GetBytes(best.NextHop.sin_addr)), best.InterfaceIndex);
    }

    private static void UpdateState(string name, string endpoint, bool add)
    {
        RouteStateFile.Update(TunnelPaths.RouteStateFile(name), endpoint, add);
    }

    // How long a tunnel waits for another one to finish with the routes they share.
    private static readonly TimeSpan RoutesTurnBudget = TimeSpan.FromSeconds(15);

    // Takes the turn at the routes the tunnels of the machine share.
    private static TunnelTurn TakeRoutesTurn()
    {
        var path = TunnelPaths.RoutesTurnFile();
        var turn = TunnelTurn.TakeAsync(path, RoutesTurnBudget).GetAwaiter().GetResult();
        if (!turn.Held)
        {
            RouteLog.Write("routes-turn", path, "not waited for", ok: false, "another tunnel has held the shared routes too long");
        }

        return turn;
    }

    private const int StateWriteBudgetMs = 2000;

    // Persists added exclusions to the state file off the connect thread, blocking it only a short bound.
    private static void PersistStateAdds(string path, IReadOnlyCollection<string> entries)
    {
        RouteStateFile.Add(path, entries, StateWriteBudgetMs, late => RouteLog.Write("state-add", path, "deferred persist", ok: false, late.Message));
    }

    // Removes a state file.
    private static void TryDelete(string path)
    {
        if (!RouteStateFile.Delete(path))
        {
            RouteLog.Write("state-del", path, "held by another process", ok: false, "left to the next cleanup");
        }
    }

    // v6 address as four uints (4-aligned) to match the native struct layout; ulong fields would misalign the row.
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

    // Only the fields this reads are named, at the offsets the row has always carried them; the room to spare
    // takes a longer row from a newer Windows without touching what is around it.
    [StructLayout(LayoutKind.Explicit, Size = 256)]
    private struct MIB_IPINTERFACE_ROW
    {
        [FieldOffset(0)] public ushort Family;
        [FieldOffset(16)] public uint InterfaceIndex;
        [FieldOffset(41)] public byte ForwardingEnabled;
    }

    [LibraryImport("iphlpapi.dll")]
    private static partial uint GetIpInterfaceEntry(ref MIB_IPINTERFACE_ROW row);

    [LibraryImport("iphlpapi.dll")]
    private static partial uint GetBestRoute2(IntPtr interfaceLuid, uint interfaceIndex, IntPtr sourceAddress, ref SOCKADDR_INET destinationAddress, uint addressSortOptions, ref MIB_IPFORWARD_ROW2 bestRoute, ref SOCKADDR_INET bestSourceAddress);

    [LibraryImport("iphlpapi.dll")]
    private static partial void InitializeIpForwardEntry(ref MIB_IPFORWARD_ROW2 row);

    [LibraryImport("iphlpapi.dll")]
    private static partial uint CreateIpForwardEntry2(ref MIB_IPFORWARD_ROW2 row);

    [LibraryImport("iphlpapi.dll")]
    private static partial uint DeleteIpForwardEntry2(ref MIB_IPFORWARD_ROW2 row);

    [LibraryImport("iphlpapi.dll")]
    private static partial uint GetIpForwardEntry2(ref MIB_IPFORWARD_ROW2 row);
}
