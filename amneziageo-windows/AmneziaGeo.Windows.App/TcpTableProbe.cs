using System.Runtime.InteropServices;
using AmneziaGeo.Routing;

namespace AmneziaGeo.Windows.App;

/// <summary>
/// Reads the live destinations off the system TCP table.
/// </summary>
internal sealed class TcpTableProbe : ILiveDestinations
{
    private const int AfInet = 2;
    private const int TcpTableOwnerPidAll = 5;

    /// <summary>
    /// Remote addresses of every current TCP connection, host order.
    /// </summary>
    public HashSet<uint> Snapshot()
    {
        var remotes = new HashSet<uint>();
        var size = 0;
        GetExtendedTcpTable(IntPtr.Zero, ref size, false, AfInet, TcpTableOwnerPidAll, 0);
        if (size <= 0)
        {
            return remotes;
        }

        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            if (GetExtendedTcpTable(buffer, ref size, false, AfInet, TcpTableOwnerPidAll, 0) != 0)
            {
                return remotes;
            }

            var count = Marshal.ReadInt32(buffer);
            var basePtr = buffer + 4;
            for (var i = 0; i < count; i++)
            {
                // MIB_TCPROW_OWNER_PID: state, local addr, local port, remote addr at offset 12, remote port, pid.
                var addr = new byte[4];
                Marshal.Copy(basePtr + (i * 24) + 12, addr, 0, 4);
                var value = ((uint)addr[0] << 24) | ((uint)addr[1] << 16) | ((uint)addr[2] << 8) | addr[3];
                if (value != 0)
                {
                    remotes.Add(value);
                }
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }

        return remotes;
    }

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedTcpTable(IntPtr pTcpTable, ref int pdwSize, [MarshalAs(UnmanagedType.Bool)] bool bOrder, int ulAf, int tableClass, int reserved);
}
