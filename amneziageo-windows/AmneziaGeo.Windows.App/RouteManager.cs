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
    private const uint NoError = 0;
    private const uint ErrorObjectAlreadyExists = 5010;
    private const uint MibIpProtoNetMgmt = 3; // NL_ROUTE_PROTOCOL RouteProtocolNetMgmt — tags our routes

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
    /// Adds an on-link host route for an IP through the tunnel interface. IPv6 is a no-op: these
    /// tunnels are IPv4-only and never carry IPv6 traffic.
    /// </summary>
    public bool AddTunnelRoute(IPAddress ip, uint tunnelInterfaceIndex)
    {
        if (ip.AddressFamily == AddressFamily.InterNetworkV6)
        {
            return false;
        }

        var row = NewRow(ip, 32, tunnelInterfaceIndex, nextHop: null); // on-link (no gateway)
        var result = CreateIpForwardEntry2(ref row);
        return result is NoError or ErrorObjectAlreadyExists;
    }

    /// <summary>
    /// Removes a host route for an IP from the tunnel interface. IPv6 is a no-op.
    /// </summary>
    public void RemoveTunnelRoute(IPAddress ip, uint tunnelInterfaceIndex)
    {
        if (ip.AddressFamily == AddressFamily.InterNetworkV6)
        {
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
    /// Deletes our own management routes to an IPv4 destination (optionally restricted to one
    /// interface) by matching the live routing table, so the exact entry is removed regardless of how
    /// the next hop changed.
    /// </summary>
    private static void DeleteManagedRoutes(IPAddress destination, uint? ifIndex)
    {
        var dest = ToRouteAddress(destination);
        foreach (var row in ReadForwardTable())
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

            var copy = row;
            DeleteIpForwardEntry2(ref copy);
        }
    }

    private static List<MIB_IPFORWARD_ROW2> ReadForwardTable()
    {
        var rows = new List<MIB_IPFORWARD_ROW2>();
        if (GetIpForwardTable2(AfInet, out var table) != NoError || table == IntPtr.Zero)
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
        var path = TunnelPaths.RouteStateFile(name);
        var saved = ReadStateFile(path);
        if (add)
        {
            if (!saved.Contains(endpoint))
            {
                saved.Add(endpoint);
            }
        }
        else
        {
            saved.Remove(endpoint);
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

    [StructLayout(LayoutKind.Sequential, Size = 28)]
    private struct SOCKADDR_INET
    {
        public ushort si_family;
        public ushort sin_port;
        public uint sin_addr;
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
