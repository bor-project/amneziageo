using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;

namespace AmneziaGeo.Windows.App;

/// <summary>
/// Resets the connection attempts a fresh tunnel route arrived too late for. Windows picks a socket's source
/// address when connect is called and keeps it: a route installed afterwards moves the packets to the tunnel but
/// leaves the LAN address on them, and the peer drops what falls outside its allowed addresses. The attempt then
/// sits in SYN_SENT for the whole connect timeout. Aborting it makes the app open a new socket, which picks the
/// tunnel address. IPv4 only - Windows exposes no such control for IPv6.
/// </summary>
internal sealed class SynSentReset(string tunnelName, ILogger logger)
{
    private const int AfInet = 2;
    private const int TcpTableOwnerPidAll = 5;
    private const int MibTcpStateSynSent = 3;
    private const int MibTcpStateDeleteTcb = 12;
    // The adapter's own addresses are re-read on this cadence: the tunnel gets its address shortly after it appears.
    private const long LocalTtlMs = 30_000;

    private HashSet<uint> _local = [];
    private long _localStamp;

    /// <summary>
    /// Aborts every half-open connection to these addresses that is not already leaving through the tunnel.
    /// </summary>
    public void Abort(IReadOnlyCollection<IPAddress> addresses)
    {
        if (addresses.Count == 0)
        {
            return;
        }

        var wanted = new HashSet<uint>();
        foreach (var address in addresses)
        {
            if (address.AddressFamily == AddressFamily.InterNetwork)
            {
                wanted.Add(BitConverter.ToUInt32(address.GetAddressBytes(), 0));
            }
        }

        if (wanted.Count == 0)
        {
            return;
        }

        try
        {
            Sweep(wanted);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "the half-open connections to a freshly routed address could not be reset");
        }
    }

    private void Sweep(HashSet<uint> wanted)
    {
        var mine = TunnelAddresses();
        var size = 0;
        GetExtendedTcpTable(IntPtr.Zero, ref size, false, AfInet, TcpTableOwnerPidAll, 0);
        if (size <= 0)
        {
            return;
        }

        var reset = 0;
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            if (GetExtendedTcpTable(buffer, ref size, false, AfInet, TcpTableOwnerPidAll, 0) != 0)
            {
                return;
            }

            var count = Marshal.ReadInt32(buffer);
            var basePtr = buffer + 4;
            for (var i = 0; i < count; i++)
            {
                // MIB_TCPROW_OWNER_PID: state, local addr, local port, remote addr, remote port, pid - each a DWORD.
                var row = basePtr + (i * 24);
                if (Marshal.ReadInt32(row, 0) != MibTcpStateSynSent)
                {
                    continue;
                }

                var localAddr = (uint)Marshal.ReadInt32(row, 4);
                var remoteAddr = (uint)Marshal.ReadInt32(row, 12);
                if (!wanted.Contains(remoteAddr) || mine.Contains(localAddr))
                {
                    continue;
                }

                var entry = new MibTcpRow
                {
                    State = MibTcpStateDeleteTcb,
                    LocalAddr = localAddr,
                    LocalPort = (uint)Marshal.ReadInt32(row, 8),
                    RemoteAddr = remoteAddr,
                    RemotePort = (uint)Marshal.ReadInt32(row, 16),
                };

                if (SetTcpEntry(ref entry) == 0)
                {
                    reset++;
                }
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }

        if (reset > 0)
        {
            logger.LogDebug("{Count} connection attempt(s) that had already left outside the tunnel were dropped, so the app opens them again through it", reset);
        }
    }

    // Addresses of the tunnel adapter; a socket already on one of them was born routed and must be left alone.
    private HashSet<uint> TunnelAddresses()
    {
        var now = Environment.TickCount64;
        if (_localStamp != 0 && now - _localStamp < LocalTtlMs)
        {
            return _local;
        }

        var found = new HashSet<uint>();
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.Name != tunnelName)
                {
                    continue;
                }

                foreach (var unicast in nic.GetIPProperties().UnicastAddresses)
                {
                    if (unicast.Address.AddressFamily == AddressFamily.InterNetwork)
                    {
                        found.Add(BitConverter.ToUInt32(unicast.Address.GetAddressBytes(), 0));
                    }
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "the tunnel adapter's own addresses could not be read");
        }

        _local = found;
        _localStamp = now;
        return found;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MibTcpRow
    {
        public uint State;
        public uint LocalAddr;
        public uint LocalPort;
        public uint RemoteAddr;
        public uint RemotePort;
    }

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedTcpTable(IntPtr pTcpTable, ref int pdwSize, [MarshalAs(UnmanagedType.Bool)] bool bOrder, int ulAf, int tableClass, int reserved);

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint SetTcpEntry(ref MibTcpRow row);
}
