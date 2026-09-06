using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using AmneziaGeo.Routing;

namespace AmneziaGeo.Windows.App;

/// <summary>
/// Reads the live destinations off the system TCP table and attributes each to the process that owns it, so a
/// matched app's destination is never admitted as ordinary traffic. The table lists half-open sockets too, which is
/// exactly where an app dialling a bare address is caught - before its verdict is settled the wrong way.
/// </summary>
internal sealed class TcpTableProbe : ILiveDestinations
{
    private const int AfInet = 2;
    private const int TcpTableOwnerPidAll = 5;
    private const int UdpTableOwnerPid = 1;
    private static readonly uint OwnProcessId = (uint)Environment.ProcessId;

    private volatile Func<IReadOnlyCollection<uint>, HashSet<uint>>? _matchPids;

    /// <summary>
    /// Attaches the app-rule filter over PIDs; without it nothing is attributed to an app.
    /// </summary>
    public void SetAppMatch(Func<IReadOnlyCollection<uint>, HashSet<uint>>? match)
    {
        _matchPids = match;
    }

    /// <summary>
    /// The process that owns one connection, or zero where the table holds none. A session handed over by the
    /// gateway is terminated on the adapter, so the pair it was opened for is the only thing left naming the
    /// program behind it.
    /// </summary>
    public static uint OwnerOf(IPEndPoint local, IPEndPoint remote)
    {
        if (local.AddressFamily != AddressFamily.InterNetwork || remote.AddressFamily != AddressFamily.InterNetwork)
        {
            return 0;
        }

        var wantedLocal = Numeric(local.Address);
        var wantedRemote = Numeric(remote.Address);
        var size = 0;
        GetExtendedTcpTable(IntPtr.Zero, ref size, false, AfInet, TcpTableOwnerPidAll, 0);
        if (size <= 0)
        {
            return 0;
        }

        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            if (GetExtendedTcpTable(buffer, ref size, false, AfInet, TcpTableOwnerPidAll, 0) != 0)
            {
                return 0;
            }

            var count = Marshal.ReadInt32(buffer);
            var basePtr = buffer + 4;
            for (var i = 0; i < count; i++)
            {
                var row = basePtr + (i * 24);
                if (Address(row + 4) != wantedLocal || Port(row + 8) != local.Port
                    || Address(row + 12) != wantedRemote || Port(row + 16) != remote.Port)
                {
                    continue;
                }

                return (uint)Marshal.ReadInt32(row, 20);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }

        return 0;
    }

    // An address of one table row, host order.
    private static uint Address(IntPtr at)
    {
        var octets = new byte[4];
        Marshal.Copy(at, octets, 0, 4);
        return ((uint)octets[0] << 24) | ((uint)octets[1] << 16) | ((uint)octets[2] << 8) | octets[3];
    }

    // A port of one table row; the table holds it in network order inside a dword.
    private static int Port(IntPtr at)
    {
        var raw = Marshal.ReadInt32(at);
        return ((raw & 0xFF) << 8) | ((raw >> 8) & 0xFF);
    }

    // An address in the order the rows are compared in.
    private static uint Numeric(IPAddress address)
    {
        var octets = address.GetAddressBytes();
        return ((uint)octets[0] << 24) | ((uint)octets[1] << 16) | ((uint)octets[2] << 8) | octets[3];
    }

    /// <summary>
    /// The process that holds one datagram socket. The system names no peer for those, so the local port is all
    /// there is to go by; it is enough, because a port belongs to one socket at a time.
    /// </summary>
    public static uint OwnerOfDatagram(IPEndPoint local)
    {
        if (local.AddressFamily != AddressFamily.InterNetwork)
        {
            return 0;
        }

        var size = 0;
        GetExtendedUdpTable(IntPtr.Zero, ref size, false, AfInet, UdpTableOwnerPid, 0);
        if (size <= 0)
        {
            return 0;
        }

        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            if (GetExtendedUdpTable(buffer, ref size, false, AfInet, UdpTableOwnerPid, 0) != 0)
            {
                return 0;
            }

            var count = Marshal.ReadInt32(buffer);
            var basePtr = buffer + 4;
            for (var i = 0; i < count; i++)
            {
                // MIB_UDPROW_OWNER_PID: local address, local port, pid.
                var row = basePtr + (i * 12);
                if (Port(row + 4) != local.Port)
                {
                    continue;
                }

                return (uint)Marshal.ReadInt32(row, 8);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }

        return 0;
    }

    /// <summary>
    /// Remote addresses of every current TCP connection, host order, and those owned by a matched app.
    /// </summary>
    public LiveDestinations Snapshot()
    {
        var remotes = new HashSet<uint>();
        var owners = new List<(uint Pid, uint Address)>();
        var pids = new HashSet<uint>();
        var match = _matchPids;
        var size = 0;
        GetExtendedTcpTable(IntPtr.Zero, ref size, false, AfInet, TcpTableOwnerPidAll, 0);
        if (size <= 0)
        {
            return new LiveDestinations(remotes, []);
        }

        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            if (GetExtendedTcpTable(buffer, ref size, false, AfInet, TcpTableOwnerPidAll, 0) != 0)
            {
                return new LiveDestinations(remotes, []);
            }

            var count = Marshal.ReadInt32(buffer);
            var basePtr = buffer + 4;
            for (var i = 0; i < count; i++)
            {
                // MIB_TCPROW_OWNER_PID: state, local addr, local port, remote addr at offset 12, remote port, pid at 20.
                var row = basePtr + (i * 24);
                var addr = new byte[4];
                Marshal.Copy(row + 12, addr, 0, 4);
                var value = ((uint)addr[0] << 24) | ((uint)addr[1] << 16) | ((uint)addr[2] << 8) | addr[3];
                if (value == 0)
                {
                    continue;
                }

                remotes.Add(value);
                if (match is null)
                {
                    continue;
                }

                var pid = (uint)Marshal.ReadInt32(row, 20);
                if (pid == 0 || pid == OwnProcessId)
                {
                    continue;
                }

                owners.Add((pid, value));
                pids.Add(pid);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }

        return new LiveDestinations(remotes, Attribute(match, owners, pids));
    }

    // Resolves the owning PIDs against the app rules in one pass and collects their destinations.
    private static HashSet<uint> Attribute(Func<IReadOnlyCollection<uint>, HashSet<uint>>? match, List<(uint Pid, uint Address)> owners, HashSet<uint> pids)
    {
        var app = new HashSet<uint>();
        if (match is null || pids.Count == 0)
        {
            return app;
        }

        var matched = match(pids);
        if (matched.Count == 0)
        {
            return app;
        }

        foreach (var (pid, address) in owners)
        {
            if (matched.Contains(pid))
            {
                app.Add(address);
            }
        }

        return app;
    }

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedTcpTable(IntPtr pTcpTable, ref int pdwSize, [MarshalAs(UnmanagedType.Bool)] bool bOrder, int ulAf, int tableClass, int reserved);

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedUdpTable(IntPtr pUdpTable, ref int pdwSize, [MarshalAs(UnmanagedType.Bool)] bool bOrder, int ulAf, int tableClass, int reserved);
}
