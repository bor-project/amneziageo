using WixToolset.BootstrapperApplicationApi;

namespace AmneziaGeo.Windows.Installer;

/// <summary>
/// Entry point for the AmneziaGeo bootstrapper application. In WiX v5 a managed out-of-process BA is
/// a normal WinExe started via <see cref="ManagedBootstrapperApplication.Run"/>; the host wires the
/// engine connection and runs <see cref="BootstrapperApplication.Run"/> on a UI-capable thread.
/// </summary>
internal static class Program
{
    private static int Main()
    {
        var application = new InstallerBootstrapper();
        ManagedBootstrapperApplication.Run(application);
        return 0;
    }
}
