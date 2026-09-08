using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace AmneziaGeo.Windows.App;

/// <summary>
/// The machine's own firewall rule for inbound access: allows what arrives at its addresses inside the tunnel.
/// </summary>
internal static class InboundFirewall
{
    /// <summary>
    /// Opens the firewall at the given addresses for the ranges the tunnel carries and returns whether the rule
    /// stands. Nothing outside those ranges reaches the machine through it.
    /// </summary>
    public static bool Allow(string name, IReadOnlyList<string> addresses, IReadOnlyList<string> ranges, ILogger logger)
    {
        if (addresses.Count == 0 || ranges.Count == 0)
        {
            return false;
        }

        Remove(name, logger);
        var local = string.Join(',', addresses);
        var remote = string.Join(',', ranges);
        if (!Netsh(Rule(name, local, remote), logger))
        {
            logger.LogWarning("{Name}: the firewall rule for access from the tunnel could not be written, so this machine may stay unreachable at its tunnel address", name);
            return false;
        }

        logger.LogInformation("{Name}: this machine answers what arrives at {Addresses} from {Ranges} inside the tunnel", name, local, remote);
        return true;
    }

    // The rule as netsh takes it: what arrives at these addresses of the machine from these ranges alone.
    internal static string Rule(string name, string local, string remote) =>
        $"advfirewall firewall add rule name=\"{RuleName(name)}\" dir=in action=allow localip={local} remoteip={remote} profile=any";

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
