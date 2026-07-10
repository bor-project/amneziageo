using System.Runtime.InteropServices;

namespace AmneziaGeo.Windows.App;

/// <summary>
/// Install/uninstall housekeeping invoked by the MSI custom actions: removes orphaned transient tunnel
/// services and, on request, the runtime data tree. Done in-process via P/Invoke to the Service Control
/// Manager and the file API - no child shell (powershell/sc.exe), which keeps the installer's behavioural
/// footprint (and AV heuristic surface) low (#167).
/// </summary>
internal static class InstallerMaintenance
{
    private const string TransientPrefix = "AmneziaGeo$";

    /// <summary>
    /// Stops and deletes any leftover transient "AmneziaGeo$*" tunnel services orphaned by an agent crash.
    /// </summary>
    public static void RemoveTransientServices()
    {
        var scm = OpenSCManager(null, null, ScManagerConnect | ScManagerEnumerateService);
        if (scm == IntPtr.Zero)
        {
            return;
        }

        try
        {
            foreach (var name in EnumerateServiceNames(scm))
            {
                if (name.StartsWith(TransientPrefix, StringComparison.Ordinal))
                {
                    StopAndDelete(scm, name);
                }
            }
        }
        finally
        {
            CloseServiceHandle(scm);
        }
    }

    /// <summary>
    /// Removes the runtime data tree (%ProgramData%\AmneziaGeo), retrying briefly so the wipe survives the
    /// narrow window where the just-stopped agent has not yet released state.db or the current log file.
    /// </summary>
    public static void WipeRuntimeData()
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "AmneziaGeo");
        for (var attempt = 0; attempt < 10 && Directory.Exists(dir); attempt++)
        {
            try
            {
                Directory.Delete(dir, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Thread.Sleep(300);
            }
        }
    }

    private static IEnumerable<string> EnumerateServiceNames(IntPtr scm)
    {
        var resume = 0;
        EnumServicesStatusEx(scm, ScEnumProcessInfo, ServiceWin32, ServiceStateAll, IntPtr.Zero, 0, out var bytesNeeded, out _, ref resume, null);
        if (bytesNeeded <= 0)
        {
            yield break;
        }

        var buffer = Marshal.AllocHGlobal(bytesNeeded);
        try
        {
            resume = 0;
            if (!EnumServicesStatusEx(scm, ScEnumProcessInfo, ServiceWin32, ServiceStateAll, buffer, bytesNeeded, out bytesNeeded, out var count, ref resume, null))
            {
                yield break;
            }

            var stride = Marshal.SizeOf<EnumServiceStatusProcess>();
            for (var i = 0; i < count; i++)
            {
                var entry = Marshal.PtrToStructure<EnumServiceStatusProcess>(buffer + (i * stride));
                var name = Marshal.PtrToStringUni(entry.lpServiceName);
                if (!string.IsNullOrEmpty(name))
                {
                    yield return name;
                }
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static void StopAndDelete(IntPtr scm, string name)
    {
        var service = OpenService(scm, name, ServiceStop | ServiceDelete);
        if (service == IntPtr.Zero)
        {
            return;
        }

        try
        {
            var status = default(ServiceStatus);
            ControlService(service, ServiceControlStop, ref status);
            DeleteService(service);
        }
        finally
        {
            CloseServiceHandle(service);
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

    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceStatus
    {
        public uint dwServiceType;
        public uint dwCurrentState;
        public uint dwControlsAccepted;
        public uint dwWin32ExitCode;
        public uint dwServiceSpecificExitCode;
        public uint dwCheckPoint;
        public uint dwWaitHint;
    }

    private const uint ScManagerConnect = 0x0001;
    private const uint ScManagerEnumerateService = 0x0004;
    private const uint ServiceStop = 0x0020;
    private const uint ServiceDelete = 0x10000;
    private const uint ServiceControlStop = 0x00000001;
    private const int ScEnumProcessInfo = 0;
    private const int ServiceWin32 = 0x30;
    private const int ServiceStateAll = 0x3;

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr OpenSCManager(string? machineName, string? databaseName, uint desiredAccess);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "OpenServiceW")]
    private static extern IntPtr OpenService(IntPtr scManager, string serviceName, uint desiredAccess);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "EnumServicesStatusExW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumServicesStatusEx(IntPtr scManager, int infoLevel, int serviceType, int serviceState, IntPtr services, int bufSize, out int bytesNeeded, out int servicesReturned, ref int resumeHandle, string? groupName);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ControlService(IntPtr service, uint control, ref ServiceStatus status);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteService(IntPtr service);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseServiceHandle(IntPtr handle);
}
