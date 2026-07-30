using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;

namespace AmneziaGeo.Windows.App;

/// <summary>
/// Resolves the runtime data root.
/// </summary>
internal static class AppDataRoot
{
    private const string AppFolder = "AmneziaGeo";

    /// <summary>
    /// Machine-wide root for shared assets (geo bases, logs, tunnel runtime state); stable across users and sessions.
    /// </summary>
    public static string MachineBase()
    {
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), AppFolder);
    }

    /// <summary>
    /// Per-user data root under a resolved profile's local application data.
    /// </summary>
    public static string UserBase(string localAppData)
    {
        return Path.Combine(localAppData, AppFolder);
    }

    /// <summary>
    /// Whether the root is the shared machine directory, which carries no user library.
    /// </summary>
    public static bool IsMachineRoot(string? root)
    {
        if (string.IsNullOrWhiteSpace(root))
        {
            return false;
        }

        try
        {
            return string.Equals(Normalize(root), Normalize(MachineBase()), StringComparison.OrdinalIgnoreCase);
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static string Normalize(string path)
    {
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    }

    private const uint InvalidSession = 0xFFFFFFFF;

    private static readonly bool RunningAsSystem = IsSystemAccount();

    private static string? _userCached;
    private static string? _systemCached;
    private static uint _systemCachedSession = InvalidSession;

    /// <summary>
    /// Path to the per-user data directory.
    /// </summary>
    public static string Base()
    {
        return Path.Combine(UserLocalAppData(), AppFolder);
    }

    private static string UserLocalAppData()
    {
        if (!RunningAsSystem)
        {
            return _userCached ??= Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        }

        return SystemSideLocalAppData();
    }

    private static string SystemSideLocalAppData()
    {
        var console = WTSGetActiveConsoleSessionId();
        if (console == _systemCachedSession && _systemCached is { } cached)
        {
            return cached;
        }

        foreach (var session in InteractiveSessions(console))
        {
            if (InteractiveUserLocalAppData(session) is { } interactive)
            {
                _systemCached = interactive;
                _systemCachedSession = console;
                PersistActiveRoot(interactive);
                return interactive;
            }
        }

        // No interactive session right now (a restart during an RDP-session flip, or before login): reuse the last
        // resolved user root instead of the SYSTEM profile, which points at a different database and would revert
        // the active user's edits after every restart.
        return ReadPersistedRoot() ?? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    }

    // Machine-wide marker of the last resolved interactive user root, so a session-less restart binds to the same
    // per-user database instead of the SYSTEM profile.
    private static string MarkerFile()
    {
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), AppFolder, "data-root");
    }

    private static void PersistActiveRoot(string root)
    {
        try
        {
            var marker = MarkerFile();
            Directory.CreateDirectory(Path.GetDirectoryName(marker)!);
            File.WriteAllText(marker, root);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static string? ReadPersistedRoot()
    {
        try
        {
            var marker = MarkerFile();
            if (!File.Exists(marker))
            {
                return null;
            }

            var root = File.ReadAllText(marker).Trim();
            return root.Length > 0 && Directory.Exists(root) ? root : null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    // Console seat first, then any active session: on a host worked over RDP the console seat is empty, its token
    // query fails and the service would drop into the SYSTEM profile while the tray and UI use the user profile.
    private static IEnumerable<uint> InteractiveSessions(uint console)
    {
        if (console != InvalidSession)
        {
            yield return console;
        }

        foreach (var session in ActiveSessions())
        {
            if (session != console)
            {
                yield return session;
            }
        }
    }

    private static List<uint> ActiveSessions()
    {
        var sessions = new List<uint>();
        if (!WTSEnumerateSessions(IntPtr.Zero, 0, 1, out var buffer, out var count))
        {
            return sessions;
        }

        try
        {
            var size = Marshal.SizeOf<WtsSessionInfo>();
            for (var i = 0; i < count; i++)
            {
                var info = Marshal.PtrToStructure<WtsSessionInfo>(IntPtr.Add(buffer, i * size));
                if (info.State == WtsActive)
                {
                    sessions.Add(info.SessionId);
                }
            }
        }
        finally
        {
            WTSFreeMemory(buffer);
        }

        return sessions;
    }

    private static bool IsSystemAccount()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return identity.IsSystem;
    }

    private static string? InteractiveUserLocalAppData(uint session)
    {
        if (session == InvalidSession)
        {
            return null;
        }

        if (!WTSQueryUserToken(session, out var token))
        {
            return null;
        }

        try
        {
            var size = 0u;
            _ = GetUserProfileDirectory(token, null, ref size);
            if (size == 0)
            {
                return null;
            }

            var buffer = new StringBuilder((int)size);
            if (!GetUserProfileDirectory(token, buffer, ref size))
            {
                return null;
            }

            return Path.Combine(buffer.ToString(), "AppData", "Local");
        }
        finally
        {
            CloseHandle(token);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WtsSessionInfo
    {
        public uint SessionId;
        public IntPtr WinStationName;
        public int State;
    }

    // WTS_CONNECTSTATE_CLASS.WTSActive
    private const int WtsActive = 0;

    [DllImport("kernel32.dll")]
    private static extern uint WTSGetActiveConsoleSessionId();

    [DllImport("wtsapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WTSQueryUserToken(uint sessionId, out IntPtr token);

    [DllImport("wtsapi32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "WTSEnumerateSessionsW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WTSEnumerateSessions(IntPtr server, uint reserved, uint version, out IntPtr sessionInfo, out int count);

    [DllImport("wtsapi32.dll")]
    private static extern void WTSFreeMemory(IntPtr memory);

    [DllImport("userenv.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetUserProfileDirectory(IntPtr token, StringBuilder? path, ref uint size);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}
