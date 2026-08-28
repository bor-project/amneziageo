using System.Globalization;
using AmneziaGeo.Ipc;
using AmneziaGeo.Ipc.Fleet;

namespace AmneziaGeo.Cli.Fleet;

/// <summary>
/// The console of the mode: the set of tunnels the machine keeps up, and where one rule of a routing list
/// rides. A platform that has no mode never wires this in and prints the shared help, to the line.
/// </summary>
public static class FleetCommands
{
    /// <summary>
    /// Group the mode answers under.
    /// </summary>
    public const string Group = "fleet";

    /// <summary>
    /// Usage block a host folds into its own.
    /// </summary>
    public const string Usage = """
        several servers (experimental, Windows only)
          fleet status                        the set: roles, what stands and who carries the machine
          fleet up <config> [--takeover]      ask for one more server; the rest stand
          fleet down <config>                 take one server out of the set
          fleet primary <config>              name the server that carries what no rule sends elsewhere
          fleet role <config> <primary|reserve|neutral>
          fleet order <config> [<config>...]  the order the mode falls back through
          fleet target <id|name> <rule> [<rides>] [<fallback>]
                                              where one rule of a routing list rides: auto, best, direct, block
                                              or a server; naming neither end leaves the rule to the machine.
                                              A rule by name takes no server: only the tunnel holding this
                                              machine's lookups ever sees the name
          Turn the mode on with 'settings set multi-server on'. While it is on, 'status' prints the set as
          well, 'up <config>' joins the selected server to it and 'down <config>' takes one out.
        """;

    /// <summary>
    /// Whether the mode answers this command line instead of the shared console.
    /// </summary>
    public static bool Claims(StatusSnapshot snapshot, IReadOnlyList<string> args)
    {
        if (args.Count == 0)
        {
            return false;
        }

        if (args[0] == Group)
        {
            return true;
        }

        // While the set stands the status carries it, and one server is taken out by name. Everything else is
        // the shared command: connecting the selected server joins it to the set on its own.
        return snapshot.Fleet is not null && (args[0] == "status" || (args[0] == "down" && args.Count > 1));
    }

    /// <summary>
    /// Runs one command of the mode.
    /// </summary>
    public static async Task<int> RunAsync(IAgentLink agent, IReadOnlyList<string> args)
    {
        var snapshot = agent.Snapshot;
        if (args[0] != Group)
        {
            return args[0] == "status"
                ? Status(snapshot, shared: true)
                : await DownAsync(agent, [.. args.Skip(1)]).ConfigureAwait(false);
        }

        if (snapshot.Fleet is null)
        {
            return Off(snapshot);
        }

        if (args.Count < 2)
        {
            return Reply.Usage("usage: amneziageo fleet <status|up|down|primary|role|order|target>");
        }

        var rest = (IReadOnlyList<string>)[.. args.Skip(2)];
        return args[1] switch
        {
            "status" => Status(snapshot, shared: false),
            "up" => await UpAsync(agent, rest).ConfigureAwait(false),
            "down" => await DownAsync(agent, rest).ConfigureAwait(false),
            "primary" => await PrimaryAsync(agent, rest).ConfigureAwait(false),
            "role" => await RoleAsync(agent, rest).ConfigureAwait(false),
            "order" => await OrderAsync(agent, rest).ConfigureAwait(false),
            "target" => await TargetAsync(agent, rest).ConfigureAwait(false),
            _ => Reply.Usage($"unknown fleet command '{args[1]}'"),
        };
    }

    // The set rides the snapshot only while the mode is on and the tunnels of the machine are this user's.
    private static int Off(StatusSnapshot snapshot)
    {
        Output.Error(snapshot.MultiServer
            ? "the tunnels of this machine belong to another user"
            : "several servers is off; turn it on with 'amneziageo settings set multi-server on'");
        return Exit.Failed;
    }

    // Prints the set: what stands, who carries the machine and every rule addressed away from it.
    private static int Status(StatusSnapshot snapshot, bool shared)
    {
        if (snapshot.Fleet is not { } fleet)
        {
            return Off(snapshot);
        }

        if (Output.Json)
        {
            // The set rides the snapshot, so the shared status prints it already.
            if (shared)
            {
                StatusCommands.Print(snapshot);
            }
            else
            {
                Output.AsJson(fleet);
            }

            return Exit.Ok;
        }

        if (shared)
        {
            StatusCommands.Print(snapshot);
            Output.Line();
        }

        var balance = fleet.Balance ?? BalancePolicy.Default;
        Output.Pairs([
            ("primary", Named(fleet.Primary)),
            ("carrier", Named(fleet.Carrier)),
            ("balancer", $"every {balance.IntervalSeconds}s, {balance.Strikes} silent look(s), takes over under {balance.MarginPercent}%"),
        ]);
        Output.Line();
        Output.Table(
            ["SERVER", "ROLE", "ASKED", "DEFAULT", "RESOLVER"],
            [.. fleet.Servers.Select(server => (IReadOnlyList<string>)
            [
                server.Name,
                server.Role,
                Yes(server.Wanted),
                Yes(server.CarriesDefault),
                Yes(server.HoldsResolver),
            ])],
            "no servers yet");

        var targets = fleet.Targets ?? FleetTargets.Unaddressed;
        if (targets.Count == 0)
        {
            return Exit.Ok;
        }

        Output.Line();
        Output.Table(["LIST", "RULE", "RIDES", "FALLBACK"], [.. Addressed(snapshot, targets)]);
        return Exit.Ok;
    }

    // Asks for one more server; the rest stand. The header goes on looking at whatever it looked at before.
    private static async Task<int> UpAsync(IAgentLink agent, IReadOnlyList<string> args)
    {
        var flags = Flags.Parse(args, "takeover");
        if (!flags.Allowed("takeover"))
        {
            return Reply.Usage(flags.Error!);
        }

        if (flags.Positional.Count != 1)
        {
            return Reply.Usage("usage: amneziageo fleet up <config> [--takeover]");
        }

        var name = flags.Positional[0];
        var ack = flags.Has("takeover")
            ? await agent.SendAsync(FleetOps.Connect, name, "takeover").ConfigureAwait(false)
            : await agent.SendAsync(FleetOps.Connect, name).ConfigureAwait(false);
        return Reply.Report(ack, $"asked for {name}");
    }

    private static async Task<int> DownAsync(IAgentLink agent, IReadOnlyList<string> args)
    {
        if (args.Count != 1)
        {
            return Reply.Usage("usage: amneziageo fleet down <config>");
        }

        return Reply.Report(await agent.SendAsync(FleetOps.Disconnect, args[0]).ConfigureAwait(false), $"dropped {args[0]}");
    }

    private static async Task<int> PrimaryAsync(IAgentLink agent, IReadOnlyList<string> args)
    {
        if (args.Count != 1)
        {
            return Reply.Usage("usage: amneziageo fleet primary <config>");
        }

        return Reply.Report(await agent.SendAsync(FleetOps.SetPrimary, args[0]).ConfigureAwait(false), $"{args[0]} carries the machine");
    }

    private static async Task<int> RoleAsync(IAgentLink agent, IReadOnlyList<string> args)
    {
        if (args.Count != 2 || !TunnelRoles.IsKnown(args[1]))
        {
            return Reply.Usage($"usage: amneziageo fleet role <config> <{TunnelRoles.Primary}|{TunnelRoles.Reserve}|{TunnelRoles.Neutral}>");
        }

        var role = TunnelRoles.Of(args[1]);
        return Reply.Report(await agent.SendAsync(FleetOps.SetRole, args[0], role).ConfigureAwait(false), $"{args[0]}: {role}");
    }

    private static async Task<int> OrderAsync(IAgentLink agent, IReadOnlyList<string> args)
    {
        if (args.Count == 0)
        {
            return Reply.Usage("usage: amneziageo fleet order <config> [<config>...]");
        }

        return Reply.Report(await agent.SendAsync(FleetOps.Reorder, [.. args]).ConfigureAwait(false), "order saved");
    }

    // Says where one rule of a routing list rides. A rule left to the machine at both ends is not addressed at
    // all, so naming neither end takes the address back off it.
    private static async Task<int> TargetAsync(IAgentLink agent, IReadOnlyList<string> args)
    {
        if (args.Count is < 2 or > 4 || RoutingCommands.Resolve(agent, args[0]) is not { } list)
        {
            return Reply.Usage("usage: amneziageo fleet target <id|name> <rule> [auto|best|direct|block|<config>] [auto|best|direct|block|<config>]");
        }

        var id = list.Id.ToString(CultureInfo.InvariantCulture);
        var rules = await agent.SendAsync(IpcContract.OpGetRoutingList, id).ConfigureAwait(false);
        if (!rules.Ok)
        {
            return Reply.Report(rules);
        }

        var token = Token(args[1]);
        var rule = rules.Message
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault(entry => string.Equals(Token(entry), token, StringComparison.OrdinalIgnoreCase));
        if (rule is null)
        {
            return Reply.Usage($"'{token}' is not a rule of {list.Name}");
        }

        if (Role(rule) != "proxy")
        {
            return Reply.Usage($"'{token}' is {Role(rule)} in {list.Name}: every tunnel reads it alike, so it rides none of them");
        }

        var route = new RuleRoute(
            RuleTarget.Parse(args.Count > 2 ? args[2] : string.Empty),
            RuleTarget.Parse(args.Count > 3 ? args[3] : string.Empty));
        var ack = await agent.SendAsync(FleetOps.SetTarget, id, Token(rule), route.Target.Format(), route.Fallback.Format()).ConfigureAwait(false);
        return Reply.Report(ack, route.IsDefault ? $"{token} is left to the machine again" : $"{token} rides {route.Format()}");
    }

    private static IEnumerable<IReadOnlyList<string>> Addressed(StatusSnapshot snapshot, IReadOnlyDictionary<string, string> targets)
    {
        foreach (var pair in targets.OrderBy(target => target.Key, StringComparer.Ordinal))
        {
            if (!FleetTargets.TrySplit(pair.Key, out var listId, out var token))
            {
                continue;
            }

            var route = RuleRoute.Parse(pair.Value);
            yield return [ListName(snapshot, listId), token, route.Target.Format(), route.Fallback.Format()];
        }
    }

    private static string ListName(StatusSnapshot snapshot, long id)
    {
        var list = snapshot.RoutingLists?.FirstOrDefault(entry => entry.Id == id);
        return list?.Name ?? id.ToString(CultureInfo.InvariantCulture);
    }

    private static string Role(string rule)
    {
        var separator = rule.IndexOf('|');
        return separator > 0 ? rule[..separator].Trim().ToLowerInvariant() : "proxy";
    }

    private static string Token(string rule)
    {
        var separator = rule.IndexOf('|');
        return separator > 0 ? rule[(separator + 1)..].Trim() : rule.Trim();
    }

    private static string Named(string name) => name.Length > 0 ? name : "-";

    private static string Yes(bool value) => value ? "yes" : "-";
}
