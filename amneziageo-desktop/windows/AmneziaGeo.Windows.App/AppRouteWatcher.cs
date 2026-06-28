using System.Net;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;

namespace AmneziaGeo.Windows.App;

/// <summary>
/// Per-app split tunneling (#68): watches which processes own which outbound TCP connections and routes the
/// remote IPs of matched apps through the tunnel. Unlike the DNS-driven domain path, this is independent of
/// how the app resolves names (plain DNS, DoH, DoT, an in-process cache) — it keys on the OWNING PROCESS, so
/// an app the user marked for the tunnel is routed by the IPs it actually connects to.
///
/// Mechanism (all user-mode, no driver): poll <c>GetExtendedTcpTable(OWNER_PID)</c> for connections with a
/// remote endpoint, resolve each owning PID to its image path (and, for <c>svc=</c> matchers, resolve the
/// service name to its hosting PID), match against the app matchers, and feed the matched remote IPs to the
/// <see cref="DomainTracker"/> so they share the single allowed-ips authority and /32 route set. Discovered
/// IPs accumulate for the life of the tunnel (an app's endpoints rarely need un-routing while it runs).
///
/// Caveat: polling is reactive, so the very first SYN of a freshly opened connection can egress before its
/// route exists; the app retries and subsequent attempts route correctly. A race-free install would need a
/// kernel WFP connect-time callout (a driver), which is out of scope here.
/// </summary>
internal sealed class AppRouteWatcher
{
    // Poll cadence. Tight enough that a connecting app routes within ~1s (its TCP retry), without spinning.
    private static readonly TimeSpan _pollInterval = TimeSpan.FromSeconds(1);

    // Process-state of a TCP row that has a real remote peer we should route. LISTEN (2) has no remote.
    private const uint MibTcpStateListen = 2;
    private const int AfInet = 2;
    private const int TcpTableOwnerPidAll = 5;

    private readonly DomainTracker _tracker;
    private readonly ILogger _logger;

    // Parsed matchers (lower-cased, canonical). pkg= (UWP) is not matched in v1 — logged and skipped.
    private readonly HashSet<string> _paths = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _dirs = [];
    private readonly HashSet<string> _names = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _services = [];

    // PID -> image path, cached across ticks. PIDs can be reused, but the only consequence of a stale entry
    // is routing one extra IP through the tunnel, so a long-lived cache is a fair trade for not re-opening
    // every process each second. Capped so a churn of short-lived PIDs cannot grow it without bound.
    private readonly Dictionary<uint, string?> _pathCache = [];

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
                // Bare value with no sub-type: treat as a full path (the file picker's default).
                _paths.Add(token);
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
                    _paths.Add(value);
                    break;
                case "dir":
                    _dirs.Add(value.TrimEnd('\\', '/'));
                    break;
                case "name":
                    _names.Add(value);
                    break;
                case "svc":
                    _services.Add(value);
                    break;
                default:
                    // pkg= (UWP package SID) and any future kind: not matched by image path. Skip with a note
                    // so the user can see it had no effect rather than silently believing it routes.
                    _logger.LogInformation("app route watcher: matcher kind '{Kind}' is not supported yet; ignored", kind);
                    break;
            }
        }
    }

    /// <summary>True when there is at least one matcher this watcher can act on.</summary>
    public bool HasMatchers => _paths.Count > 0 || _dirs.Count > 0 || _names.Count > 0 || _services.Count > 0;

    /// <summary>
    /// Polls connections and routes matched apps' remote IPs until cancelled (tunnel teardown).
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
        // Resolve service matchers to their current hosting PIDs (services restart, so re-resolve each tick).
        var servicePids = ResolveServicePids();

        // Per-tick decision cache so each PID is classified at most once even if it owns many connections.
        var decision = new Dictionary<uint, bool>();
        var matchedIps = new List<string>();

        foreach (var (remote, pid) in EnumerateTcpConnections())
        {
            if (!decision.TryGetValue(pid, out var matched))
            {
                matched = servicePids.Contains(pid) || MatchesByImage(pid);
                decision[pid] = matched;
            }

            if (matched)
            {
                matchedIps.Add(remote.ToString());
            }
        }

        if (matchedIps.Count > 0)
        {
            _tracker.UpdateAppIps(matchedIps);
        }
    }

    // Whether the process' image path matches a path=/dir=/name= rule. Service-hosted (svc=) matching is
    // handled separately by PID, since a svchost-hosted service shares svchost.exe's path.
    private bool MatchesByImage(uint pid)
    {
        var path = ResolvePath(pid);
        if (path is null)
        {
            return false;
        }

        if (_paths.Contains(path))
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
            // Under the install tree: "<dir>\..." (case-insensitive). Catches an app's versioned subfolders
            // and sibling helpers (e.g. Discord.exe + its updater) from one folder rule.
            if (path.StartsWith(dir + "\\", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private string? ResolvePath(uint pid)
    {
        if (_pathCache.TryGetValue(pid, out var cached))
        {
            return cached;
        }

        var path = QueryImagePath(pid);
        if (_pathCache.Count > 4096)
        {
            _pathCache.Clear();
        }

        _pathCache[pid] = path;
        return path;
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
        // First call sizes the buffer.
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
                yield return (remote, row.dwOwningPid);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static string? QueryImagePath(uint pid)
    {
        if (pid == 0)
        {
            return null; // System Idle / kernel pseudo-PID
        }

        var handle = OpenProcess(ProcessQueryLimitedInformation, false, pid);
        if (handle == IntPtr.Zero)
        {
            return null; // protected/elevated or already gone
        }

        try
        {
            var capacity = 1024;
            var buffer = new System.Text.StringBuilder(capacity);
            return QueryFullProcessImageName(handle, 0, buffer, ref capacity) ? buffer.ToString() : null;
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    // Resolves a service name to its hosting process id, or null when the service is stopped / absent. Only
    // the services the user picked are queried (targeted), so no full service enumeration is needed.
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

    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const uint ScManagerConnect = 0x0001;
    private const uint ServiceQueryStatus = 0x0004;
    private const int ScStatusProcessInfo = 0;

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedTcpTable(IntPtr pTcpTable, ref int pdwSize, bool bOrder, int ulAf, int tableClass, int reserved);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint desiredAccess, bool inheritHandle, uint processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);

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
}
