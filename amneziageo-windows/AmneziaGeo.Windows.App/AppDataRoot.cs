#if !DEBUG
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
#endif

namespace AmneziaGeo.Windows.App;

/// <summary>
/// Resolves the runtime data root.
/// </summary>
internal static class AppDataRoot
{
    private const string AppFolder = "AmneziaGeo";

#if DEBUG
    // Debug: единый машинный каталог для агента и SYSTEM-службы
    /// <summary>
    /// Path to the data directory.
    /// </summary>
    public static string Base()
    {
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), AppFolder);
    }
#else
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
        var session = WTSGetActiveConsoleSessionId();
        if (session == _systemCachedSession && _systemCached is { } cached)
        {
            return cached;
        }

        if (InteractiveUserLocalAppData(session) is { } interactive)
        {
            _systemCached = interactive;
            _systemCachedSession = session;
            return interactive;
        }

        return Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
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

    [DllImport("kernel32.dll")]
    private static extern uint WTSGetActiveConsoleSessionId();

    [DllImport("wtsapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WTSQueryUserToken(uint sessionId, out IntPtr token);

    [DllImport("userenv.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetUserProfileDirectory(IntPtr token, StringBuilder? path, ref uint size);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
#endif
}
