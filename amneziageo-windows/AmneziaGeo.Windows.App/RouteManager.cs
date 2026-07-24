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
    private const uint MibIpProtoNetMgmt = 3; // RouteProtocolNetMgmt

    // Private IPv4 ranges kept off the tunnel in full-tunnel mode.
    private static readonly (string Network, byte Prefix)[] LanExclusions =
    [
        ("10.0.0.0", 8),
        ("172.16.0.0", 12),
        ("192.168.0.0", 16),
    ];

    // IPv6 ULA range kept off the tunnel on a dual-stack tunnel.
    private static readonly (string Network, byte Prefix)[] LanExclusionsV6 =
    [
        ("fc00::", 7),
    ];

    // Tunnel routes this instance installed, so a delete calls DeleteIpForwardEntry2 on the remembered row (O(1))
    // instead of reading and scanning the whole OS forwarding table. The scan stays the fallback for a route we
    // did not install this session (a previous run's) or one the OS has since altered. Guarded by _addedLock.
    private readonly Dictionary<RouteKey, MIB_IPFORWARD_ROW2> _added = [];
    private readonly object _addedLock = new();

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
        var (gateway, interfaceIndex) = FindPhysicalGateway(endpoint);
        if (gateway is null)
        {
            RouteLog.Write("endpoint-excl", $"{endpoint}/32", "physical gw", ok: false, "no gateway found");
            return false;
        }

        var row = NewRow(endpoint, 32, interfaceIndex, gateway);
        var result = CreateIpForwardEntry2(ref row);
        var ok = result is NoError or ErrorObjectAlreadyExists;
        RouteLog.Write("endpoint-excl", $"{endpoint}/32", $"{gateway} if{interfaceIndex}", ok);
        if (ok)
        {
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
        RouteLog.Write("rm endpoint", $"{endpoint}/32", "physical", ok: true);
    }

    /// <summary>
    /// Removes endpoint-exclusion routes left by a previous run. <paramref name="abortIf"/> stands the cleanup
    /// down once a tunnel bring-up is requested, so a boot pass cannot remove a connect's live exclusion.
    /// </summary>
    public void RestoreSavedExclusions(Func<bool>? abortIf = null)
    {
        foreach (var file in TunnelPaths.RouteStateFiles())
        {
            if (abortIf?.Invoke() == true)
            {
                return;
            }

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
    /// Pins LAN ranges to the physical gateway in full-tunnel mode.
    /// </summary>
    public bool AddLanExclusions(string name, bool dualStack, IReadOnlyList<string> extraCidrs)
    {
        var any = false;

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
            RouteLog.Write("lan-excl", $"{dest}/{prefix}", $"{gateway} if{interfaceIndex}", ok);
            if (ok)
            {
                UpdateStateFile(TunnelPaths.LanStateFile(name), $"{dest}/{prefix}", add: true);
                any = true;
            }
        }

        // v6 LAN exclusion on a dual-stack tunnel.
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
                var ok = result is NoError or ErrorObjectAlreadyExists;
                RouteLog.Write("lan-excl6", $"{network}/{prefix}", $"if{best.InterfaceIndex}", ok);
                if (ok)
                {
                    UpdateStateFile(TunnelPaths.LanStateFile(name), $"{network}/{prefix}", add: true);
                    any = true;
                }
            }
        }

        return any;
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
        var result = new List<string>();
        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.OperationalStatus != OperationalStatus.Up
                || ni.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel
                || IsTunnelAdapter(ni))
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

    /// <summary>
    /// Default bypass set: RFC1918 ranges plus connected subnets outside them.
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

    // Whether a CIDR falls inside an RFC1918 default range.
    private static bool IsWithinDefaultRanges(string cidr)
    {
        var slash = cidr.IndexOf('/');
        if (slash < 0 || !IPAddress.TryParse(cidr[..slash], out var addr))
        {
            return false;
        }

        return LanExclusions.Any(r => InRange(addr, IPAddress.Parse(r.Network), r.Prefix));
    }

    // APIPA link-local 169.254/16 has no real LAN.
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
            RouteLog.Write("rm lan-excl", cidr, "physical", ok: true);
        }

        TryDelete(path);
    }

    /// <summary>
    /// Removes LAN-bypass exclusion routes left by a previous run. <paramref name="abortIf"/> stands the cleanup
    /// down once a tunnel bring-up is requested, so a boot pass cannot remove a connect's live exclusions.
    /// </summary>
    public void RestoreSavedLanExclusions(Func<bool>? abortIf = null)
    {
        foreach (var file in TunnelPaths.LanStateFiles())
        {
            if (abortIf?.Invoke() == true)
            {
                return;
            }

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
    /// Adds an on-link host route for an IP through the tunnel interface.
    /// </summary>
    public bool AddTunnelRoute(IPAddress ip, uint tunnelInterfaceIndex)
    {
        var prefix = ip.AddressFamily == AddressFamily.InterNetworkV6 ? 128 : 32;
        var row = ip.AddressFamily == AddressFamily.InterNetworkV6
            ? NewRowV6(ip, 128, tunnelInterfaceIndex, nextHop: null) // on-link (no gateway)
            : NewRow(ip, 32, tunnelInterfaceIndex, nextHop: null);
        var result = CreateIpForwardEntry2(ref row);
        var ok = result is NoError or ErrorObjectAlreadyExists;
        if (ok)
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
            if (okV6)
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
        if (ok)
        {
            Remember(ip, prefix, tunnelInterfaceIndex, row);
        }

        RouteLog.Write("tunnel +cidr", $"{ip}/{prefix}", $"if{tunnelInterfaceIndex}", ok);
        return ok;
    }

    /// <summary>
    /// Removes a host route for an IP from the tunnel interface (v4 /32 or v6 /128).
    /// </summary>
    public void RemoveTunnelRoute(IPAddress ip, uint tunnelInterfaceIndex)
    {
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
    /// Removes host routes for many IPs with one forwarding-table read.
    /// </summary>
    public void RemoveTunnelRoutes(IReadOnlyCollection<IPAddress> ips, uint tunnelInterfaceIndex)
    {
        if (ips.Count == 0)
        {
            return;
        }

        RouteLog.Write("tunnel -hosts", $"{ips.Count} route(s)", $"if{tunnelInterfaceIndex}", ok: true);

        // Fast-path each remembered route; only routes we did not install this session fall through to the scan.
        var v4 = new HashSet<uint>();
        foreach (var ip in ips)
        {
            if (ip.AddressFamily == AddressFamily.InterNetworkV6)
            {
                if (!TryDeleteRemembered(ip, 128, tunnelInterfaceIndex))
                {
                    DeleteManagedV6Routes(ip, 128);
                }
            }
            else if (!TryDeleteRemembered(ip, 32, tunnelInterfaceIndex))
            {
                v4.Add(ToRouteAddress(ip));
            }
        }

        if (v4.Count == 0)
        {
            return;
        }

        // One table read, then delete every matching managed route on this interface.
        foreach (var row in ReadForwardTable(AfInet))
        {
            if (row.DestinationPrefix.Prefix.si_family != AfInet
                || row.Protocol != MibIpProtoNetMgmt
                || row.InterfaceIndex != tunnelInterfaceIndex
                || !v4.Contains(row.DestinationPrefix.Prefix.sin_addr))
            {
                continue;
            }

            var copy = row;
            DeleteIpForwardEntry2(ref copy);
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
