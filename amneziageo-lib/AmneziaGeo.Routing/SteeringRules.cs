using System.Text;

namespace AmneziaGeo.Routing;

/// <summary>
/// Writes the netfilter tables a Linux tunnel steers and refuses traffic by: the mark that selects the routing
/// table of the tunnel, and the ranges a list blocks; and the routes its direct ranges leave a full tunnel by. Reads
/// the way the kernel leads an address by as well.
/// </summary>
public static class SteeringRules
{
    /// <summary>
    /// The mark that selects the routing table of the tunnel.
    /// </summary>
    public const string Mark = "0x51820";

    /// <summary>
    /// The table the mark is set in.
    /// </summary>
    public const string Table = "amneziageo";

    /// <summary>
    /// The table the blocked ranges are refused in.
    /// </summary>
    public const string BlockTable = "amneziageo-block";

    /// <summary>
    /// The set of the ranges a list sends through the tunnel.
    /// </summary>
    public const string Proxied = "proxied";

    /// <summary>
    /// The set of the addresses a name keeps off the tunnel.
    /// </summary>
    public const string Spared = "spared";

    /// <summary>
    /// The set of the ranges a list keeps off the tunnel.
    /// </summary>
    public const string Direct = "direct";

    /// <summary>
    /// The routing table of the routes of the machine.
    /// </summary>
    public const string MainTable = "main";

    private const string Blocked = "blocked";

    /// <summary>
    /// The ranges a list sends through the tunnel by address: its tunnel ranges without the direct and the blocked
    /// ones.
    /// </summary>
    public static IReadOnlyList<string> Carried(IReadOnlyList<string> proxy, IReadOnlyList<string> direct, IReadOnlyList<string> block) =>
        SystemRoutes.Without(proxy, [.. direct, .. block]);

    /// <summary>
    /// The ranges a list keeps off the tunnel by address: its direct ranges without the blocked ones and without
    /// the addresses the connection itself stands on.
    /// </summary>
    public static IReadOnlyList<string> Bypassed(IReadOnlyList<string> direct, IReadOnlyList<string> block, IReadOnlyList<string> kept) =>
        SystemRoutes.Without(direct, [.. block, .. kept]);

    /// <summary>
    /// The ranges a list refuses, without the addresses the connection itself stands on.
    /// </summary>
    public static IReadOnlyList<string> Refused(IReadOnlyList<string> block, IReadOnlyList<string> kept) =>
        SystemRoutes.Without(block, kept);

    /// <summary>
    /// The table that marks what the tunnel carries past the routes of the machine and rewrites its source: the
    /// processes of a cgroup, what the machine opens toward the ranges of a list, every datagram. Null ranges leave
    /// the set of them out; the spared addresses stay off the mark of the ranges; the direct ranges, where given,
    /// take no mark at all, and neither do the spared addresses then.
    /// </summary>
    public static string Marks(string iface, string? cgroup, IReadOnlyList<string>? ranges, bool allUdp, string? endpoint, IReadOnlyList<string>? spared = null, IReadOnlyList<string>? direct = null)
    {
        var text = new StringBuilder();
        text.Append("table inet ").Append(Table).Append(" {\n");
        if (ranges is not null)
        {
            Set(text, Proxied, true, ranges);
        }

        if (ranges is not null || direct is not null)
        {
            Set(text, Spared, false, spared ?? []);
        }

        if (direct is not null)
        {
            Set(text, Direct, true, direct);
        }

        text.Append("  chain apps {\n");
        text.Append("    type route hook output priority mangle; policy accept;\n");
        if (direct is not null)
        {
            text.Append("    ip daddr @").Append(Direct).Append(" return\n");
            text.Append("    ip daddr @").Append(Spared).Append(" return\n");
        }

        if (cgroup is { Length: > 0 })
        {
            text.Append("    socket cgroupv2 level 1 \"").Append(cgroup).Append("\" meta mark set ").Append(Mark).Append('\n');
        }

        if (ranges is not null || allUdp)
        {
            text.Append("    oifname \"lo\" return\n");
            if (endpoint is { Length: > 0 })
            {
                text.Append(endpoint.Contains(':', StringComparison.Ordinal) ? "    ip6 daddr " : "    ip daddr ");
                text.Append(endpoint).Append(" return\n");
            }
        }

        if (ranges is not null)
        {
            text.Append("    ct direction original ip daddr @").Append(Proxied).Append(" ip daddr != @").Append(Spared);
            text.Append(" meta mark set ").Append(Mark).Append('\n');
        }

        if (allUdp)
        {
            text.Append("    oifname \"").Append(iface).Append("\" return\n");
            text.Append("    ip daddr 255.255.255.255 return\n");
            text.Append("    ip daddr 224.0.0.0/4 return\n");
            text.Append("    ip6 daddr ff00::/8 return\n");
            text.Append("    meta l4proto udp meta mark set ").Append(Mark).Append('\n');
        }

        text.Append("  }\n");
        text.Append("  chain source {\n");
        text.Append("    type nat hook postrouting priority srcnat; policy accept;\n");
        text.Append("    meta mark ").Append(Mark).Append(" oifname \"").Append(iface).Append("\" masquerade\n");
        text.Append("  }\n");
        text.Append("}\n");

        return text.ToString();
    }

    /// <summary>
    /// The lines that replace the ranges the marks go by and spare more addresses in the same go; a null set is
    /// one the table does not stand.
    /// </summary>
    public static string Reload(IReadOnlyList<string>? ranges, IReadOnlyList<string> spared, IReadOnlyList<string>? direct = null)
    {
        var text = new StringBuilder();
        if (ranges is not null)
        {
            text.Append("flush set inet ").Append(Table).Append(' ').Append(Proxied).Append('\n');
            Add(text, Proxied, ranges);
            Add(text, Spared, spared);
        }

        if (direct is not null)
        {
            text.Append("flush set inet ").Append(Table).Append(' ').Append(Direct).Append('\n');
            Add(text, Direct, direct);
        }

        return text.ToString();
    }

    /// <summary>
    /// The lines iproute2 lays the ranges past a tunnel that carries everything by, through the hop the machine
    /// reaches the internet over.
    /// </summary>
    public static string Bypasses(IReadOnlyList<string> ranges, string gateway, string device, string table)
    {
        var text = new StringBuilder();
        foreach (var range in ranges)
        {
            text.Append("route replace ").Append(range).Append(" via ").Append(gateway);
            text.Append(" dev ").Append(device).Append(" table ").Append(table).Append('\n');
        }

        return text.ToString();
    }

    /// <summary>
    /// The lines iproute2 takes the ranges out of the table by.
    /// </summary>
    public static string Withdrawals(IReadOnlyList<string> ranges, string table)
    {
        var text = new StringBuilder();
        foreach (var range in ranges)
        {
            text.Append("route del ").Append(range).Append(" table ").Append(table).Append('\n');
        }

        return text.ToString();
    }

    /// <summary>
    /// The routing table that leads an address anywhere but into the tunnel, read from what the kernel answers a
    /// lookup of its route with; null when the tunnel takes the address or the answer names no interface.
    /// </summary>
    public static string? Leading(string answer, string tunnel)
    {
        var words = answer.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var device = After(words, "dev");
        if (device is null || device == tunnel)
        {
            return null;
        }

        return After(words, "table") ?? MainTable;
    }

    /// <summary>
    /// The table that refuses what leaves the machine for the ranges, in place of the one that stood.
    /// </summary>
    public static string Refusals(IReadOnlyList<string> ranges)
    {
        var text = new StringBuilder();
        text.Append("table inet ").Append(BlockTable).Append('\n');
        text.Append("delete table inet ").Append(BlockTable).Append('\n');
        text.Append("table inet ").Append(BlockTable).Append(" {\n");
        Set(text, Blocked, true, ranges);
        text.Append("  chain output {\n");
        text.Append("    type filter hook output priority filter; policy accept;\n");
        text.Append("    oifname \"lo\" accept\n");
        text.Append("    ip daddr @").Append(Blocked).Append(" reject with icmpx type admin-prohibited\n");
        text.Append("  }\n");
        text.Append("}\n");

        return text.ToString();
    }

    // The word that follows the keyword.
    private static string? After(string[] words, string keyword)
    {
        var index = Array.IndexOf(words, keyword);
        return index >= 0 && index + 1 < words.Length ? words[index + 1] : null;
    }

    private static void Add(StringBuilder text, string set, IReadOnlyList<string> elements)
    {
        if (elements.Count > 0)
        {
            text.Append("add element inet ").Append(Table).Append(' ').Append(set);
            text.Append(" { ").Append(string.Join(", ", elements)).Append(" }\n");
        }
    }

    private static void Set(StringBuilder text, string name, bool ranges, IReadOnlyList<string> elements)
    {
        text.Append("  set ").Append(name).Append(" {\n");
        text.Append("    type ipv4_addr\n");
        if (ranges)
        {
            text.Append("    flags interval\n");
        }

        if (elements.Count > 0)
        {
            text.Append("    elements = { ").Append(string.Join(", ", elements)).Append(" }\n");
        }

        text.Append("  }\n");
    }
}
