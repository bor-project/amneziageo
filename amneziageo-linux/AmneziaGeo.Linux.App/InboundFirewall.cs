using System.Globalization;
using System.Text;

namespace AmneziaGeo.Linux.App;

/// <summary>
/// Holds connections opened from the tunnel off this machine. Own nftables table; the rules of other software
/// are never touched.
/// </summary>
internal static class InboundFirewall
{
    private const string Table = "amneziageo-inbound";

    // ICMPv6 discovery and the errors a live flow needs; without them IPv6 breaks on the tunnel.
    private const string KeepIcmpV6 =
        "{ destination-unreachable, packet-too-big, time-exceeded, parameter-problem, "
        + "nd-router-solicit, nd-router-advert, nd-neighbor-solicit, nd-neighbor-advert }";

    /// <summary>
    /// Drops what the tunnel opens towards this machine and returns whether the table stands; the flows this
    /// machine opened itself keep answering.
    /// </summary>
    public static async Task<bool> ApplyAsync(string iface, AgentLog log, CancellationToken ct)
    {
        var rules = new StringBuilder();
        rules.Append(CultureInfo.InvariantCulture, $"table inet {Table} {{\n");
        rules.Append("  chain input {\n");
        rules.Append("    type filter hook input priority filter; policy accept;\n");
        rules.Append(CultureInfo.InvariantCulture, $"    iifname \"{iface}\" ct state established,related accept\n");
        rules.Append(CultureInfo.InvariantCulture, $"    iifname \"{iface}\" icmpv6 type {KeepIcmpV6} accept\n");
        rules.Append(CultureInfo.InvariantCulture, $"    iifname \"{iface}\" drop\n");
        rules.Append("  }\n");
        rules.Append("}\n");

        var path = Path.Combine(AgentPaths.Root, "inbound.nft");
        try
        {
            await File.WriteAllTextAsync(path, rules.ToString(), ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            log.Warn("tunnel", $"the rules holding the tunnel off this machine could not be written: {ex.Message}");
            return false;
        }

        await RemoveAsync(ct).ConfigureAwait(false);
        var applied = await Shell.RunAsync("nft", ct, "-f", path).ConfigureAwait(false);
        if (applied.ExitCode != 0)
        {
            log.Warn("tunnel", $"nothing holds the tunnel off this machine: {applied.Output}");
            return false;
        }

        log.Info("tunnel", $"nothing inside the tunnel can open a connection to this machine over {iface}");
        return true;
    }

    /// <summary>
    /// Drops the table.
    /// </summary>
    public static async Task RemoveAsync(CancellationToken ct)
    {
        await Shell.RunAsync("nft", ct, "delete", "table", "inet", Table).ConfigureAwait(false);
    }
}
