using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace AmneziaGeo.Windows.Tray;

/// <summary>
/// The tray anchor entry point: a hidden owner window drives a Shell notify icon, a background link keeps the
/// agent tunnel alive and feeds the icon its colour and tooltip, balloons announce the connect start,
/// completion, and failure while the GUI is not in front. A left click (or the menu's Open) surfaces the
/// lightweight launcher, whose More button expands it to the full configuration console; the menu also
/// connects, disconnects, or exits. A cold launch opens the launcher; the right-click menu is never
/// auto-shown (#187). Launched with --connect (post-install auto-connect), it dials the active profile
/// straight away and stays resident, no window (#188).
/// </summary>
internal static unsafe class Program
{
    // Matches the GUI's AppUserModelID so a taskbar pin groups with the launched window.
    private const string AppUserModelId = "AmneziaGeo.AmneziaGeo";
    private const string ClassName = "AmneziaGeoTray";

    // Fallback so the cold-launch popup still shows if no snapshot arrives.
    private const uint PopupTimerId = 1;
    private const uint PopupTimeoutMs = 1200;

    private static nint _hwnd;
    private static nint[] _icons = [];
    private static int _current;
    private static uint _taskbarCreatedMsg;
    private static nint _classNamePtr;
    private static bool _popupPending;
    private static bool _autoConnect;
    private static bool _stateInitialized;

    // State the current transition started from; -1 until one is seen.
    private static int _transitionFrom = -1;

    // GUI process ids targeted by the current Exit close-sweep.
    private static HashSet<uint> _uiPids = new();

    [STAThread]
    private static int Main(string[] args)
    {
        // Post-install auto-connect (#188): dial the active profile on cold launch instead of showing the launcher.
        _autoConnect = Array.IndexOf(args, "--connect") >= 0;

        Native.SetCurrentProcessExplicitAppUserModelID(AppUserModelId);

        // A second launch (shortcut clicked again) hands the running tray the "open GUI" nudge and exits.
        if (!SingleInstance.TryAcquire())
        {
            return 0;
        }

        Labels.Load();

        _classNamePtr = Marshal.StringToHGlobalUni(ClassName);
        var hInstance = Native.GetModuleHandleW(null);
        var wc = new Native.WNDCLASSEXW
        {
            cbSize = (uint)sizeof(Native.WNDCLASSEXW),
            lpfnWndProc = (nint)(delegate* unmanaged[Stdcall]<nint, uint, nuint, nint, nint>)&WndProc,
            hInstance = hInstance,
            lpszClassName = _classNamePtr,
        };
        Native.RegisterClassExW(ref wc);

        // Re-add the icon if Explorer restarts.
        _taskbarCreatedMsg = Native.RegisterWindowMessageW("TaskbarCreated");

        // A normal (never shown) window: it can take the foreground for the menu and receive the broadcast.
        _hwnd = Native.CreateWindowExW(0, ClassName, "AmneziaGeo", 0, 0, 0, 0, 0, 0, 0, hInstance, 0);

        _icons =
        [
            MakeStateIcon(0x7B, 0x81, 0x8D), // disconnected: grey
            MakeStateIcon(0xE0, 0x90, 0x2F), // transitioning: orange
            MakeStateIcon(0x2A, 0x6F, 0xDB), // connected: blue
        ];
        AddOrModifyIcon(Native.NIM_ADD);

        AgentLink.Start((state, connectFailed) => Native.PostMessageW(_hwnd, Native.WM_STATE, (nuint)state, connectFailed ? 1 : 0));
        SingleInstance.ListenForActivation(_hwnd, Native.WM_OPENUI);
        SingleInstance.ListenForQuit(_hwnd, Native.WM_QUITTRAY);

        // Cold launch: bring up only the tray, then resolve once the first snapshot lands (or after a short
        // fallback) - open the launcher with an active profile, or the full GUI without one.
        _popupPending = true;
        Native.SetTimer(_hwnd, PopupTimerId, PopupTimeoutMs, 0);

        while (Native.GetMessageW(out var msg, 0, 0, 0) > 0)
        {
            Native.TranslateMessage(ref msg);
            Native.DispatchMessageW(ref msg);
        }

        foreach (var icon in _icons)
        {
            Native.DestroyIcon(icon);
        }

        return 0;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static nint WndProc(nint hWnd, uint msg, nuint wParam, nint lParam)
    {
        try
        {
            if (msg == Native.WM_TRAY)
            {
                var ev = (uint)(lParam & 0xFFFF);
                if (ev == Native.WM_LBUTTONUP)
                {
                    LaunchUi("--launcher");
                }
                else if (ev == Native.WM_RBUTTONUP)
                {
                    ShowMenuAtCursor();
                }
                else if (ev == Native.NIN_BALLOONUSERCLICK)
                {
                    LaunchUi("--launcher");
                }

                return 0;
            }

            if (msg == Native.WM_COMMAND)
            {
                OnCommand((int)(wParam & 0xFFFF), hWnd);
                return 0;
            }

            if (msg == Native.WM_STATE)
            {
                SetState((int)wParam, lParam != 0);
                if (_popupPending)
                {
                    ResolveColdLaunch();
                }

                return 0;
            }

            if (msg == Native.WM_TIMER && wParam == PopupTimerId)
            {
                if (_popupPending)
                {
                    ResolveColdLaunch();
                }

                return 0;
            }

            if (msg == Native.WM_OPENUI)
            {
                LaunchUi("--launcher");
                return 0;
            }

            if (msg == Native.WM_QUITTRAY)
            {
                // The installer is retiring this tray for an upgrade: tear down cleanly (WM_DESTROY removes the icon).
                Native.DestroyWindow(hWnd);
                return 0;
            }

            if (msg == _taskbarCreatedMsg && _taskbarCreatedMsg != 0)
            {
                AddOrModifyIcon(Native.NIM_ADD);
                return 0;
            }

            if (msg == Native.WM_DESTROY)
            {
                RemoveIcon();
                Native.PostQuitMessage(0);
                return 0;
            }
        }
        catch
        {
            // A message handler must never throw across the native boundary.
        }

        return Native.DefWindowProcW(hWnd, msg, wParam, lParam);
    }

    private static void OnCommand(int id, nint hWnd)
    {
        switch (id)
        {
            case Native.ID_OPEN:
                LaunchUi("--launcher");
                break;
            case Native.ID_CONNECT:
                AgentLink.SendConnect();
                break;
            case Native.ID_DISCONNECT:
                // Disconnect leaves the tray resident.
                AgentLink.SendDisconnect();
                break;
            case Native.ID_EXIT:
                // Exit tears the tunnel down (if up), closes any open GUI window, and unloads the tray.
                if (_current != 0)
                {
                    AgentLink.SendDisconnect();
                }

                CloseUi();
                Native.DestroyWindow(hWnd);
                break;
        }
    }

    private static void SetState(int state, bool connectFailed)
    {
        if (state < 0 || state > 2 || _icons.Length != 3)
        {
            return;
        }

        // Balloon on real transitions only, never on the first snapshot (a tray restart over a live tunnel must
        // not fire a spurious notice). Connect start is the 0->connecting edge. The agent reports "disconnecting"
        // as the same transitioning state as "connecting", so a 1->0 edge alone does not mean a failure: it is a
        // failed dial when the transition opened at disconnected, a dropped tunnel when it opened at connected -
        // but both only when the agent latched a connect failure (connectFailed). A user disconnect (tray menu or
        // the GUI's button) clears that flag, so a deliberate teardown stays silent (#192).
        var justConnecting = _stateInitialized && state == 1 && _current == 0;
        var justConnected = _stateInitialized && state == 2 && _current != 2;
        var justFailed = _stateInitialized && state == 0 && _current == 1 && _transitionFrom == 0 && connectFailed;
        var justDropped = _stateInitialized && state == 0 && _current == 1 && _transitionFrom == 2 && connectFailed;

        // Records which edge opened the transition; skipped on the first snapshot, whose origin is unknown.
        if (_stateInitialized && state == 1 && _current != 1)
        {
            _transitionFrom = _current;
        }

        _current = state;
        _stateInitialized = true;
        AddOrModifyIcon(Native.NIM_MODIFY);

        // The GUI animates the connection itself, so only surface a balloon when the GUI is not the active window
        // (auto-connect with no window, tray menu, or after the launcher has closed).
        if (!IsUiForeground())
        {
            if (justConnected)
            {
                ShowBalloon("AmneziaGeo", Labels.ConnectedInfo);
            }
            else if (justConnecting)
            {
                ShowBalloon("AmneziaGeo", Labels.ConnectingInfo);
            }
            else if (justFailed)
            {
                ShowBalloon("AmneziaGeo", Labels.ConnectFailedInfo, Native.NIIF_WARNING);
            }
            else if (justDropped)
            {
                ShowBalloon("AmneziaGeo", Labels.DisconnectedInfo, Native.NIIF_WARNING);
            }
        }
    }

    // Whether the foreground window belongs to the GUI process; when it does, its own animation covers the state
    // change and a balloon would be noise.
    private static bool IsUiForeground()
    {
        var fg = Native.GetForegroundWindow();
        if (fg == 0)
        {
            return false;
        }

        Native.GetWindowThreadProcessId(fg, out var pid);
        if (pid == 0)
        {
            return false;
        }

        try
        {
            using var process = Process.GetProcessById((int)pid);
            return string.Equals(process.ProcessName, "AmneziaGeo.Windows.Ui", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    // Cold launch, once: open the lightweight launcher (More expands it to the full console). Post-install
    // auto-connect with an active profile dials straight away and stays resident, no window (#188).
    private static void ResolveColdLaunch()
    {
        _popupPending = false;
        Native.KillTimer(_hwnd, PopupTimerId);

        if (_autoConnect && AgentLink.HasActiveProfile)
        {
            AgentLink.SendConnect();
        }
        else
        {
            LaunchUi("--launcher");
        }
    }

    private static void ShowMenuAtCursor()
    {
        Native.GetCursorPos(out var pt);
        ShowMenuAt(pt.x, pt.y, Native.TPM_RIGHTBUTTON);
    }

    private static void ShowMenuAt(int x, int y, uint alignFlags)
    {
        Native.SetForegroundWindow(_hwnd);

        var menu = BuildMenu();
        Native.TrackPopupMenu(menu, alignFlags, x, y, 0, _hwnd, 0);
        // Classic dismissal fix: post a benign message so the menu closes when clicked away.
        Native.PostMessageW(_hwnd, Native.WM_NULL, 0, 0);
        Native.DestroyMenu(menu);
    }

    // Builds the menu for the current state: Open (launcher) always; Connect (grey without an active profile)
    // when down, Disconnect when up; Exit always.
    private static nint BuildMenu()
    {
        var menu = Native.CreatePopupMenu();
        Native.AppendMenuW(menu, Native.MF_STRING, (nuint)Native.ID_OPEN, Labels.Open);
        Native.AppendMenuW(menu, Native.MF_SEPARATOR, 0, null);

        if (_current == 0)
        {
            var connectFlags = AgentLink.HasActiveProfile ? Native.MF_STRING : Native.MF_STRING | Native.MF_GRAYED;
            Native.AppendMenuW(menu, connectFlags, (nuint)Native.ID_CONNECT, Labels.Connect);
        }
        else
        {
            Native.AppendMenuW(menu, Native.MF_STRING, (nuint)Native.ID_DISCONNECT, Labels.Disconnect);
        }

        Native.AppendMenuW(menu, Native.MF_SEPARATOR, 0, null);
        // Signpost that quitting drops a live tunnel, so protection is never lost without notice.
        Native.AppendMenuW(menu, Native.MF_STRING, (nuint)Native.ID_EXIT, _current != 0 ? Labels.ExitConnected : Labels.Exit);
        return menu;
    }

    private static void AddOrModifyIcon(uint dwMessage)
    {
        var nid = new Native.NOTIFYICONDATAW
        {
            cbSize = (uint)sizeof(Native.NOTIFYICONDATAW),
            hWnd = _hwnd,
            uID = 1,
            uFlags = Native.NIF_MESSAGE | Native.NIF_ICON | Native.NIF_TIP,
            uCallbackMessage = Native.WM_TRAY,
            hIcon = _icons.Length == 3 ? _icons[_current] : 0,
        };
        SetTip(&nid, TipForState());
        Native.Shell_NotifyIconW(dwMessage, ref nid);
    }

    private static void RemoveIcon()
    {
        var nid = new Native.NOTIFYICONDATAW
        {
            cbSize = (uint)sizeof(Native.NOTIFYICONDATAW),
            hWnd = _hwnd,
            uID = 1,
        };
        Native.Shell_NotifyIconW(Native.NIM_DELETE, ref nid);
    }

    private static void SetTip(Native.NOTIFYICONDATAW* nid, string text)
    {
        var count = Math.Min(text.Length, 127);
        for (var i = 0; i < count; i++)
        {
            nid->szTip[i] = text[i];
        }

        nid->szTip[count] = '\0';
    }

    // Pops a system balloon (a toast on Win10/11) on the existing icon.
    private static void ShowBalloon(string title, string text, uint infoFlags = Native.NIIF_INFO)
    {
        if (!AgentLink.ShowNotifications)
        {
            return;
        }

        if (_icons.Length != 3)
        {
            return;
        }

        var nid = new Native.NOTIFYICONDATAW
        {
            cbSize = (uint)sizeof(Native.NOTIFYICONDATAW),
            hWnd = _hwnd,
            uID = 1,
            uFlags = Native.NIF_INFO,
            dwInfoFlags = infoFlags,
        };
        SetInfo(&nid, title, text);
        Native.Shell_NotifyIconW(Native.NIM_MODIFY, ref nid);
    }

    private static void SetInfo(Native.NOTIFYICONDATAW* nid, string title, string text)
    {
        var titleCount = Math.Min(title.Length, 63);
        for (var i = 0; i < titleCount; i++)
        {
            nid->szInfoTitle[i] = title[i];
        }

        nid->szInfoTitle[titleCount] = '\0';

        var infoCount = Math.Min(text.Length, 255);
        for (var i = 0; i < infoCount; i++)
        {
            nid->szInfo[i] = text[i];
        }

        nid->szInfo[infoCount] = '\0';
    }

    // Tooltip tracks the tunnel state; the transitional state names the direction from the edge that opened it.
    private static string TipForState()
    {
        return _current switch
        {
            2 => $"AmneziaGeo - {Labels.StatusConnected}",
            0 => $"AmneziaGeo - {Labels.StatusDisconnected}",
            _ => $"AmneziaGeo - {(_transitionFrom == 2 ? Labels.StatusDisconnecting : Labels.StatusConnecting)}",
        };
    }

    // Mirrors the installer's own GUI shutdown (StopRunningApp): WM_CLOSE every open GUI window - both the
    // launcher and the full window (#189) - then a forced kill only if a process hangs, so no window left by
    // --launcher or the full GUI outlives the tray it was supposed to exit with.
    private static void CloseUi()
    {
        var processes = Process.GetProcessesByName("AmneziaGeo.Windows.Ui");
        if (processes.Length == 0)
        {
            return;
        }

        _uiPids = new HashSet<uint>();
        foreach (var p in processes)
        {
            try
            {
                _uiPids.Add((uint)p.Id);
            }
            catch
            {
            }
        }

        Native.EnumWindows((nint)(delegate* unmanaged[Stdcall]<nint, nint, int>)&EnumCloseUiWindow, 0);

        foreach (var p in processes)
        {
            try
            {
                if (!p.WaitForExit(5000))
                {
                    p.Kill();
                    p.WaitForExit(5000);
                }
            }
            catch
            {
            }
            finally
            {
                p.Dispose();
            }
        }
    }

    // Closes every visible top-level window owned by a targeted GUI process.
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static int EnumCloseUiWindow(nint hWnd, nint lParam)
    {
        Native.GetWindowThreadProcessId(hWnd, out var pid);
        if (_uiPids.Contains(pid) && Native.IsWindowVisible(hWnd))
        {
            Native.PostMessageW(hWnd, Native.WM_CLOSE, 0, 0);
        }

        return 1;
    }

    private static void LaunchUi(string? arg = null)
    {
        try
        {
            var exe = Path.Combine(AppContext.BaseDirectory, "AmneziaGeo.Windows.Ui.exe");
            var psi = new ProcessStartInfo(exe)
            {
                UseShellExecute = false,
                WorkingDirectory = AppContext.BaseDirectory,
            };
            if (!string.IsNullOrEmpty(arg))
            {
                psi.ArgumentList.Add(arg);
            }

            Process.Start(psi);
        }
        catch
        {
        }
    }

    // Draws the app glyph (white power symbol on a state-coloured disc) as an HICON: a 32bpp colour bitmap plus
    // a 1bpp mask that carves the disc shape.
    private static nint MakeStateIcon(byte r, byte g, byte b)
    {
        const int size = 32;
        const double c = 15.5;
        const double discR = 15.0;
        // Power glyph: an open ring (gap at top) plus a vertical stroke through the gap.
        const double ringOuter = 7.5;
        const double ringInner = 5.0;
        const double gapHalf = 2.3;
        const double barHalf = 1.35;
        const double barTop = -9.0;
        const double barBottom = 0.5;

        var color = new uint[size * size];
        var mask = new byte[size * (size / 8)];
        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                var dx = x - c;
                var dy = y - c;
                var d = Math.Sqrt((dx * dx) + (dy * dy));
                if (d > discR)
                {
                    mask[(y * (size / 8)) + (x >> 3)] |= (byte)(0x80 >> (x & 7));
                    continue;
                }

                var inRing = d >= ringInner && d <= ringOuter && !(dy < 0 && Math.Abs(dx) < gapHalf);
                var inBar = Math.Abs(dx) <= barHalf && dy >= barTop && dy <= barBottom;
                var white = inRing || inBar;
                var pr = white ? (byte)255 : r;
                var pg = white ? (byte)255 : g;
                var pb = white ? (byte)255 : b;
                color[(y * size) + x] = 0xFF000000u | ((uint)pr << 16) | ((uint)pg << 8) | pb;
            }
        }

        nint hColor;
        nint hMask;
        fixed (uint* colorBits = color)
        {
            hColor = Native.CreateBitmap(size, size, 1, 32, (nint)colorBits);
        }

        fixed (byte* maskBits = mask)
        {
            hMask = Native.CreateBitmap(size, size, 1, 1, (nint)maskBits);
        }

        var info = new Native.ICONINFO { fIcon = 1, hbmMask = hMask, hbmColor = hColor };
        var hIcon = Native.CreateIconIndirect(ref info);
        Native.DeleteObject(hColor);
        Native.DeleteObject(hMask);
        return hIcon;
    }
}
