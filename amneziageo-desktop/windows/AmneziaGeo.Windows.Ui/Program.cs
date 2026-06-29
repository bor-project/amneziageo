using Avalonia;
using System.Runtime.InteropServices;

namespace AmneziaGeo.Windows.Ui;

/// <summary>
/// Desktop UI entry point.
/// </summary>
public static partial class Program
{
    // Stable taskbar identity across updates.
    private const string AppUserModelId = "AmneziaGeo.AmneziaGeo";

    [STAThread]
    private static void Main(string[] args)
    {
        SetAppUserModelId();

        // Single-instance: a second launch must not open a duplicate window. It surfaces the already
        // running window and exits; only the first instance starts the app.
        if (!SingleInstance.TryAcquire())
        {
            return;
        }

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    private static void SetAppUserModelId()
    {
        try
        {
            SetCurrentProcessExplicitAppUserModelID(AppUserModelId);
        }
        catch
        {
        }
    }

    [LibraryImport("shell32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial void SetCurrentProcessExplicitAppUserModelID(string appId);

    /// <summary>
    /// Configures the Avalonia application.
    /// </summary>
    public static AppBuilder BuildAvaloniaApp()
    {
        return AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace();
    }
}
