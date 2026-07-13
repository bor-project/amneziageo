using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace AmneziaGeo.Windows.Tray;

/// <summary>
/// The tray anchor entry point: a hidden owner window drives a Shell notify icon, a background link keeps the
/// agent tunnel alive and feeds the icon its colour, and the context menu opens the GUI, connects, disconnects,
/// or exits. A cold launch shows the menu as a popup so an experienced user can connect without the heavy GUI.
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

    [STAThread]
    private static int Main()
    {
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

        AgentLink.Start(state => Native.PostMessageW(_hwnd, Native.WM_STATE, (nuint)state, 0));
        SingleInstance.ListenForActivation(_hwnd, Native.WM_OPENUI);
        SingleInstance.ListenForQuit(_hwnd, Native.WM_QUITTRAY);

        // Cold launch: bring up only the tray, then pop the menu once the first snapshot lands (or after a short
        // fallback), so the user connects/opens without paying for the heavy GUI.
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
                    LaunchUi();
                }
                else if (ev == Native.WM_RBUTTONUP)
                {
                    ShowMenuAtCursor();
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
                SetState((int)wParam);
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
                LaunchUi();
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
                LaunchUi();
                break;
            case Native.ID_CONNECT:
                AgentLink.SendConnect();
                break;
            case Native.ID_DISCONNECT:
                // Disconnect leaves the tray resident.
                AgentLink.SendDisconnect();
                break;
            case Native.ID_EXIT:
                // Exit tears the tunnel down (if up) and unloads the tray.
                if (_current != 0)
                {
                    AgentLink.SendDisconnect();
                }

                Native.DestroyWindow(hWnd);
                break;
        }
    }

    private static void SetState(int state)
    {
        if (state < 0 || state > 2 || _icons.Length != 3)
        {
            return;
        }

        _current = state;
        AddOrModifyIcon(Native.NIM_MODIFY);
    }

    // Cold launch, once: with an active profile pop the menu so the user can connect without the GUI; with
    // nothing selected (fresh install / reset / all removed) the menu only adds a step, so open the GUI instead.
    private static void ResolveColdLaunch()
    {
        _popupPending = false;
        Native.KillTimer(_hwnd, PopupTimerId);

        if (AgentLink.HasActiveProfile)
        {
            ShowMenuPopup();
        }
        else
        {
            LaunchUi();
        }
    }

    private static void ShowMenuPopup()
    {
        var work = default(Native.RECT);
        if (Native.SystemParametersInfoW(Native.SPI_GETWORKAREA, 0, ref work, 0))
        {
            ShowMenuAt(work.right, work.bottom, Native.TPM_RIGHTALIGN | Native.TPM_BOTTOMALIGN);
        }
        else
        {
            ShowMenuAtCursor();
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

    // Builds the menu for the current state: Open always; Connect (grey without an active profile) when down,
    // Disconnect when up; Exit always.
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
        Native.AppendMenuW(menu, Native.MF_STRING, (nuint)Native.ID_EXIT, Labels.Exit);
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
        SetTip(&nid, "AmneziaGeo");
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

    private static void LaunchUi()
    {
        try
        {
            var exe = Path.Combine(AppContext.BaseDirectory, "AmneziaGeo.Windows.Ui.exe");
            Process.Start(new ProcessStartInfo(exe)
            {
                UseShellExecute = false,
                WorkingDirectory = AppContext.BaseDirectory,
            });
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
