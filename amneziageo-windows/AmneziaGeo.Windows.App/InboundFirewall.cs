using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace AmneziaGeo.Windows.App;

/// <summary>
/// The machine's own firewall rule for inbound access: allows what arrives from the ranges a config advertises.
/// </summary>
internal static class InboundFirewall
{
    /// <summary>
    /// Opens the firewall for the given ranges and returns whether the rule stands.
    /// </summary>
    public static bool Allow(string name, IReadOnlyList<string> ranges, ILogger logger)
    {
        if (ranges.Count == 0)
        {
            return false;
        }

        Remove(name, logger);
        var remote = string.Join(',', ranges);
        if (!Netsh($"advfirewall firewall add rule name=\"{RuleName(name)}\" dir=in action=allow remoteip={remote} profile=any", logger))
        {
            logger.LogWarning("{Name}: the firewall rule for access from the tunnel could not be written, so this machine may stay unreachable at its tunnel address", name);
            return false;
        }

        logger.LogInformation("{Name}: this machine answers what arrives from {Ranges} at its address inside the tunnel", name, remote);
        return true;
    }

    /// <summary>
    /// Drops the rule.
    /// </summary>
    public static void Remove(string name, ILogger logger)
    {
        Netsh($"advfirewall firewall delete rule name=\"{RuleName(name)}\"", logger);
    }

    private static string RuleName(string name) => $"AmneziaGeo inbound: {name}";

    private static bool Netsh(string arguments, ILogger logger)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo("netsh", arguments)
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });
            if (process is null)
            {
                return false;
            }

            process.WaitForExit(10_000);
            return process.HasExited && process.ExitCode == 0;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "the firewall rule for access from the tunnel could not be written");
            return false;
        }
    }
}
