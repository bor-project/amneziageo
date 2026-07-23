using System.Runtime.InteropServices;

namespace AmneziaGeo.Windows.Tray;

/// <summary>
/// Win32 interop for the notify icon, its hidden owner window, the context menu, and the GDI-drawn status icon.
/// </summary>
internal static class Native
{
    // Window messages.
    public const uint WM_NULL = 0x0000;
    public const uint WM_DESTROY = 0x0002;
    public const uint WM_CLOSE = 0x0010;
    public const uint WM_COMMAND = 0x0111;
    public const uint WM_LBUTTONUP = 0x0202;
    public const uint WM_RBUTTONUP = 0x0205;

    // Balloon events, delivered as the tray callback's lParam - a different space from the window messages
    // below, so NIN_BALLOONTIMEOUT sharing a value with WM_QUITTRAY is harmless.
    public const uint NIN_BALLOONUSERCLICK = 0x0400 + 5;

    // Sent when the balloon leaves the screen, by timeout or by the user closing it.
    public const uint NIN_BALLOONTIMEOUT = 0x0400 + 4;

    // App-private messages: tray callback, agent-state change, "open UI" activation from a second launch,
    // and an app-update availability change from the agent link.
    public const uint WM_TRAY = 0x0400 + 1;   // WM_USER + 1
    public const uint WM_STATE = 0x0400 + 2;
    public const uint WM_OPENUI = 0x0400 + 3;
    public const uint WM_QUITTRAY = 0x0400 + 4;
    public const uint WM_UPDATE = 0x0400 + 6;

    // An update download completed (the agent link reports it), so the tray can announce "ready to install".
    public const uint WM_UPDATEDOWNLOADED = 0x0400 + 7;

    // An update download failed (the agent link reports it), so the tray can warn (#8).
    public const uint WM_UPDATEFAILED = 0x0400 + 8;

    // A manual update check finished (the agent link reports it), so the tray can announce the up-to-date
    // result (#15). wParam: 0 up to date, 1 update available, 2 check failed.
    public const uint WM_CHECKDONE = 0x0400 + 9;

    // Menu command ids.
    public const int ID_OPEN = 1;
    public const int ID_CONNECT = 2;
    public const int ID_DISCONNECT = 3;
    public const int ID_EXIT = 4;
    public const int ID_CHECKUPDATE = 5;
    public const int ID_UPDATE = 6;
    public const int ID_INSTALL = 7;
    public const int ID_CANCELDOWNLOAD = 8;

    // Shell_NotifyIcon.
    public const uint NIM_ADD = 0;
    public const uint NIM_MODIFY = 1;
    public const uint NIM_DELETE = 2;
    public const uint NIF_MESSAGE = 0x01;
    public const uint NIF_ICON = 0x02;
    public const uint NIF_TIP = 0x04;
    public const uint NIF_INFO = 0x10;

    // Balloon icon: the standard information glyph.
    public const uint NIIF_INFO = 0x01;

    // Balloon icon: the standard warning glyph.
    public const uint NIIF_WARNING = 0x02;

    public const uint WM_TIMER = 0x0113;

    // Menu flags.
    public const uint MF_STRING = 0x0000;
    public const uint MF_GRAYED = 0x0001;
    public const uint MF_SEPARATOR = 0x0800;
    public const uint TPM_RIGHTBUTTON = 0x0002;
    public const uint TPM_RIGHTALIGN = 0x0008;
    public const uint TPM_BOTTOMALIGN = 0x0020;

    // SystemParametersInfo: the desktop work area (screen minus taskbar), for the auto-popup anchor.
    public const uint SPI_GETWORKAREA = 0x0030;

    // MessageBox: a Yes/No confirmation with a warning glyph, kept above the foreground for the exit prompt.
    public const uint MB_YESNO = 0x00000004;
    public const uint MB_ICONWARNING = 0x00000030;
    public const uint MB_TOPMOST = 0x00040000;
    public const int IDYES = 6;

    [StructLayout(LayoutKind.Sequential)]
    public struct WNDCLASSEXW
    {
        public uint cbSize;
        public uint style;
        public nint lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public nint hInstance;
        public nint hIcon;
        public nint hCursor;
        public nint hbrBackground;
        public nint lpszMenuName;
        public nint lpszClassName;
        public nint hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MSG
    {
        public nint hwnd;
        public uint message;
        public nuint wParam;
        public nint lParam;
        public uint time;
        public int pt_x;
        public int pt_y;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT
    {
        public int x;
        public int y;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int left;
        public int top;
        public int right;
        public int bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct ICONINFO
    {
        public int fIcon;
        public int xHotspot;
        public int yHotspot;
        public nint hbmMask;
        public nint hbmColor;
    }

    public const uint BI_RGB = 0;
    public const uint DIB_RGB_COLORS = 0;

    [StructLayout(LayoutKind.Sequential)]
    public struct BITMAPINFOHEADER
    {
        public uint biSize;
        public int biWidth;
        public int biHeight;
        public ushort biPlanes;
        public ushort biBitCount;
        public uint biCompression;
        public uint biSizeImage;
        public int biXPelsPerMeter;
        public int biYPelsPerMeter;
        public uint biClrUsed;
        public uint biClrImportant;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public unsafe struct NOTIFYICONDATAW
    {
        public uint cbSize;
        public nint hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public nint hIcon;
        public fixed char szTip[128];
        public uint dwState;
        public uint dwStateMask;
        public fixed char szInfo[256];
        public uint uVersion;
        public fixed char szInfoTitle[64];
        public uint dwInfoFlags;
        public Guid guidItem;
        public nint hBalloonIcon;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern nint GetModuleHandleW(string? lpModuleName);

    [DllImport("kernel32.dll")]
    public static extern ushort GetUserDefaultUILanguage();

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern ushort RegisterClassExW(ref WNDCLASSEXW lpwcx);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern nint CreateWindowExW(
        uint dwExStyle, string lpClassName, string lpWindowName, uint dwStyle,
        int x, int y, int nWidth, int nHeight, nint hWndParent, nint hMenu, nint hInstance, nint lpParam);

    [DllImport("user32.dll")]
    public static extern nint DefWindowProcW(nint hWnd, uint msg, nuint wParam, nint lParam);

    [DllImport("user32.dll")]
    public static extern int DestroyWindow(nint hWnd);

    [DllImport("user32.dll")]
    public static extern void PostQuitMessage(int nExitCode);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool PostMessageW(nint hWnd, uint msg, nuint wParam, nint lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int GetMessageW(out MSG lpMsg, nint hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

    [DllImport("user32.dll")]
    public static extern int TranslateMessage(ref MSG lpMsg);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern nint DispatchMessageW(ref MSG lpMsg);

    [DllImport("user32.dll")]
    public static extern nint CreatePopupMenu();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool AppendMenuW(nint hMenu, uint uFlags, nuint uIDNewItem, string? lpNewItem);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool TrackPopupMenu(nint hMenu, uint uFlags, int x, int y, int nReserved, nint hWnd, nint prcRect);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool DestroyMenu(nint hMenu);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetForegroundWindow(nint hWnd);

    // Grants any process the right to set the foreground window, so the UI it launches can open above the current app.
    public const uint ASFW_ANY = 0xFFFFFFFF;

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool AllowSetForegroundWindow(uint dwProcessId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int MessageBoxW(nint hWnd, string lpText, string lpCaption, uint uType);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll")]
    public static extern nint GetForegroundWindow();

    [DllImport("user32.dll", SetLastError = true)]
    public static extern uint GetWindowThreadProcessId(nint hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool EnumWindows(nint lpEnumFunc, nint lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool IsWindowVisible(nint hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool IsIconic(nint hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SystemParametersInfoW(uint uiAction, uint uiParam, ref RECT pvParam, uint fWinIni);

    [DllImport("user32.dll")]
    public static extern nuint SetTimer(nint hWnd, nuint nIDEvent, uint uElapse, nint lpTimerFunc);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool KillTimer(nint hWnd, nuint uIDEvent);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern uint RegisterWindowMessageW(string lpString);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool Shell_NotifyIconW(uint dwMessage, ref NOTIFYICONDATAW lpData);

    [DllImport("user32.dll")]
    public static extern nint CreateIconIndirect(ref ICONINFO piconinfo);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool DestroyIcon(nint hIcon);

    [DllImport("gdi32.dll")]
    public static extern nint CreateBitmap(int nWidth, int nHeight, uint nPlanes, uint nBitCount, nint lpBits);

    [DllImport("gdi32.dll")]
    public static extern nint CreateDIBSection(nint hdc, ref BITMAPINFOHEADER pbmi, uint usage, out nint ppvBits, nint hSection, uint offset);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool DeleteObject(nint ho);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    public static extern int SetCurrentProcessExplicitAppUserModelID(string appId);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
    public static extern int RegGetValueW(nint hkey, string lpSubKey, string lpValue, uint dwFlags, nint pdwType, ref uint pvData, ref uint pcbData);
}
