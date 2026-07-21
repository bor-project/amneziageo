using System.IO;
using System.Windows;
using AmneziaGeo.Localization;
using WixToolset.BootstrapperApplicationApi;

namespace AmneziaGeo.Windows.Installer;

/// <summary>
/// Bootstrapper application entry point.
/// </summary>
internal static class Program
{
    private static int Main()
    {
        Loc.Instance.ApplyStartupCulture(null);

        var application = new InstallerBootstrapper();

        var watchdog = new Thread(() => WatchForMissingEngine(application)) { IsBackground = true };
        watchdog.Start();

        ManagedBootstrapperApplication.Run(application);
        return 0;
    }

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
        }

        MessageBox.Show(
            Loc.Instance.Get("Installer_NotDirectRun"),
            "AmneziaGeo",
            MessageBoxButton.OK,
            MessageBoxImage.Information);

        Environment.Exit(2);
    }
}
