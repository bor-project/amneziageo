using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace AmneziaGeo.Windows.Tray;

/// <summary>
/// The single decision point for whether a system notification may be shown: the app notifications setting, the
/// OS notification permission, and (for connection notices) whether a GUI window is on screen. Kept free of
/// reflection so the native image stays small.
/// </summary>
internal static unsafe class NotificationGate
{
    // HKEY_CURRENT_USER; RRF_RT_DWORD (accepts a REG_DWORD value).
    private static readonly nint HkeyCurrentUser = unchecked((nint)0x80000001u);
    private const uint RrfRtDword = 0x18;

    // GUI process ids probed for a visible window during a single IsUiVisible pass.
    private static HashSet<uint> _probePids = new();
    private static bool _uiVisible;

    /// <summary>
    /// Whether any notification may be shown: the app setting is on and the OS allows toasts. A failed OS read
    /// defaults to allowed so a notification is never lost to a registry error.
    /// </summary>
    public static bool CanNotify()
    {
        return AgentLink.ShowNotifications && OsToastsEnabled();
    }

    /// <summary>
    /// Whether a GUI window is visible to the user: any targeted process owning a shown, non-minimized top-level
    /// window. A closed window or one minimized to the tray or taskbar counts as not visible.
    /// </summary>
    public static bool IsUiVisible()
    {
        try
        {
            var processes = Process.GetProcessesByName("AmneziaGeo.Windows.Ui");
            try
            {
                if (processes.Length == 0)
                {
                    return false;
                }

                _probePids = new HashSet<uint>();
                foreach (var p in processes)
                {
                    try
                    {
                        _probePids.Add((uint)p.Id);
                    }
                    catch
                    {
                    }
                }

                _uiVisible = false;
                Native.EnumWindows((nint)(delegate* unmanaged[Stdcall]<nint, nint, int>)&ProbeVisibleWindow, 0);
                return _uiVisible;
            }
            finally
            {
                foreach (var p in processes)
                {
                    p.Dispose();
                }
            }
        }
        catch
        {
            return false;
        }
    }

    // Whether the OS lets the app raise toasts. The Win10/11 shell routes a Shell_NotifyIcon balloon through the
    // notification platform, which the per-user ToastEnabled flag governs; a missing value means enabled.
    private static bool OsToastsEnabled()
    {
        return ReadHkcuDword(@"Software\Microsoft\Windows\CurrentVersion\PushNotifications", "ToastEnabled", 1) != 0;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static int ProbeVisibleWindow(nint hWnd, nint lParam)
    {
        if (_uiVisible)
        {
            return 1;
        }

        Native.GetWindowThreadProcessId(hWnd, out var pid);
        if (_probePids.Contains(pid) && Native.IsWindowVisible(hWnd) && !Native.IsIconic(hWnd))
        {
            _uiVisible = true;
        }

        return 1;
    }

    // Reads a HKCU DWORD, returning the default when the value is absent or unreadable.
    private static uint ReadHkcuDword(string subkey, string name, uint defaultValue)
    {
        var data = 0u;
        var size = (uint)sizeof(uint);
        var result = Native.RegGetValueW(HkeyCurrentUser, subkey, name, RrfRtDword, 0, ref data, ref size);
        return result == 0 ? data : defaultValue;
    }
}
