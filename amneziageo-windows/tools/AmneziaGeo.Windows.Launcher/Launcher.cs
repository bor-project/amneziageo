using System.Diagnostics;
using AmneziaGeo.Windows.App;
using Avalonia;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using UiProgram = AmneziaGeo.Windows.Ui.Program;

namespace AmneziaGeo.Windows.Launcher;

/// <summary>
/// Hosts the backend agent in-process and launches the tray for the standard desktop flow.
/// </summary>
internal sealed class Launcher(ILogger<Launcher> logger, IOptions<LauncherOptions> options)
{
    /// <summary>
    /// Launches the configured components in-process and blocks until they exit.
    /// </summary>
    public int Run(string[] args)
    {
        var launch = LaunchOptions.Resolve(options.Value, args);

        if (!launch.RunService && !launch.RunUi)
        {
            logger.LogWarning("nothing to launch");
            return 0;
        }

        using (var cts = new CancellationTokenSource())
        {
            Task<int>? agentTask = null;

            if (launch.RunService)
            {
                if (launch.ConfigPath is not null)
                {
                    var name = launch.Target ?? Path.GetFileNameWithoutExtension(launch.ConfigPath);
                    logger.LogInformation("registering config '{Name}' from {Path}", name, launch.ConfigPath);
                    AppEntry.RunAsync(["config-add", name, launch.ConfigPath]).GetAwaiter().GetResult();
                }

                string[] agentArgs = launch.Target is { } target ? ["--agent", target] : ["--agent"];
                logger.LogInformation("starting backend (agent, in-process)");
                agentTask = Task.Run(() => AppEntry.RunAsync(agentArgs, cts.Token));
            }

            if (launch.RunUi)
            {
                ScheduledTaskInstaller.EnsureLogonTask(logger);
                RunDesktop(args);
                cts.Cancel();
            }

            if (agentTask is not null)
            {
                try
                {
                    agentTask.GetAwaiter().GetResult();
                }
                catch (OperationCanceledException)
                {
                }
            }
        }

        return 0;
    }

    // Launches the resident tray for the standard cold-launch flow; opens the GUI in-process when the tray exe
    // was not staged next to the launcher.
    private void RunDesktop(string[] args)
    {
        var tray = Path.Combine(AppContext.BaseDirectory, "AmneziaGeo.Windows.Tray.exe");
        if (File.Exists(tray))
        {
            logger.LogInformation("starting tray");
            using var process = Process.Start(new ProcessStartInfo(tray)
            {
                UseShellExecute = false,
                WorkingDirectory = AppContext.BaseDirectory,
            });
            process?.WaitForExit();
            return;
        }

        logger.LogWarning("tray exe not staged next to the launcher; opening the GUI in-process");
        UiProgram.BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }
}
