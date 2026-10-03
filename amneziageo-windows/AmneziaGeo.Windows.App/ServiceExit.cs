using System.ComponentModel;
using System.Runtime.InteropServices;

namespace AmneziaGeo.Windows.App;

/// <summary>
/// Reads the codes a service ended with and puts them into words.
/// </summary>
internal static partial class ServiceExit
{
    private const uint ScManagerConnect = 0x0001;
    private const uint ServiceQueryStatus = 0x0004;
    private const uint ServiceStopped = 1;
    private const uint ServiceSpecificError = 1066;

    // The words the tunnel engine gives its own codes.
    private static readonly string[] _engineErrors =
    [
        "No error",
        "Unable to open log file",
        "Unable to load configuration from path",
        "Unable to create Wintun interface",
        "Unable to listen on named pipe",
        "Unable to resolve one or more DNS hostname endpoints",
        "Unable to enable firewall rules",
        "Unable to set device configuration",
        "Unable to bind sockets to default route",
        "Unable to set interface addresses, routes, dns, and/or interface settings",
        "Unable to determine path of running executable",
        "Unable to track existing tunnels",
        "Unable to enumerate current sessions",
        "Unable to drop privileges",
        "An error occurred while running a configuration script command",
        "An internal Windows error has occurred",
    ];

    /// <summary>
    /// The codes the service manager keeps for a stopped service; zeroes for any other.
    /// </summary>
    public static (uint Win32, uint Specific) Read(string serviceName)
    {
        var manager = OpenSCManager(null, null, ScManagerConnect);
        if (manager == 0)
        {
            return (0, 0);
        }

        try
        {
            var service = OpenService(manager, serviceName, ServiceQueryStatus);
            if (service == 0)
            {
                return (0, 0);
            }

            try
            {
                return QueryServiceStatus(service, out var status) && status.CurrentState == ServiceStopped
                    ? (status.Win32ExitCode, status.ServiceSpecificExitCode)
                    : (0, 0);
            }
            finally
            {
                CloseServiceHandle(service);
            }
        }
        finally
        {
            CloseServiceHandle(manager);
        }
    }

    /// <summary>
    /// Words for the codes a service ended with; empty for a clean stop.
    /// </summary>
    public static string Describe(uint win32, uint specific)
    {
        if (win32 == 0)
        {
            return string.Empty;
        }

        if (win32 == ServiceSpecificError && specific != 0)
        {
            return specific < _engineErrors.Length
                ? $"engine error {specific} ({_engineErrors[specific]})"
                : $"engine error {specific}";
        }

        return $"error {win32} ({new Win32Exception((int)win32).Message.TrimEnd('.', ' ', '\r', '\n')})";
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceStatus
    {
        public uint ServiceType;
        public uint CurrentState;
        public uint ControlsAccepted;
        public uint Win32ExitCode;
        public uint ServiceSpecificExitCode;
        public uint CheckPoint;
        public uint WaitHint;
    }

    [LibraryImport("advapi32.dll", EntryPoint = "OpenSCManagerW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint OpenSCManager(string? machine, string? database, uint access);

    [LibraryImport("advapi32.dll", EntryPoint = "OpenServiceW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint OpenService(nint manager, string name, uint access);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool QueryServiceStatus(nint service, out ServiceStatus status);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseServiceHandle(nint handle);
}
