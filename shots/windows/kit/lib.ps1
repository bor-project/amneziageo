Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Drawing
Add-Type -ReferencedAssemblies System.Drawing -TypeDefinition @'
using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text;

public static class Win
{
    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left, Top, Right, Bottom; }

    public delegate bool EnumProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr lParam);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr hwnd);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int max);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetClassName(IntPtr hwnd, StringBuilder text, int max);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
    [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hwnd, int cmd);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int w, int h, uint flags);
    [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr hwnd, int attr, out RECT rect, int size);

    public static IntPtr[] WindowsOf(uint pid)
    {
        var found = new System.Collections.Generic.List<IntPtr>();
        EnumWindows((h, l) => { uint p; GetWindowThreadProcessId(h, out p); if (p == pid && IsWindowVisible(h)) { found.Add(h); } return true; }, IntPtr.Zero);
        return found.ToArray();
    }

    public static string Title(IntPtr hwnd)
    {
        var text = new StringBuilder(512);
        GetWindowText(hwnd, text, text.Capacity);
        return text.ToString();
    }

    public static string Class(IntPtr hwnd)
    {
        var text = new StringBuilder(256);
        GetClassName(hwnd, text, text.Capacity);
        return text.ToString();
    }

    public static RECT Outer(IntPtr hwnd)
    {
        RECT r;
        GetWindowRect(hwnd, out r);
        return r;
    }

    public static RECT Frame(IntPtr hwnd)
    {
        RECT r;
        if (DwmGetWindowAttribute(hwnd, 9, out r, Marshal.SizeOf(typeof(RECT))) != 0) { GetWindowRect(hwnd, out r); }
        return r;
    }

    public static Bitmap Grab(IntPtr hwnd)
    {
        RECT w; GetWindowRect(hwnd, out w);
        RECT f = Frame(hwnd);
        using (var full = new Bitmap(w.Right - w.Left, w.Bottom - w.Top, PixelFormat.Format32bppArgb))
        {
            using (var g = Graphics.FromImage(full))
            {
                var hdc = g.GetHdc();
                PrintWindow(hwnd, hdc, 2);
                g.ReleaseHdc(hdc);
            }
            var crop = new Rectangle(f.Left - w.Left, f.Top - w.Top, f.Right - f.Left, f.Bottom - f.Top);
            return full.Clone(crop, PixelFormat.Format32bppArgb);
        }
    }

    public static void Save(Bitmap bitmap, string path)
    {
        bitmap.Save(path, ImageFormat.Png);
    }

    public static Bitmap Compose(IntPtr main, IntPtr[] popups)
    {
        var baseFrame = Frame(main);
        var canvas = Grab(main);
        using (var g = Graphics.FromImage(canvas))
        {
            foreach (var popup in popups)
            {
                var f = Frame(popup);
                using (var part = Grab(popup)) { g.DrawImage(part, f.Left - baseFrame.Left, f.Top - baseFrame.Top, part.Width, part.Height); }
            }
        }
        return canvas;
    }
}
'@
[void][Win]::SetProcessDPIAware()

. (Join-Path $PSScriptRoot 'lib-fn.ps1')
