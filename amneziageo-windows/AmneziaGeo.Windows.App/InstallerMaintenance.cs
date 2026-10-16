using System.Runtime.InteropServices;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

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

    private const string RetiredLibrary = "state.db.legacy";

    /// <summary>
    /// Stops and deletes any leftover transient "AmneziaGeo$*" tunnel services orphaned by an agent crash.
    /// </summary>
    public static void RemoveTransientServices()
    {
        ReapTransientServices(null);
    }

    /// <summary>
    /// Reverts any DNS redirect this product left on the adapters, from the persisted per-tunnel state, and
    /// forces any adapter still on the loopback proxy back to automatic. Standalone (no DI host), so removing
    /// the product or clicking the repair button restores the resolver even when the agent died before it could.
    /// </summary>
    public static void RestoreDnsRedirect()
    {
        try
        {
            new DnsConfigurator(NullLogger<DnsConfigurator>.Instance).RestoreAndHeal();
        }
        catch (Exception ex)
        {
            // Best-effort: a restore hiccup must never block uninstall or the repair action.
            Console.Error.WriteLine($"dns restore failed: {ex}");
        }
    }

    /// <summary>
    /// Stops and deletes transient "AmneziaGeo$*" tunnel services except those for keepBareNames; returns the removed bare names.
    /// </summary>
    /// <summary>
    /// The tunnels this machine has a service installed for.
    /// </summary>
    public static IReadOnlyList<string> TransientTunnels()
    {
        var names = new List<string>();
        var scm = OpenSCManager(null, null, ScManagerConnect | ScManagerEnumerateService);
        if (scm == IntPtr.Zero)
        {
            return names;
        }

        try
        {
            foreach (var name in EnumerateServiceNames(scm))
            {
                if (name.StartsWith(TransientPrefix, StringComparison.Ordinal))
                {
                    names.Add(name[TransientPrefix.Length..]);
                }
            }
        }
        finally
        {
            CloseServiceHandle(scm);
        }

        return names;
    }

    public static IReadOnlyList<string> ReapTransientServices(IEnumerable<string>? keepBareNames)
    {
        var removed = new List<string>();
        var scm = OpenSCManager(null, null, ScManagerConnect | ScManagerEnumerateService);
        if (scm == IntPtr.Zero)
        {
            return removed;
        }

        var kept = new HashSet<string>(StringComparer.Ordinal);
        foreach (var bare in keepBareNames ?? [])
        {
            kept.Add(TransientPrefix + bare);
        }

        try
        {
            foreach (var name in EnumerateServiceNames(scm))
            {
                if (!name.StartsWith(TransientPrefix, StringComparison.Ordinal))
                {
                    continue;
                }

                if (kept.Contains(name))
                {
                    continue;
                }

                StopAndDelete(scm, name);
                removed.Add(name[TransientPrefix.Length..]);
            }
        }
        finally
        {
            CloseServiceHandle(scm);
        }

        return removed;
    }

    /// <summary>
    /// Settings reset: removes the per-user runtime data tree, the agent settings kept for the machine and the retired
    /// copies of the databases, retrying briefly so the wipe survives the narrow window where the just-stopped agent
    /// has not yet released state.db or the current log file.
    /// </summary>
    public static void WipeRuntimeData()
    {
        WipeRuntimeData(AppDataRoot.Base(), AppDataRoot.MachineBase());
    }

    /// <summary>
    /// Removal with the configuration deleted: removes the per-user and the machine runtime data trees.
    /// </summary>
    public static void WipeAllRuntimeData()
    {
        WipeAllRuntimeData(AppDataRoot.Base(), AppDataRoot.MachineBase());
    }

    /// <summary>
    /// Settings reset of the given per-user and machine roots.
    /// </summary>
    internal static void WipeRuntimeData(string userRoot, string machineRoot)
    {
        RemoveTree(userRoot);
        ResetMachineSettings(Path.Combine(machineRoot, Path.GetFileName(TunnelPaths.MachineDbFile())));
        RemoveRetiredCopies(machineRoot);
    }

    /// <summary>
    /// Removal of the given per-user and machine roots.
    /// </summary>
    internal static void WipeAllRuntimeData(string userRoot, string machineRoot)
    {
        RemoveTree(userRoot);
        RemoveTree(machineRoot);
    }

    // Deletes the settings rows the machine store owns, so the agent comes up on its defaults.
    private static void ResetMachineSettings(string machineDb)
    {
        if (!File.Exists(machineDb))
        {
            return;
        }

        try
        {
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = machineDb, Pooling = false }.ToString());
            connection.Open();

            using (var pragma = connection.CreateCommand())
            {
                pragma.CommandText = "PRAGMA busy_timeout = 3000;";
                pragma.ExecuteNonQuery();
            }

            using var command = connection.CreateCommand();
            var names = new List<string>();
            foreach (var key in ScopedStateStore.MachineKeys)
            {
                var name = $"$k{names.Count}";
                names.Add(name);
                command.Parameters.AddWithValue(name, key);
            }

            command.CommandText = $"DELETE FROM settings WHERE key IN ({string.Join(", ", names)});";
            command.ExecuteNonQuery();
        }
        catch (SqliteException)
        {
        }
    }

    // Removes the retired library and the copies the databases set aside before a migration or after a corruption.
    private static void RemoveRetiredCopies(string machineRoot)
    {
        if (!Directory.Exists(machineRoot))
        {
            return;
        }

        foreach (var file in Directory.EnumerateFiles(machineRoot))
        {
            var name = Path.GetFileName(file);
            if (name.StartsWith(RetiredLibrary, StringComparison.Ordinal)
                || name.EndsWith(".bak", StringComparison.Ordinal)
                || name.Contains(".corrupt-", StringComparison.Ordinal))
            {
                TryDelete(file);
            }
        }
    }

    private static void TryDelete(string file)
    {
        try
        {
            File.Delete(file);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    // Removes a tree, retrying while a just-stopped process still holds a file in it.
    private static void RemoveTree(string dir)
    {
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
