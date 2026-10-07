using System.Diagnostics;
using System.Globalization;
using AmneziaGeo.Ipc;
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

    /// <summary>
    /// Opens the firewall at the addresses of the machine inside the tunnel for the signal of the server to
    /// disconnect, from the server alone, and returns whether the rule stands.
    /// </summary>
    public static bool AllowSignal(string name, SignalPlace signal, ILogger logger)
    {
        RemoveSignal(name, logger);
        var local = string.Join(',', signal.Hosts);
        var remote = string.Join(',', signal.Sources);
        if (!Netsh(SignalRule(name, local, remote, signal.Port), logger))
        {
            logger.LogWarning("{Name}: the firewall rule for the signal of the server to disconnect could not be written, so the server may not be able to take this tunnel down", name);
            return false;
        }

        logger.LogInformation("{Name}: the server takes this tunnel down by a signal to port {Port} at {Addresses}, let in from {Sources} alone", name, signal.Port, local, remote);
        return true;
    }

    // The rule of the signal as netsh takes it: one port at these addresses, from the server alone.
    internal static string SignalRule(string name, string local, string remote, int port) =>
        string.Create(CultureInfo.InvariantCulture, $"advfirewall firewall add rule name=\"{SignalRuleName(name)}\" dir=in action=allow protocol=TCP localport={port} localip={local} remoteip={remote} profile=any");

    /// <summary>
    /// Drops the rule of the signal.
    /// </summary>
    public static void RemoveSignal(string name, ILogger logger)
    {
        Netsh($"advfirewall firewall delete rule name=\"{SignalRuleName(name)}\"", logger);
    }

    private static string SignalRuleName(string name) => $"AmneziaGeo signal: {name}";

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
