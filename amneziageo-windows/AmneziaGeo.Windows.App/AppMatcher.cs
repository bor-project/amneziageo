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
    private readonly ProcessImages? _images;

    // The rules in force, parsed. Replaced whole on a reload, so a reader sees one set or the other.
    private volatile RuleSet _set;

    // Parsed matchers.
    private sealed record RuleSet(
        IReadOnlyList<string> Rules,
        HashSet<string> Paths,
        List<string> Dirs,
        HashSet<string> Names,
        List<string> Services,
        HashSet<string> Packages);

    // Stop the ancestry walk at generic hosts so an app rule stays scoped to its own tree.
    private static readonly HashSet<string> _ancestryStops = new(StringComparer.OrdinalIgnoreCase)
    {
        "explorer.exe", "services.exe", "svchost.exe", "wininit.exe", "winlogon.exe",
        "userinit.exe", "smss.exe", "csrss.exe", "lsass.exe",
    };

    /// <summary>
    /// ctor
    /// </summary>
    public AppMatcher(IReadOnlyList<string> matchers, ILogger logger, ProcessImages? images = null)
    {
        _logger = logger;
        _images = images;
        _set = Parse(matchers, logger);
    }

    /// <summary>
    /// Puts an edited rule set in force without reconnecting the tunnel. Returns true when the rules changed.
    /// </summary>
    public bool Reload(IReadOnlyList<string> rules)
    {
        if (_set.Rules.SequenceEqual(rules, StringComparer.OrdinalIgnoreCase))
        {
            return false;
        }

        _set = Parse(rules, _logger);
        _logger.LogInformation("app rules reloaded without reconnecting: {Count} rule(s) now in effect", rules.Count);
        ReportRuleProblems();
        return true;
    }

    // Reads the rule texts into the matchers they stand for.
    private static RuleSet Parse(IReadOnlyList<string> matchers, ILogger logger)
    {
        var set = new RuleSet(
            matchers,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            [],
            new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            [],
            new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        foreach (var raw in matchers)
        {
            var token = raw.Trim();
            var eq = token.IndexOf('=');
            if (eq <= 0)
            {
                // Bare value: treat as a full path.
                AddPathMatcher(set, token);
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
                    AddPathMatcher(set, value);
                    break;
                case "dir":
                    AddDirMatcher(set, value);
                    break;
                case "name":
                    set.Names.Add(value);
                    break;
                case "svc":
                    set.Services.Add(value);
                    break;
                case "pkg":
                    set.Packages.Add(value);
                    break;
                default:
                    logger.LogInformation("app rules of type '{Kind}' are not supported yet and are ignored; the programs they name will not be routed through the tunnel", kind);
                    break;
            }
        }

        return set;
    }

    /// <summary>
    /// Writes a warning for every app rule that catches nothing here: no such program installed, or its program
    /// also installed under another package no rule names.
    /// </summary>
    public void ReportRuleProblems(IReadOnlyList<PackageEntry>? packages = null)
    {
        var rules = _set.Rules;
        var installed = packages ?? InstalledPackages.Snapshot(AppRuleAudit.Publishers(rules), _logger);
        foreach (var note in AppRuleAudit.Check(rules, installed, DirectoryExists, FileExists, ServiceExists))
        {
            if (note.Twin is null)
            {
                _logger.LogWarning("the app rule \"{Rule}\" names no program installed on this machine, so nothing rides the tunnel for it", note.Rule);
            }
            else
            {
                _logger.LogWarning("the app rule \"{Rule}\" and the installed package {Twin} run the same program, so if you use that one its traffic stays outside the tunnel until a rule names it too", note.Rule, note.Twin);
            }
        }
    }

    // Rule targets on disk. A rule under a per-user folder is looked for in every user profile: the agent runs
    // as LocalSystem, whose own profile holds none of them.
    private static bool DirectoryExists(string path) => AmneziaGeo.Ipc.AppPathToken.Expand(path).Any(Directory.Exists);

    private static bool FileExists(string path) => AmneziaGeo.Ipc.AppPathToken.Expand(path).Any(File.Exists);

    // Whether a service of that name is registered.
    private static bool ServiceExists(string name)
    {
        using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\" + name);
        return key is not null;
    }

    // A Store/MSIX exe path becomes a package match so the rule survives auto-updates into a new version folder.
    private static void AddPathMatcher(RuleSet set, string value)
    {
        var canon = AmneziaGeo.Ipc.AppPathToken.Tokenize(value);
        var family = AmneziaGeo.Ipc.AppPathToken.PackageFamilyFromPath(canon);
        if (family is not null)
        {
            set.Packages.Add(family);
        }
        else
        {
            set.Paths.Add(canon);
        }
    }

    // A Store/MSIX package folder becomes a package match; a Squirrel app-<ver> leaf is hoisted (#204).
    private static void AddDirMatcher(RuleSet set, string value)
    {
        var canon = AmneziaGeo.Ipc.AppPathToken.Tokenize(value.TrimEnd('\\', '/'));
        var family = AmneziaGeo.Ipc.AppPathToken.PackageFamilyFromPath(canon);
        if (family is not null)
        {
            set.Packages.Add(family);
        }
        else
        {
            set.Dirs.Add(AmneziaGeo.Ipc.AppPathToken.StripVersionedLeaf(canon));
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
    /// Whether a pid belongs to the app rules, or null when nothing names the program behind it any more.
    /// </summary>
    internal bool? Owned(uint pid)
    {
        if (pid == 0)
        {
            return null;
        }

        if (ResolveServicePids().Contains(pid))
        {
            return true;
        }

        var cache = new Dictionary<uint, (string? Path, long Created)>();
        if (MatchesByImageOrAncestor(pid, SnapshotProcessTree(), cache))
        {
            return true;
        }

        return cache.TryGetValue(pid, out var proc) && proc.Path is not null ? false : null;
    }

    /// <summary>
    /// Has any matcher.
    /// </summary>
    public bool HasMatchers
    {
        get
        {
            var set = _set;
            return set.Paths.Count > 0 || set.Dirs.Count > 0 || set.Names.Count > 0 || set.Services.Count > 0 || set.Packages.Count > 0;
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
    /// <summary>
    /// Whether an image reported as an NT device path (as the firewall reports it) matches the app rules.
    /// </summary>
    public bool MatchesDevicePath(string path)
    {
        return MatchesImage(ToDosPath(path));
    }

    // \device\harddiskvolume3\app\app.exe -> C:\app\app.exe. An unmapped device is left alone; name rules still match it.
    private static string ToDosPath(string path)
    {
        foreach (var (device, drive) in _devices.Value)
        {
            if (path.StartsWith(device, StringComparison.OrdinalIgnoreCase) && path.Length > device.Length)
            {
                return drive + path[device.Length..];
            }
        }

        return path;
    }

    private static readonly Lazy<List<(string Device, string Drive)>> _devices = new(BuildDeviceMap, LazyThreadSafetyMode.ExecutionAndPublication);

    // Volume device paths per drive letter, resolved once.
    private static List<(string Device, string Drive)> BuildDeviceMap()
    {
        var map = new List<(string, string)>();
        var buffer = new char[1024];
        for (var letter = 'A'; letter <= 'Z'; letter++)
        {
            var drive = $"{letter}:";
            var length = QueryDosDevice(drive, buffer, buffer.Length);
            if (length == 0)
            {
                continue;
            }

            var target = new string(buffer, 0, (int)length).Split('\0')[0];
            if (target.Length > 0)
            {
                map.Add((target, drive));
            }
        }

        return map;
    }

    private bool MatchesImage(string? path)
    {
        if (path is null)
        {
            return false;
        }

        var set = _set;

        // Canonicalize to the same %ENV% space the rules were stored in (portable across users/machines).
        var canon = AmneziaGeo.Ipc.AppPathToken.Tokenize(path);
        if (set.Paths.Contains(canon))
        {
            return true;
        }

        if (set.Names.Count > 0)
        {
            var name = System.IO.Path.GetFileName(path);
            if (set.Names.Contains(name))
            {
                return true;
            }
        }

        foreach (var dir in set.Dirs)
        {
            // Matches dir prefix, catches versioned subfolders.
            if (canon.StartsWith(dir + "\\", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        if (set.Packages.Count > 0)
        {
            // A Store/MSIX image matches by package family, so the version folder in its path is ignored.
            var family = AmneziaGeo.Ipc.AppPathToken.PackageFamilyFromPath(path);
            if (family is not null && set.Packages.Contains(family))
            {
                return true;
            }
        }

        return false;
    }

    private (string? Path, long Created) ResolveProc(uint pid, Dictionary<uint, (string? Path, long Created)> cache)
    {
        if (cache.TryGetValue(pid, out var hit))
        {
            return hit;
        }

        // A program that ended between its packet and this question answers no more, so the image held from its
        // start stands in for it.
        var proc = QueryProc(pid);
        if (proc.Path is null && _images is not null && _images.TryGet(pid, out var held))
        {
            proc = (ToDosPath(held.Path), held.Created);
        }

        cache[pid] = proc;
        return proc;
    }

    private HashSet<uint> ResolveServicePids()
    {
        var pids = new HashSet<uint>();
        foreach (var service in _set.Services)
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

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern uint QueryDosDevice(string deviceName, [Out] char[] targetPath, int max);
}
