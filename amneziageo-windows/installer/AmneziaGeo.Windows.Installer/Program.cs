using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using AmneziaGeo.Localization;
using WixToolset.BootstrapperApplicationApi;

namespace AmneziaGeo.Windows.Installer;

/// <summary>
/// Bootstrapper application entry point.
/// </summary>
internal static class Program
{
    private const ushort ImageFileMachineArm64 = 0xAA64;

    private static int Main()
    {
        Loc.Instance.ApplyStartupCulture(null);

        if (!MachineFits())
        {
            MessageBox.Show(
                Loc.Instance.Get("Installer_WrongArchitecture"),
                "AmneziaGeo",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return 3;
        }

        var application = new InstallerBootstrapper();

        var watchdog = new Thread(() => WatchForMissingEngine(application)) { IsBackground = true };
        watchdog.Start();

        ManagedBootstrapperApplication.Run(application);
        return 0;
    }

    // Whether this build fits the machine it was started on. The tunnel driver is native and the emulator does
    // not carry it into the kernel, so an x64 build installs on an arm64 machine and never raises a tunnel.
    private static bool MachineFits()
    {
        if (!IsWow64Process2(GetCurrentProcess(), out _, out var native))
        {
            return true;
        }

        return native != ImageFileMachineArm64 || RuntimeInformation.ProcessArchitecture == Architecture.Arm64;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWow64Process2(IntPtr process, out ushort processMachine, out ushort nativeMachine);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

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
