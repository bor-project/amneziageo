using Avalonia;

namespace AmneziaGeo.Windows.Ui;

/// <summary>
/// Desktop UI entry point.
/// </summary>
public static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        // Single-instance: a second launch must not open a duplicate window. It surfaces the already
        // running window and exits; only the first instance starts the app.
        if (!SingleInstance.TryAcquire())
        {
            return;
        }

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

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
