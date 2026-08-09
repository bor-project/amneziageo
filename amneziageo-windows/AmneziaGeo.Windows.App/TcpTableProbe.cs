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
}
