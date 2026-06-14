using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace AmneziaGeo.Windows.Launcher;

/// <summary>
/// Starts the configured backend (agent, foreground) and desktop UI for debugging, replacing the scripts.
/// </summary>
internal sealed class Launcher(ILogger<Launcher> logger)
{
    /// <summary>
    /// Launches the selected processes and waits until they exit or Ctrl+C.
    /// </summary>
    public async Task<int> RunAsync(string[] args)
    {
        var options = LaunchOptions.Parse(args);
        var processes = new List<Process>();

        if (options.RunService)
        {
            if (options.Target is null)
            {
                logger.LogError("specify --target <balancer-or-config> or --config <path>");
                return 1;
            }

            var appExe = options.AppPath ?? LocateApp();
            if (appExe is null)
            {
                logger.LogError("could not locate AmneziaGeo.Windows.App.exe; pass --app <path>");
                return 1;
            }

            if (options.ConfigPath is not null)
            {
                logger.LogInformation("registering config '{Target}' from {Path}", options.Target, options.ConfigPath);
                RegisterConfig(appExe, options.Target, options.ConfigPath);
            }

            logger.LogInformation("starting backend (agent, foreground) for '{Target}'", options.Target);
            processes.Add(Start(appExe, $"--agent {options.Target}"));
        }

        if (options.RunUi)
        {
            var uiExe = LocateUi();
            if (uiExe is null)
            {
                logger.LogWarning("UI is not implemented yet (tasks #12/#13); skipping UI");
            }
            else
            {
                logger.LogInformation("starting UI");
                processes.Add(Start(uiExe, string.Empty));
            }
        }

        if (processes.Count == 0)
        {
            logger.LogWarning("nothing to launch");
            return 0;
        }

        using (var cts = new CancellationTokenSource())
        {
            Console.CancelKeyPress += (_, e) =>
            {
                e.Cancel = true;
                cts.Cancel();
            };

            await WaitAllAsync(processes, cts.Token);
        }

        foreach (var process in processes)
        {
            TryStop(process);
        }

        return 0;
    }

    private void RegisterConfig(string appExe, string target, string configPath)
    {
        using (var process = Start(appExe, $"config-add {target} \"{configPath}\""))
        {
            process.WaitForExit();
        }
    }

    private static async Task WaitAllAsync(IReadOnlyList<Process> processes, CancellationToken ct)
    {
        try
        {
            await Task.WhenAll(processes.Select(process => process.WaitForExitAsync(ct)));
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void TryStop(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "failed to stop process {Id}", process.Id);
        }
    }

    private static Process Start(string exe, string arguments)
    {
        var startInfo = new ProcessStartInfo(exe, arguments)
        {
            UseShellExecute = false,
        };
        return Process.Start(startInfo)!;
    }

    private static string? LocateApp()
    {
        var baseDir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var config = new DirectoryInfo(baseDir).Parent?.Name ?? "Debug";

        var dir = new DirectoryInfo(baseDir);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "AmneziaGeo.Windows.App")))
        {
            dir = dir.Parent;
        }

        if (dir is null)
        {
            return null;
        }

        var candidate = Path.Combine(dir.FullName, "AmneziaGeo.Windows.App", "bin", config, "net10.0", "AmneziaGeo.Windows.App.exe");
        return File.Exists(candidate) ? candidate : null;
    }

    private static string? LocateUi()
    {
        return null;
    }
}
