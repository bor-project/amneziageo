using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace AmneziaGeo.Windows.Launcher;

/// <summary>
/// Registers the per-user logon scheduled task.
/// </summary>
internal static class ScheduledTaskInstaller
{
    private const string TaskName = "AmneziaGeo";

    /// <summary>
    /// Ensure the logon task exists and points at the running host.
    /// </summary>
    public static void EnsureLogonTask(ILogger logger)
    {
#if DEBUG
        _ = logger;
#else
        try
        {
            var exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe))
            {
                return;
            }

            var psi = new ProcessStartInfo("schtasks.exe")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            foreach (var arg in new[] { "/create", "/tn", TaskName, "/tr", $"\"{exe}\"", "/sc", "onlogon", "/rl", "highest", "/f" })
            {
                psi.ArgumentList.Add(arg);
            }

            using var process = Process.Start(psi);
            if (process is null)
            {
                return;
            }

            process.WaitForExit(10000);
            if (process.ExitCode != 0)
            {
                logger.LogWarning("could not register logon autostart task (schtasks exit {Code})", process.ExitCode);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "registering logon autostart task failed");
        }
#endif
    }
}
