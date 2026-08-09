using Avalonia;

namespace AmneziaGeo.Linux.Ui;

/// <summary>
/// Desktop UI entry point.
/// </summary>
public static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        // Одно окно: второй запуск выводит открытое вперёд и уходит.
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
            // Draws dropdowns and menus inside the window instead of separate X surfaces.
            .With(new X11PlatformOptions { OverlayPopups = true })
            .LogToTrace();
    }
}
