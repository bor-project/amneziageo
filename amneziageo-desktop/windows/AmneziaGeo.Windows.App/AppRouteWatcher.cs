using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;

namespace AmneziaGeo.Windows.App;

/// <summary>
/// Per-app split tunneling by owning process.
/// </summary>
internal sealed class AppRouteWatcher
{
    // Poll ~1s: routes within a TCP retry.
    private static readonly TimeSpan _pollInterval = TimeSpan.FromSeconds(1);

    // Cap the ancestry walk.
    private const int MaxAncestryDepth = 8;

    // LISTEN state has no remote peer.
    private const uint MibTcpStateListen = 2;
    private const int AfInet = 2;
    private const int TcpTableOwnerPidAll = 5;

    private readonly DomainTracker _tracker;
    private readonly ILogger _logger;

    // Parsed matchers. pkg= not matched in v1.
    private readonly HashSet<string> _paths = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _dirs = [];
    private readonly HashSet<string> _names = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _services = [];

    // Log each remote once per session.
    private readonly HashSet<string> _loggedRemotes = new(StringComparer.Ordinal);

    // Stop the ancestry walk at generic hosts so an app rule stays scoped to its own tree.
    private static readonly HashSet<string> _ancestryStops = new(StringComparer.OrdinalIgnoreCase)
    {
        "explorer.exe", "services.exe", "svchost.exe", "wininit.exe", "winlogon.exe",
        "userinit.exe", "smss.exe", "csrss.exe", "lsass.exe",
    };

    /// <summary>
    /// ctor
    /// </summary>
    public AppRouteWatcher(DomainTracker tracker, IReadOnlyList<string> matchers, ILogger logger)
    {
        _tracker = tracker;
        _logger = logger;

        foreach (var raw in matchers)
        {
            var token = raw.Trim();
            var eq = token.IndexOf('=');
            if (eq <= 0)
            {
                // Bare value: treat as a full path.
                _paths.Add(AmneziaGeo.Ipc.AppPathToken.Tokenize(token));
                continue;
            }

            var kind = token[..eq].Trim().ToLowerInvariant();
            var value = token[(eq + 1)..].Trim();
            if (value.Length == 0)
            {
                continue;
            }

            switch (kind)
            {
                case "path":
                    _paths.Add(AmneziaGeo.Ipc.AppPathToken.Tokenize(value));
                    break;
                case "dir":
                    _dirs.Add(AmneziaGeo.Ipc.AppPathToken.Tokenize(value.TrimEnd('\\', '/')));
                    break;
                case "name":
                    _names.Add(value);
                    break;
                case "svc":
                    _services.Add(value);
                    break;
                default:
                    // pkg= (UWP) and unknown kinds: not matched by image path.
                    _logger.LogInformation("app route watcher: matcher kind '{Kind}' is not supported yet; ignored", kind);
                    break;
            }
        }
    }

    /// <summary>
    /// Whether a PID matches the app rules.
    /// </summary>
    internal bool MatchesPid(uint pid) => ResolveServicePids().Contains(pid) || MatchesByImageOrAncestor(pid, SnapshotProcessTree(), new Dictionary<uint, (string? Path, long Created)>());

    /// <summary>
    /// Has any matcher.
    /// </summary>
    public bool HasMatchers => _paths.Count > 0 || _dirs.Count > 0 || _names.Count > 0 || _services.Count > 0;

    /// <summary>
    /// Poll and route matched apps' remotes.
    /// </summary>
    public async Task RunAsync(CancellationToken ct)
    {
        _logger.LogInformation(
            "app route watcher started: paths={Paths} dirs={Dirs} names={Names} services={Services}",
            _paths.Count, _dirs.Count, _names.Count, _services.Count);

        try
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    Tick();
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "app route watcher tick failed");
                }

                await Task.Delay(_pollInterval, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Tunnel torn down.
        }
    }

    private void Tick()
    {
        // Re-resolve service PIDs each tick (services restart).
        var servicePids = ResolveServicePids();

        // One process-tree snapshot per tick for ancestry matching.
        var tree = SnapshotProcessTree();

        // Per-tick pid -> (path, creation) cache; per-tick lifetime avoids cross-tick PID-reuse staleness.
        var procCache = new Dictionary<uint, (string? Path, long Created)>();

        // Per-tick decision cache.
        var decision = new Dictionary<uint, bool>();
        var matchedIps = new List<string>();

        foreach (var (remote, pid) in EnumerateTcpConnections())
        {
            if (!decision.TryGetValue(pid, out var matched))
            {
                matched = servicePids.Contains(pid) || MatchesByImageOrAncestor(pid, tree, procCache);
                decision[pid] = matched;
            }

            if (matched)
            {
                var key = remote.ToString();
                matchedIps.Add(key);

                // Log each new matched remote once.
                if (_loggedRemotes.Count >= 65536)
                {
                    _loggedRemotes.Clear();
                }

                if (_loggedRemotes.Add(key))
                {
                    _logger.LogTrace("tcp request -> {Remote} (pid {Pid})", remote, pid);
                    if (RouteLog.Enabled)
                    {
                        RouteLog.Note($"tcp request -> {remote} (pid {pid})");
                    }
                }
            }
        }

        if (matchedIps.Count > 0)
        {
            _tracker.UpdateAppIps(matchedIps);
        }
    }

    // Match the owning image, or any ancestor's. WebView2/Electron/UWP apps run their networking in a
    // shared child process whose own image sits outside the app; the rule matches up the parent chain.
    private bool MatchesByImageOrAncestor(uint pid, IReadOnlyDictionary<uint, (uint Parent, string Name)> tree, Dictionary<uint, (string? Path, long Created)> cache)
    {
        var current = pid;
        var seen = new HashSet<uint>();
        for (var depth = 0; current != 0 && depth < MaxAncestryDepth && seen.Add(current); depth++)
        {
            var proc = ResolveProc(current, cache);
            if (MatchesImage(proc.Path))
            {
                return true;
            }

            if (!tree.TryGetValue(current, out var node) || _ancestryStops.Contains(node.Name))
            {
                break;
            }

            // Reject a recycled parent link: trust the stored parent PID only when the parent predates the
            // child (Windows never clears an exited parent's PID, and PIDs are recycled).
            var parent = ResolveProc(node.Parent, cache);
            if (proc.Created == 0 || parent.Created == 0 || parent.Created > proc.Created)
            {
                break;
            }

            current = node.Parent;
        }

        return false;
    }

    // pid -> (parent pid, image name), one snapshot per tick.
    private static Dictionary<uint, (uint Parent, string Name)> SnapshotProcessTree()
    {
        var map = new Dictionary<uint, (uint Parent, string Name)>();
        var snapshot = CreateToolhelp32Snapshot(Th32csSnapProcess, 0);
        if (snapshot == IntPtr.Zero || snapshot == InvalidHandleValue)
        {
            return map;
        }

        try
        {
            var entry = new ProcessEntry32 { dwSize = (uint)Marshal.SizeOf<ProcessEntry32>() };
            if (!Process32First(snapshot, ref entry))
            {
                return map;
            }

            do
            {
                map[entry.th32ProcessID] = (entry.th32ParentProcessID, entry.szExeFile);
            }
            while (Process32Next(snapshot, ref entry));
        }
        finally
        {
            CloseHandle(snapshot);
        }

        return map;
    }

    // Image-path match for path=/dir=/name=; svc= handled by PID.
    private bool MatchesImage(string? path)
    {
        if (path is null)
        {
            return false;
        }

        // Canonicalize to the same %ENV% space the rules were stored in (portable across users/machines).
        var canon = AmneziaGeo.Ipc.AppPathToken.Tokenize(path);
        if (_paths.Contains(canon))
        {
            return true;
        }

        if (_names.Count > 0)
        {
            var name = System.IO.Path.GetFileName(path);
            if (_names.Contains(name))
            {
                return true;
            }
        }

        foreach (var dir in _dirs)
        {
            // Matches dir prefix, catches versioned subfolders.
            if (canon.StartsWith(dir + "\\", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static (string? Path, long Created) ResolveProc(uint pid, Dictionary<uint, (string? Path, long Created)> cache)
    {
        if (cache.TryGetValue(pid, out var hit))
        {
            return hit;
        }

        var proc = QueryProc(pid);
        cache[pid] = proc;
        return proc;
    }

    private HashSet<uint> ResolveServicePids()
    {
        var pids = new HashSet<uint>();
        foreach (var service in _services)
        {
            var pid = QueryServiceProcessId(service);
            if (pid is > 0)
            {
                pids.Add(pid.Value);
            }
        }

        return pids;
    }

    // --- Win32 connection enumeration ---------------------------------------------------------------

    private static IEnumerable<(IPAddress Remote, uint Pid)> EnumerateTcpConnections()
    {
        var size = 0;
        GetExtendedTcpTable(IntPtr.Zero, ref size, false, AfInet, TcpTableOwnerPidAll, 0);
        if (size <= 0)
        {
            yield break;
        }

        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            if (GetExtendedTcpTable(buffer, ref size, false, AfInet, TcpTableOwnerPidAll, 0) != 0)
            {
                yield break;
            }

            var count = Marshal.ReadInt32(buffer);
            var rowPtr = buffer + 4;
            var rowSize = Marshal.SizeOf<MibTcpRowOwnerPid>();
            for (var i = 0; i < count; i++)
            {
                var row = Marshal.PtrToStructure<MibTcpRowOwnerPid>(rowPtr);
                rowPtr += rowSize;

                if (row.dwState == MibTcpStateListen || row.dwRemoteAddr == 0)
                {
                    continue; // no remote peer to route
                }

                // dwRemoteAddr is a network-byte-order DWORD; its in-memory bytes are the address octets.
                var remote = new IPAddress(BitConverter.GetBytes(row.dwRemoteAddr));
                if (!IsTunnelableRemote(remote))
                {
                    // Skip loopback/LAN: routing 127.0.0.1 into the tunnel broke the agent's DNS and app IPC.
                    continue;
                }

                yield return (remote, row.dwOwningPid);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    // Keep only public routable remotes; v6 arms for a future pass.
    internal static bool IsTunnelableRemote(IPAddress addr)
    {
        if (addr.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = addr.GetAddressBytes(); // network order: b[0] is the high octet
            return b[0] switch
            {
                0 => false,                                  // 0.0.0.0/8   "this network"
                10 => false,                                 // 10.0.0.0/8  private
                127 => false,                                // 127.0.0.0/8 loopback
                100 when b[1] is >= 64 and <= 127 => false,  // 100.64.0.0/10 CGNAT
                169 when b[1] == 254 => false,               // 169.254.0.0/16 link-local
                172 when b[1] is >= 16 and <= 31 => false,   // 172.16.0.0/12 private
                192 when b[1] == 168 => false,               // 192.168.0.0/16 private
                >= 224 => false,                             // 224.0.0.0/4 multicast + 240/4 reserved + 255.255.255.255
                _ => true,
            };
        }

        // IPv6: skip loopback (::1), unspecified (::), link-local (fe80::/10), ULA (fc00::/7), multicast (ff00::/8).
        if (IPAddress.IsLoopback(addr) || addr.Equals(IPAddress.IPv6Any))
        {
            return false;
        }

        var v6 = addr.GetAddressBytes();
        if (v6[0] == 0xff)
        {
            return false; // multicast ff00::/8
        }

        if (v6[0] == 0xfe && (v6[1] & 0xc0) == 0x80)
        {
            return false; // link-local fe80::/10
        }

        if ((v6[0] & 0xfe) == 0xfc)
        {
            return false; // ULA fc00::/7
        }

        return true;
    }

    // Image path + creation time from one handle; creation time validates PID identity across the tree.
    private static (string? Path, long Created) QueryProc(uint pid)
    {
        if (pid == 0)
        {
            return (null, 0); // System Idle / kernel pseudo-PID
        }

        var handle = OpenProcess(ProcessQueryLimitedInformation, false, pid);
        if (handle == IntPtr.Zero)
        {
            return (null, 0); // protected/elevated or already gone
        }

        try
        {
            var capacity = 1024;
            var buffer = new System.Text.StringBuilder(capacity);
            var path = QueryFullProcessImageName(handle, 0, buffer, ref capacity) ? buffer.ToString() : null;
            var created = GetProcessTimes(handle, out var creation, out _, out _, out _) ? creation : 0L;
            return (path, created);
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    // Resolve a service name to its hosting PID.
    private static uint? QueryServiceProcessId(string serviceName)
    {
        var scm = OpenSCManager(null, null, ScManagerConnect);
        if (scm == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            var service = OpenService(scm, serviceName, ServiceQueryStatus);
            if (service == IntPtr.Zero)
            {
                return null;
            }

            try
            {
                var size = Marshal.SizeOf<ServiceStatusProcess>();
                var buffer = Marshal.AllocHGlobal(size);
                try
                {
                    if (!QueryServiceStatusEx(service, ScStatusProcessInfo, buffer, size, out _))
                    {
                        return null;
                    }

                    var status = Marshal.PtrToStructure<ServiceStatusProcess>(buffer);
                    return status.dwProcessId == 0 ? null : status.dwProcessId;
                }
                finally
                {
                    Marshal.FreeHGlobal(buffer);
                }
            }
            finally
            {
                CloseServiceHandle(service);
            }
        }
        finally
        {
            CloseServiceHandle(scm);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MibTcpRowOwnerPid
    {
        public uint dwState;
        public uint dwLocalAddr;
        public uint dwLocalPort;
        public uint dwRemoteAddr;
        public uint dwRemotePort;
        public uint dwOwningPid;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceStatusProcess
    {
        public uint dwServiceType;
        public uint dwCurrentState;
        public uint dwControlsAccepted;
        public uint dwWin32ExitCode;
        public uint dwServiceSpecificExitCode;
        public uint dwCheckPoint;
        public uint dwWaitHint;
        public uint dwProcessId;
        public uint dwServiceFlags;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessEntry32
    {
        public uint dwSize;
        public uint cntUsage;
        public uint th32ProcessID;
        public IntPtr th32DefaultHeapID;
        public uint th32ModuleID;
        public uint cntThreads;
        public uint th32ParentProcessID;
        public int pcPriClassBase;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szExeFile;
    }

    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const uint ScManagerConnect = 0x0001;
    private const uint ServiceQueryStatus = 0x0004;
    private const int ScStatusProcessInfo = 0;
    private const uint Th32csSnapProcess = 0x00000002;
    private static readonly IntPtr InvalidHandleValue = new(-1);

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedTcpTable(IntPtr pTcpTable, ref int pdwSize, bool bOrder, int ulAf, int tableClass, int reserved);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint desiredAccess, bool inheritHandle, uint processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessTimes(IntPtr process, out long creation, out long exit, out long kernel, out long user);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageName(IntPtr process, uint flags, System.Text.StringBuilder exeName, ref int size);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr OpenSCManager(string? machineName, string? databaseName, uint desiredAccess);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr OpenService(IntPtr scManager, string serviceName, uint desiredAccess);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryServiceStatusEx(IntPtr service, int infoLevel, IntPtr buffer, int bufferSize, out int bytesNeeded);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseServiceHandle(IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint processId);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32First(IntPtr snapshot, ref ProcessEntry32 entry);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32Next(IntPtr snapshot, ref ProcessEntry32 entry);
}
