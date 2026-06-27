using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace AmneziaGeo.Windows.Launcher;

/// <summary>
/// Registers the per-user logon scheduled task that auto-starts the elevated host without a UAC prompt.
/// Self-registration (rather than an MSI custom action) keeps it path-aware via the running exe and out of
/// the WiX deferred-action property dance; the MSI only removes the task on uninstall. No-op in Debug so a
/// dev <c>dotnet run</c> never lays down a machine task pointing at a bin path.
/// </summary>
internal static class ScheduledTaskInstaller
{
    private const string TaskName = "AmneziaGeo";

    /// <summary>
    /// Ensures the "AmneziaGeo" logon task exists and points at the running host. Idempotent (recreated
    /// with /f every run); best-effort - a failure only means no silent autostart, never blocks the app.
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

            // A logon-triggered, highest-privilege task so the tray host auto-starts elevated at the next
            // sign-in without a UAC prompt. Tied to the current (installing) user; a manual launch via the
            // shortcut still elevates through the requireAdministrator manifest.
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
