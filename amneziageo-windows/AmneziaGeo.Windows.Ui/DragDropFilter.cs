using System;
using System.Runtime.InteropServices;
using Avalonia.Controls;

namespace AmneziaGeo.Windows.Ui;

/// <summary>
/// Снимает UIPI-фильтр drag-and-drop с окна.
/// </summary>
internal static partial class DragDropFilter
{
    private const uint WmCopyGlobalData = 0x0049;
    private const uint WmCopyData = 0x004A;
    private const uint WmDropFiles = 0x0233;
    private const uint MsgfltAllow = 1;

    /// <summary>
    /// Разрешает drop-сообщения на HWND окна.
    /// </summary>
    public static void Allow(Window window)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var handle = window.TryGetPlatformHandle()?.Handle ?? 0;
        if (handle == 0)
        {
            return;
        }

        ChangeWindowMessageFilterEx(handle, WmDropFiles, MsgfltAllow, 0);
        ChangeWindowMessageFilterEx(handle, WmCopyData, MsgfltAllow, 0);
        ChangeWindowMessageFilterEx(handle, WmCopyGlobalData, MsgfltAllow, 0);
    }

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ChangeWindowMessageFilterEx(nint hwnd, uint message, uint action, nint changeFilterStruct);
}