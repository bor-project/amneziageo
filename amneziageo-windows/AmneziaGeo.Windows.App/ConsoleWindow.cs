using System.Runtime.InteropServices;

namespace AmneziaGeo.Windows.App;

/// <summary>
/// Hides the console this process was given. The exe is a console app because it is also the CLI, but the service,
/// agent and installer verbs are started from GUI parents (the bundle, the MSI, the tray), where that console is a
/// black window flashing on screen.
/// </summary>
internal static partial class ConsoleWindow
{
    private const int SwHide = 0;

    /// <summary>
    /// Hides the console window for a run nobody watches.
    /// </summary>
    public static void Hide()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        try
        {
            var window = GetConsoleWindow();
            if (window != IntPtr.Zero)
            {
                ShowWindow(window, SwHide);
            }
        }
        catch (DllNotFoundException)
        {
        }
        catch (EntryPointNotFoundException)
        {
        }
    }

    [LibraryImport("kernel32.dll")]
    private static partial IntPtr GetConsoleWindow();

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ShowWindow(IntPtr window, int command);
}
