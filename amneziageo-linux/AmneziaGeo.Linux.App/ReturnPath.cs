using System.Globalization;
using System.Text;

namespace AmneziaGeo.Linux.App;

/// <summary>
/// Sends the answers to connections that came in beside the tunnel back out through the gateway they came from.
/// Own nftables table and routing table; the rules of other software are never touched.
/// </summary>
internal static class ReturnPath
{
    private const string Table = "amneziageo-return";

    // The connection mark, the routing table it selects and the priorities of the two routing rules.
    private const int Mark = 16711;
    private const int RouteTable = 16711;
    private const int LocalRulePriority = 16710;
    private const int GatewayRulePriority = 16711;

    /// <summary>
    /// Marks the connections opened towards this machine beside the tunnel and routes their answers through the
    /// gateway; returns whether it stands.
    /// </summary>
    public static async Task<bool> ApplyAsync(string iface, string gateway, string device, AgentLog log, CancellationToken ct)
    {
        await RemoveAsync(ct).ConfigureAwait(false);

        var mark = Mark.ToString(CultureInfo.InvariantCulture);
        var table = RouteTable.ToString(CultureInfo.InvariantCulture);
        string[][] steps =
        [
            ["route", "replace", "default", "via", gateway, "dev", device, "table", table],
            ["rule", "add", "fwmark", mark, "lookup", "main", "suppress_prefixlength", "1", "priority", LocalRulePriority.ToString(CultureInfo.InvariantCulture)],
            ["rule", "add", "fwmark", mark, "lookup", table, "priority", GatewayRulePriority.ToString(CultureInfo.InvariantCulture)],
        ];
        foreach (var step in steps)
        {
            var (exitCode, output) = await Shell.RunAsync("ip", ct, step).ConfigureAwait(false);
            if (exitCode != 0)
            {
                log.Warn("tunnel", $"answers to connections from outside the tunnel may leave through it: ip {string.Join(' ', step)} failed: {output}");
                await RemoveAsync(ct).ConfigureAwait(false);
                return false;
            }
        }

        var rules = new StringBuilder();
        rules.Append(CultureInfo.InvariantCulture, $"table inet {Table} {{\n");
        rules.Append("  chain prerouting {\n");
        rules.Append("    type filter hook prerouting priority mangle; policy accept;\n");
        rules.Append(CultureInfo.InvariantCulture, $"    iifname != {{ \"lo\", \"{iface}\" }} ct state new ct mark set {mark}\n");
        rules.Append("  }\n");
        rules.Append("  chain output {\n");
        rules.Append("    type route hook output priority mangle; policy accept;\n");
        rules.Append(CultureInfo.InvariantCulture, $"    ct mark {mark} meta mark set {mark}\n");
        rules.Append("  }\n");
        rules.Append("}\n");

        var path = Path.Combine(AgentPaths.Root, "return.nft");
        try
        {
            await File.WriteAllTextAsync(path, rules.ToString(), ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            log.Warn("tunnel", $"the rules answering connections from outside the tunnel could not be written: {ex.Message}");
            await RemoveAsync(ct).ConfigureAwait(false);
            return false;
        }

        var applied = await Shell.RunAsync("nft", ct, "-f", path).ConfigureAwait(false);
        if (applied.ExitCode != 0)
        {
            log.Warn("tunnel", $"answers to connections from outside the tunnel may leave through it: {applied.Output}");
            await RemoveAsync(ct).ConfigureAwait(false);
            return false;
        }

        log.Info("tunnel", $"connections that come in beside {iface} are answered through {gateway} on {device}");
        return true;
    }

    /// <summary>
    /// Drops the table, the routing rules and the routing table.
    /// </summary>
    public static async Task RemoveAsync(CancellationToken ct)
    {
        await Shell.RunAsync("nft", ct, "delete", "table", "inet", Table).ConfigureAwait(false);
        await DeleteRulesAsync(LocalRulePriority, ct).ConfigureAwait(false);
        await DeleteRulesAsync(GatewayRulePriority, ct).ConfigureAwait(false);
        await Shell.RunAsync("ip", ct, "route", "flush", "table", RouteTable.ToString(CultureInfo.InvariantCulture)).ConfigureAwait(false);
    }

    // Deletes every routing rule at the priority, the ones an earlier run left behind included.
    private static async Task DeleteRulesAsync(int priority, CancellationToken ct)
    {
        var deleted = true;
        while (deleted)
        {
            var (exitCode, _) = await Shell.RunAsync("ip", ct, "rule", "del", "priority", priority.ToString(CultureInfo.InvariantCulture)).ConfigureAwait(false);
            deleted = exitCode == 0;
        }
    }
}
