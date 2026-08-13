using System.Diagnostics;
using System.Net;
using System.Runtime.InteropServices;
using AmneziaGeo.Ipc;
using AmneziaGeo.Routing;

namespace AmneziaGeo.Windows.App;

/// <summary>
/// Gathers the proxy's open connections into one entry per client and names the applications behind a client of
/// this machine: over loopback every one of them is 127.0.0.1, and only the process holding the port says which.
/// </summary>
internal static class ProxyClientNames
{
    private const int AfInet = 2;
    private const int TcpTableOwnerPidAll = 5;
    private const int LoopbackOctet = 127;

    /// <summary>
    /// One entry per client address, the busiest first.
    /// </summary>
    public static IReadOnlyList<ProxyClientEntry> Describe(IReadOnlyList<ProxyPeer> peers)
    {
        if (peers.Count == 0)
        {
            return [];
        }

        var owners = peers.Any(IsLocal) ? LoopbackOwners() : [];
        var names = new Dictionary<uint, string>();
        var entries = new List<ProxyClientEntry>();
        foreach (var group in peers.GroupBy(peer => peer.Address, StringComparer.Ordinal))
        {
            var behind = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var peer in group)
            {
                if (owners.TryGetValue(peer.Port, out var pid) && Name(names, pid) is { Length: > 0 } name)
                {
                    behind.Add(name);
                }
            }

            entries.Add(new ProxyClientEntry(group.Key, string.Join(", ", behind), group.Count(), group.Min(peer => peer.Since)));
        }

        return [.. entries.OrderByDescending(entry => entry.Connections).ThenBy(entry => entry.Address, StringComparer.Ordinal)];
    }

    private static bool IsLocal(ProxyPeer peer)
    {
        return IPAddress.TryParse(peer.Address, out var address) && IPAddress.IsLoopback(address);
    }

    private static string Name(Dictionary<uint, string> cache, uint pid)
    {
        if (cache.TryGetValue(pid, out var known))
        {
            return known;
        }

        var name = string.Empty;
        try
        {
            using var process = Process.GetProcessById((int)pid);
            name = process.ProcessName;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
        }

        cache[pid] = name;
        return name;
    }

    // The process that holds each loopback port. The client's own row carries it: the port it dialled from is
    // that row's local port.
    private static Dictionary<int, uint> LoopbackOwners()
    {
        var owners = new Dictionary<int, uint>();
        var size = 0;
        GetExtendedTcpTable(IntPtr.Zero, ref size, false, AfInet, TcpTableOwnerPidAll, 0);
        if (size <= 0)
        {
            return owners;
        }

        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            if (GetExtendedTcpTable(buffer, ref size, false, AfInet, TcpTableOwnerPidAll, 0) != 0)
            {
                return owners;
            }

            var count = Marshal.ReadInt32(buffer);
            var basePtr = buffer + 4;
            for (var i = 0; i < count; i++)
            {
                // MIB_TCPROW_OWNER_PID: state, local address at 4, local port at 8, remote pair, pid at 20; 24 bytes a row.
                var row = basePtr + (i * 24);
                if ((Marshal.ReadInt32(row, 4) & 0xFF) != LoopbackOctet)
                {
                    continue;
                }

                owners[Port(Marshal.ReadInt32(row, 8))] = (uint)Marshal.ReadInt32(row, 20);
            }

            return owners;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    // The table keeps ports in network order in the low two bytes.
    private static int Port(int raw)
    {
        return ((raw & 0xFF) << 8) | ((raw >> 8) & 0xFF);
    }

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedTcpTable(IntPtr pTcpTable, ref int pdwSize, [MarshalAs(UnmanagedType.Bool)] bool bOrder, int ulAf, int tableClass, int reserved);
}
