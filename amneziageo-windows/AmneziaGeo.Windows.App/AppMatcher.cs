using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;

namespace AmneziaGeo.Windows.App;

/// <summary>
/// Per-app match engine: resolves whether a PID belongs to the app rules (path/dir/name/service).
/// The flow tracker uses it to steer matched apps' remotes.
/// </summary>
internal sealed class AppMatcher
{
    // Cap the ancestry walk.
    private const int MaxAncestryDepth = 8;

    private readonly ILogger _logger;

    // Parsed matchers. pkg= not matched in v1.
    private readonly HashSet<string> _paths = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _dirs = [];
    private readonly HashSet<string> _names = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _services = [];

    // Stop the ancestry walk at generic hosts so an app rule stays scoped to its own tree.
    private static readonly HashSet<string> _ancestryStops = new(StringComparer.OrdinalIgnoreCase)
    {
        "explorer.exe", "services.exe", "svchost.exe", "wininit.exe", "winlogon.exe",
        "userinit.exe", "smss.exe", "csrss.exe", "lsass.exe",
    };

    /// <summary>
    /// ctor
    /// </summary>
    public AppMatcher(IReadOnlyList<string> matchers, ILogger logger)
    {
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
                    // Hoist a versioned leaf so a rule saved against an old app-<ver> folder still matches after
                    // the app auto-updates into a new one (#204).
                    _dirs.Add(AmneziaGeo.Ipc.AppPathToken.StripVersionedLeaf(AmneziaGeo.Ipc.AppPathToken.Tokenize(value.TrimEnd('\\', '/'))));
                    break;
                case "name":
                    _names.Add(value);
                    break;
                case "svc":
                    _services.Add(value);
                    break;
                default:
                    // pkg= (UWP) and unknown kinds: not matched by image path.
                    _logger.LogInformation("app matcher: matcher kind '{Kind}' is not supported yet; ignored", kind);
                    break;
            }
        }
    }

    /// <summary>
    /// Whether a PID matches the app rules.
    /// </summary>
    internal bool MatchesPid(uint pid) => ResolveServicePids().Contains(pid) || MatchesByImageOrAncestor(pid, SnapshotProcessTree(), new Dictionary<uint, (string? Path, long Created)>());

    /// <summary>
    /// Filters PIDs to those matching the app rules, snapshotting the process tree once.
    /// </summary>
    internal HashSet<uint> MatchPids(IReadOnlyCollection<uint> pids)
    {
        var matched = new HashSet<uint>();
        if (pids.Count == 0)
        {
            return matched;
        }

        var services = ResolveServicePids();
        var tree = SnapshotProcessTree();
        var cache = new Dictionary<uint, (string? Path, long Created)>();
        foreach (var pid in pids)
        {
            if (services.Contains(pid) || MatchesByImageOrAncestor(pid, tree, cache))
            {
                matched.Add(pid);
            }
        }

        return matched;
    }

    /// <summary>
    /// Has any matcher.
    /// </summary>
    public bool HasMatchers => _paths.Count > 0 || _dirs.Count > 0 || _names.Count > 0 || _services.Count > 0;

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

    // pid -> (parent pid, image name), one snapshot per resolve.
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
