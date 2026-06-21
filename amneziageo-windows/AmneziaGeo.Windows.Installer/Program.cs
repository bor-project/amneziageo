using System.IO;
using System.Windows;
using WixToolset.BootstrapperApplicationApi;

namespace AmneziaGeo.Windows.Installer;

/// <summary>
/// Entry point for the AmneziaGeo bootstrapper application. In WiX v5 a managed out-of-process BA is
/// a normal WinExe started via <see cref="ManagedBootstrapperApplication.Run"/>; the host wires the
/// engine connection and runs <see cref="BootstrapperApplication.Run"/> on a UI-capable thread.
///
/// This exe is NOT the installer — it is only the installer's UI, launched by the Burn engine from
/// inside AmneziaGeoSetup.exe. Launched on its own (a double-click) it has no engine pipe to connect
/// to and <see cref="ManagedBootstrapperApplication.Run"/> blocks forever with no window, so a
/// watchdog catches the missing engine and points the user at the real setup instead of hanging.
/// </summary>
internal static class Program
{
    private static int Main()
    {
        var application = new InstallerBootstrapper();

        var watchdog = new Thread(() => WatchForMissingEngine(application)) { IsBackground = true };
        watchdog.Start();

        ManagedBootstrapperApplication.Run(application);
        return 0;
    }

    // The engine calls OnCreate within a fraction of a second of hosting us. If that has not happened
    // well after start-up we were launched directly, so tell the user what to run and bail out — Run()
    // would otherwise sit forever on the absent engine pipe.
    private static void WatchForMissingEngine(InstallerBootstrapper application)
    {
        Thread.Sleep(TimeSpan.FromSeconds(6));
        if (application.EngineConnected)
        {
            return;
        }

        try
        {
            File.AppendAllText(
                Path.Combine(Path.GetTempPath(), "AmneziaGeo.Installer.BA.log"),
                $"{DateTime.Now:O}  launched without a Burn engine (the BA exe was started directly); aborting.{Environment.NewLine}");
        }
        catch
        {
            // best effort
        }

        MessageBox.Show(
            "Это внутренний компонент установщика AmneziaGeo и не запускается напрямую.\n\n" +
            "Запустите AmneziaGeoSetup.exe — это и есть установщик.",
            "AmneziaGeo",
            MessageBoxButton.OK,
            MessageBoxImage.Information);

        Environment.Exit(2);
    }
}
