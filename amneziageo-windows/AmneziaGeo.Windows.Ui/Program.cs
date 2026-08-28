using AmneziaGeo.Dal;
using Avalonia;
using System.Runtime.InteropServices;

namespace AmneziaGeo.Windows.Ui;

/// <summary>
/// Desktop UI entry point.
/// </summary>
public static partial class Program
{
    // Stable taskbar identity.
    private const string AppUserModelId = "AmneziaGeo.AmneziaGeo";

    [STAThread]
    private static void Main(string[] args)
    {
        SetAppUserModelId();

        OpenLog();
        ClientLog.Info($"GUI starting: pid {Environment.ProcessId}, args [{string.Join(' ', args)}]");

        // Single-instance: a second launch surfaces the existing window, asks it to download (--update) or
        // install (--apply) the update, or raises the tunnel-takeover prompt (--takeover), then exits.
        var requestUpdate = Array.IndexOf(args, "--update") >= 0;
        var requestApply = Array.IndexOf(args, "--apply") >= 0;
        var requestTakeover = Array.IndexOf(args, "--takeover") >= 0;
        if (!SingleInstance.TryAcquire(requestUpdate, requestApply, requestTakeover))
        {
            ClientLog.Flush();
            return;
        }

        // A crash on the UI thread kills the process before the exit line below, leaving no trace at all.
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            ClientLog.Error("GUI crashed", e.ExceptionObject as Exception);
            ClientLog.Flush();
        };

        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            ClientLog.Error("background task failed", e.Exception);
            e.SetObserved();
        };

        try
        {
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        catch (Exception ex)
        {
            ClientLog.Error("GUI crashed", ex);
            ClientLog.Flush();
            throw;
        }

        ClientLog.Info("GUI exited");
        ClientLog.Flush();
    }

    // Binds the GUI to the shared log database, so a launch that never shows a window leaves a record (#209).
    private static void OpenLog()
    {
        var path = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AmneziaGeo",
            "logs",
            "log.db");
        ClientLog.Open(path, "Ui");
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
