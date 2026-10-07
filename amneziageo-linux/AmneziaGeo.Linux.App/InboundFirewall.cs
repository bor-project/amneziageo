using System.Globalization;
using System.Net.Sockets;
using System.Text;
using AmneziaGeo.Ipc;

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
    /// machine opened itself keep answering, and the signal of the server to disconnect is let in.
    /// </summary>
    public static async Task<bool> ApplyAsync(string iface, SignalPlace? signal, AgentLog log, CancellationToken ct)
    {
        var path = Path.Combine(AgentPaths.Root, "inbound.nft");
        try
        {
            await File.WriteAllTextAsync(path, Rules(iface, signal), ct).ConfigureAwait(false);
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

        log.Info("tunnel", signal is null
            ? $"nothing inside the tunnel can open a connection to this machine over {iface}"
            : $"nothing inside the tunnel can open a connection to this machine over {iface} but its server, on port {signal.Port} alone");
        return true;
    }

    // The table: what the tunnel opens is dropped but the signal of the server to disconnect.
    internal static string Rules(string iface, SignalPlace? signal)
    {
        var rules = new StringBuilder();
        rules.Append(CultureInfo.InvariantCulture, $"table inet {Table} {{\n");
        rules.Append("  chain input {\n");
        rules.Append("    type filter hook input priority filter; policy accept;\n");
        rules.Append(CultureInfo.InvariantCulture, $"    iifname \"{iface}\" ct state established,related accept\n");
        rules.Append(CultureInfo.InvariantCulture, $"    iifname \"{iface}\" icmpv6 type {KeepIcmpV6} accept\n");
        foreach (var source in signal?.From ?? [])
        {
            var family = source.AddressFamily == AddressFamily.InterNetworkV6 ? "ip6" : "ip";
            rules.Append(CultureInfo.InvariantCulture, $"    iifname \"{iface}\" {family} saddr {source} tcp dport {signal!.Port} accept\n");
        }

        rules.Append(CultureInfo.InvariantCulture, $"    iifname \"{iface}\" drop\n");
        rules.Append("  }\n");
        rules.Append("}\n");

        return rules.ToString();
    }

    /// <summary>
    /// Drops the table.
    /// </summary>
    public static async Task RemoveAsync(CancellationToken ct)
    {
        await Shell.RunAsync("nft", ct, "delete", "table", "inet", Table).ConfigureAwait(false);
    }
}
