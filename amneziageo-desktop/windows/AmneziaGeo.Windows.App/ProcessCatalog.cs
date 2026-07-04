using System.Diagnostics;
using System.Runtime.InteropServices;

namespace AmneziaGeo.Windows.App;

/// <summary>
/// Enumerates running applications and services for the per-app tunneling picker.
/// </summary>
internal static class ProcessCatalog
{
    /// <summary>
    /// A picker row.
    /// </summary>
    public sealed record Entry(string Kind, string Label, string Value, string Detail);

    /// <summary>
    /// Returns the unified app + service list.
    /// </summary>
    public static IReadOnlyList<Entry> List()
    {
        var entries = new List<Entry>();

        // Services first so their PIDs can be excluded from the app list.
        var services = EnumerateServices();
        var servicePids = new HashSet<uint>();
        foreach (var (name, display, pid) in services)
        {
            if (pid != 0)
            {
                servicePids.Add(pid);
            }

            var host = pid != 0 ? QueryImagePath(pid) ?? string.Empty : string.Empty;
            entries.Add(new Entry("service", string.IsNullOrWhiteSpace(display) ? name : display, name, host));
        }

        // One row per distinct image path, skipping service hosts and system procs.
        var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var process in Process.GetProcesses())
        {
            try
            {
                var pid = (uint)process.Id;
                if (pid == 0 || servicePids.Contains(pid))
                {
                    continue; // Idle, or a service host already represented as a Service row
                }

                var path = QueryImagePath(pid);
                if (path is null || !seenPaths.Add(path))
                {
                    continue; // unreadable (System/Registry/Secure System) or already listed
                }

                entries.Add(new Entry("app", DescribeApp(path, process.ProcessName), path, string.Empty));
            }
            catch
            {
                // A process that exited mid-enumeration: skip it.
            }
            finally
            {
                process.Dispose();
            }
        }

        // Stable order: apps then services, each by label.
        entries.Sort((a, b) =>
        {
            var byKind = string.CompareOrdinal(a.Kind, b.Kind);
            return byKind != 0 ? byKind : string.Compare(a.Label, b.Label, StringComparison.OrdinalIgnoreCase);
        });
        return entries;
    }

    // Friendly label: FileDescription when present, else process name.
    private static string DescribeApp(string path, string processName)
    {
        try
        {
            var info = FileVersionInfo.GetVersionInfo(path);
            if (!string.IsNullOrWhiteSpace(info.FileDescription))
            {
                return info.FileDescription!.Trim();
            }
        }
        catch
        {
            // No version resource / access denied: use the process name.
        }

        return processName;
    }

    private static List<(string Name, string Display, uint Pid)> EnumerateServices()
    {
        var result = new List<(string, string, uint)>();
        var scm = OpenSCManager(null, null, ScManagerEnumerateService);
        if (scm == IntPtr.Zero)
        {
            return result;
        }

        try
        {
            var resume = 0;
            // First call sizes the buffer.
            EnumServicesStatusEx(scm, ScEnumProcessInfo, ServiceWin32, ServiceActive, IntPtr.Zero, 0, out var bytesNeeded, out _, ref resume, null);
            if (bytesNeeded <= 0)
            {
                return result;
            }

            var buffer = Marshal.AllocHGlobal(bytesNeeded);
            try
            {
                resume = 0;
                if (!EnumServicesStatusEx(scm, ScEnumProcessInfo, ServiceWin32, ServiceActive, buffer, bytesNeeded, out bytesNeeded, out var count, ref resume, null))
                {
                    return result;
                }

                var stride = Marshal.SizeOf<EnumServiceStatusProcess>();
                for (var i = 0; i < count; i++)
                {
                    var entry = Marshal.PtrToStructure<EnumServiceStatusProcess>(buffer + (i * stride));
                    var name = Marshal.PtrToStringUni(entry.lpServiceName) ?? string.Empty;
                    var display = Marshal.PtrToStringUni(entry.lpDisplayName) ?? name;
                    result.Add((name, display, entry.ServiceStatusProcess.dwProcessId));
                }
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        finally
        {
            CloseServiceHandle(scm);
        }

        return result;
    }

    private static string? QueryImagePath(uint pid)
    {
        if (pid == 0)
        {
            return null;
        }

        var handle = OpenProcess(ProcessQueryLimitedInformation, false, pid);
        if (handle == IntPtr.Zero)
        {
            return null;
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

    [StructLayout(LayoutKind.Sequential)]
    private struct EnumServiceStatusProcess
    {
        public IntPtr lpServiceName;
        public IntPtr lpDisplayName;
        public ServiceStatusProcess ServiceStatusProcess;
    }

    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const uint ScManagerEnumerateService = 0x0004;
    private const int ScEnumProcessInfo = 0;
    private const int ServiceWin32 = 0x30; // OWN_PROCESS | SHARE_PROCESS
    private const int ServiceActive = 0x1;

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr OpenSCManager(string? machineName, string? databaseName, uint desiredAccess);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "EnumServicesStatusExW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumServicesStatusEx(IntPtr scManager, int infoLevel, int serviceType, int serviceState, IntPtr services, int bufSize, out int bytesNeeded, out int servicesReturned, ref int resumeHandle, string? groupName);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseServiceHandle(IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint desiredAccess, bool inheritHandle, uint processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageName(IntPtr process, uint flags, System.Text.StringBuilder exeName, ref int size);
}
