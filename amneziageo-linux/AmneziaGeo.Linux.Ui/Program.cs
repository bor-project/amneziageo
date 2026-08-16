using System.Diagnostics;
using System.Runtime.InteropServices;
using Avalonia;

namespace AmneziaGeo.Linux.Ui;

/// <summary>
/// Desktop UI entry point.
/// </summary>
public static class Program
{
    private const int PrSetPtracer = 0x59616d61;

    [STAThread]
    private static void Main(string[] args)
    {
        WaitForDebugger();

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

    private static void WaitForDebugger()
    {
        var requested = Environment.GetEnvironmentVariable("AMNEZIAGEO_WAIT_DEBUGGER");
        if (requested is not ("1" or "on" or "true"))
        {
            return;
        }

        // Yama разрешает ptrace только родителю; отладчик стартует рядом.
        _ = prctl(PrSetPtracer, nuint.MaxValue, 0, 0, 0);

        Console.Error.WriteLine($"waiting for a debugger, pid {Environment.ProcessId}");
        var deadline = DateTime.UtcNow.AddMinutes(2);
        while (!Debugger.IsAttached && DateTime.UtcNow < deadline)
        {
            Thread.Sleep(200);
        }

        Console.Error.WriteLine(Debugger.IsAttached ? "debugger attached" : "no debugger came, running anyway");
    }

    [DllImport("libc", SetLastError = true)]
    private static extern int prctl(int option, nuint arg2, nuint arg3, nuint arg4, nuint arg5);
}
