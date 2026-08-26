using System.Globalization;
using System.Text.Json;
using AmneziaGeo.Decl;
using AmneziaGeo.Ipc;

namespace AmneziaGeo.Cli;

/// <summary>
/// Shared routing lists: their rules and their traffic settings.
/// </summary>
internal static class RoutingCommands
{
    private const string _ruleUsage = "usage: amneziageo routing rule server <id|name> <rule> <auto|best|config>\n"
        + "       amneziageo routing rule fallback <id|name> <rule> <auto|best|config|direct|block>";

    /// <summary>
    /// Runs one routing command.
    /// </summary>
    public static async Task<int> RunAsync(IAgentLink agent, IReadOnlyList<string> args)
    {
        if (args.Count == 0)
        {
            return Reply.Usage("usage: amneziageo routing <list|use|show|create|set|add|rule|delete-rule|remove|order|settings|configure>");
        }

        var rest = (IReadOnlyList<string>)[.. args.Skip(1)];
        return args[0] switch
        {
            "list" => List(agent),
            "use" => await UseAsync(agent, rest).ConfigureAwait(false),
            "show" => await ShowAsync(agent, rest).ConfigureAwait(false),
            "create" => await CreateAsync(agent, rest).ConfigureAwait(false),
            "set" => await SetAsync(agent, rest).ConfigureAwait(false),
            "add" => await AmendAsync(agent, rest, add: true).ConfigureAwait(false),
            "rule" => await RuleAsync(agent, rest).ConfigureAwait(false),
            "delete-rule" => await AmendAsync(agent, rest, add: false).ConfigureAwait(false),
            "remove" => await RemoveAsync(agent, rest).ConfigureAwait(false),
            "order" => rest.Count > 0
                ? Reply.Report(await agent.SendAsync(IpcContract.OpReorderRoutingLists, [.. rest]).ConfigureAwait(false), "order saved")
                : Reply.Usage("usage: amneziageo routing order <name> [<name>...]"),
            "settings" => await SettingsAsync(agent, rest).ConfigureAwait(false),
            "configure" => await ConfigureAsync(agent, rest).ConfigureAwait(false),
            _ => Reply.Usage($"unknown routing command '{args[0]}'"),
        };
    }

    // Picks the routing list a config uses, or the default one for those without a binding; "none" routes
    // everything through the tunnel, "default" puts a config back on the default list.
    private static async Task<int> UseAsync(IAgentLink agent, IReadOnlyList<string> args)
    {
        if (args.Count is < 1 or > 2)
        {
            return Reply.Usage("usage: amneziageo routing use <id|name|none|default> [<config>]");
        }

        var listId = "none";
        if (args[0] == "default")
        {
            if (args.Count != 2)
            {
                return Reply.Usage("usage: amneziageo routing use default <config>");
            }

            listId = "default";
        }
        else if (args[0] != "none" && args[0] != "0")
        {
            if (Resolve(agent, args[0]) is not { } list)
            {
                return Reply.Usage($"routing list '{args[0]}' not found");
            }

            listId = list.Id.ToString(CultureInfo.InvariantCulture);
        }

        var payload = args.Count == 2 ? new[] { listId, args[1] } : new[] { listId };
        return Reply.Report(await agent.SendAsync(IpcContract.OpAssignRouting, payload).ConfigureAwait(false));
    }

    /// <summary>
    /// Finds a routing list by id or by name.
    /// </summary>
    public static RoutingListEntry? Resolve(IAgentLink agent, string key)
    {
        var lists = agent.Snapshot.RoutingLists ?? [];
        if (long.TryParse(key, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id))
        {
            var byId = lists.FirstOrDefault(list => list.Id == id);
            if (byId is not null)
            {
                return byId;
            }
        }

        return lists.FirstOrDefault(list => string.Equals(list.Name, key, StringComparison.OrdinalIgnoreCase));
    }

    private static int List(IAgentLink agent)
    {
        var lists = agent.Snapshot.RoutingLists ?? [];
        if (Output.Json)
        {
            Output.AsJson(lists);
            return Exit.Ok;
        }

        var rows = lists
            .Select(list => (IReadOnlyList<string>)
            [
                list.Id.ToString(CultureInfo.InvariantCulture),
                list.Name,
                list.RuleCount.ToString(CultureInfo.InvariantCulture),
                list.RouteCount.ToString(CultureInfo.InvariantCulture),
                list.DomainCount.ToString(CultureInfo.InvariantCulture),
            ])
            .ToList();

        Output.Table(["ID", "NAME", "RULES", "ROUTES", "DOMAINS"], rows, "no routing lists yet");
        return Exit.Ok;
    }

    private static async Task<int> ShowAsync(IAgentLink agent, IReadOnlyList<string> args)
    {
        if (args.Count != 1 || Resolve(agent, args[0]) is not { } list)
        {
            return Reply.Usage("usage: amneziageo routing show <id|name>");
        }

        var ack = await RulesAsync(agent, list.Id).ConfigureAwait(false);
        if (!ack.Ok)
        {
            return Reply.Report(ack);
        }

        var rules = Split(ack.Message);
        var rows = rules.Select(Read).ToList();

        // The target columns show up where a rule has somewhere else to ride, and wherever one already names a
        // server: a field set before the switch went off stays in sight.
        var targets = agent.Snapshot.MultiServer
            || rows.Any(row => row.ServerMode != RuleTargetMode.Auto || row.FallbackMode != RuleTargetMode.Auto);

        if (Output.Json)
        {
            Output.AsJson(new
            {
                id = list.Id,
                name = list.Name,
                rules = rows.Select(row => new
                {
                    rule = row.Rule,
                    role = row.Role,
                    server = RuleFields.Word(row.ServerMode, row.Server),
                    fallback = RuleFields.Word(row.FallbackMode, row.Fallback),
                }),
            });
            return Exit.Ok;
        }

        Output.Info($"#{list.Id.ToString(CultureInfo.InvariantCulture)} {list.Name}");
        if (!targets)
        {
            foreach (var rule in rules)
            {
                Output.Line(rule);
            }

            return Exit.Ok;
        }

        Output.Table(
            ["RULE", "ROLE", "SERVER", "FALLBACK"],
            rows.Select(row => (IReadOnlyList<string>)
            [
                row.Rule,
                row.Role,
                Column(row, row.ServerMode, row.Server),
                Column(row, row.FallbackMode, row.Fallback),
            ]).ToList(),
            "no rules yet");
        return Exit.Ok;
    }

    private static async Task<int> CreateAsync(IAgentLink agent, IReadOnlyList<string> args)
    {
        if (args.Count < 1)
        {
            return Reply.Usage("usage: amneziageo routing create <name> [rule...]");
        }

        var rules = args.Skip(1).ToArray();
        if (Rules.FirstInvalid(rules) is { } invalid)
        {
            return Reply.Usage(Invalid(invalid));
        }

        var ack = await agent.SendAsync(IpcContract.OpSaveRoutingList, ["0", args[0], .. rules]).ConfigureAwait(false);
        return ack.Ok ? Created(ack.Message, args[0]) : Reply.Report(ack);
    }

    private static async Task<int> SetAsync(IAgentLink agent, IReadOnlyList<string> args)
    {
        if (args.Count < 1 || Resolve(agent, args[0]) is not { } list)
        {
            return Reply.Usage("usage: amneziageo routing set <id|name> [rule...]");
        }

        var rules = args.Skip(1).ToArray();
        if (Rules.FirstInvalid(rules) is { } invalid)
        {
            return Reply.Usage(Invalid(invalid));
        }

        var ack = await agent.SendAsync(IpcContract.OpSaveRoutingList, [list.Id.ToString(CultureInfo.InvariantCulture), list.Name, .. rules]).ConfigureAwait(false);
        return ack.Ok ? Reply.Report(ack with { Message = $"{list.Name}: {rules.Length.ToString(CultureInfo.InvariantCulture)} rules" }) : Reply.Report(ack);
    }

    private static async Task<int> AmendAsync(IAgentLink agent, IReadOnlyList<string> args, bool add)
    {
        var verb = add ? "add" : "delete-rule";
        if (args.Count < 2 || Resolve(agent, args[0]) is not { } list)
        {
            return Reply.Usage($"usage: amneziageo routing {verb} <id|name> <rule...>");
        }

        var changes = args.Skip(1).ToArray();
        if (Rules.FirstInvalid(changes) is { } invalid)
        {
            return Reply.Usage(Invalid(invalid));
        }

        var current = await RulesAsync(agent, list.Id).ConfigureAwait(false);
        if (!current.Ok)
        {
            return Reply.Report(current);
        }

        var rules = Split(current.Message).ToList();
        foreach (var change in changes)
        {
            if (add)
            {
                if (!rules.Any(rule => Same(rule, change)))
                {
                    rules.Add(change);
                }
            }
            else
            {
                rules.RemoveAll(rule => Same(rule, change));
            }
        }

        var ack = await agent.SendAsync(IpcContract.OpSaveRoutingList, [list.Id.ToString(CultureInfo.InvariantCulture), list.Name, .. rules]).ConfigureAwait(false);
        return ack.Ok ? Reply.Report(ack with { Message = $"{list.Name}: {rules.Count.ToString(CultureInfo.InvariantCulture)} rules" }) : Reply.Report(ack);
    }

    // Points one field of one rule: the server it rides, or where it goes while that server is down.
    private static async Task<int> RuleAsync(IAgentLink agent, IReadOnlyList<string> args)
    {
        if (args.Count == 0 || (args[0] != "server" && args[0] != "fallback"))
        {
            return Reply.Usage(_ruleUsage);
        }

        var fallback = args[0] == "fallback";
        if (args.Count != 4 || Resolve(agent, args[1]) is not { } list)
        {
            return Reply.Usage(_ruleUsage);
        }

        // A field nothing reads while one server carries everything.
        if (!agent.Snapshot.MultiServer)
        {
            Output.Error("this agent runs one server at a time, so a rule has nowhere else to ride");
            return Exit.Unsupported;
        }

        if (Target(agent, args[3], fallback) is not { } target)
        {
            return Reply.Usage(fallback
                ? $"'{args[3]}' is not a fallback; expected auto, best, direct, block or a configuration"
                : $"'{args[3]}' is not a server; expected auto, best or a configuration");
        }

        var current = await RulesAsync(agent, list.Id).ConfigureAwait(false);
        if (!current.Ok)
        {
            return Reply.Report(current);
        }

        var rules = Split(current.Message).ToList();
        var index = rules.FindIndex(rule => Same(rule, args[2]));
        if (index < 0)
        {
            return Reply.Usage($"'{args[2]}' is not a rule of {list.Name}");
        }

        var row = Read(rules[index]);
        if (row.Role != "proxy")
        {
            return Reply.Usage($"'{row.Rule}' is a {row.Role} rule, and only a proxied rule rides a server");
        }

        // A name is matched by the machine's one resolver, so it takes the default server until that changes.
        if (row.Rule.StartsWith("domain:", StringComparison.OrdinalIgnoreCase)
            || row.Rule.StartsWith("geosite:", StringComparison.OrdinalIgnoreCase))
        {
            return Reply.Usage($"'{row.Rule}' is matched by name, and names take the default server for now");
        }

        var tail = fallback
            ? RuleFields.Tail(row.ServerMode, row.Server, target.Mode, target.Name)
            : RuleFields.Tail(target.Mode, target.Name, row.FallbackMode, row.Fallback);
        rules[index] = $"{row.Role}|{row.Rule}{tail}";

        var ack = await agent.SendAsync(IpcContract.OpSaveRoutingList, [list.Id.ToString(CultureInfo.InvariantCulture), list.Name, .. rules]).ConfigureAwait(false);
        return ack.Ok
            ? Reply.Report(ack with { Message = $"{row.Rule}: {args[0]} = {RuleFields.Word(target.Mode, target.Name)}" })
            : Reply.Report(ack);
    }

    // Reads the target word, refusing what the field cannot hold and pinning a configuration to its stored spelling.
    private static (RuleTargetMode Mode, string Name)? Target(IAgentLink agent, string word, bool fallback)
    {
        var (mode, name) = RuleFields.Parse(word.Trim());
        if (!fallback && mode is RuleTargetMode.Direct or RuleTargetMode.Block)
        {
            return null;
        }

        if (mode != RuleTargetMode.Server)
        {
            return (mode, name);
        }

        var config = agent.Snapshot.Configs.FirstOrDefault(entry => string.Equals(entry.Name, name, StringComparison.OrdinalIgnoreCase));
        return config is null ? null : (mode, config.Name);
    }

    private static async Task<int> RemoveAsync(IAgentLink agent, IReadOnlyList<string> args)
    {
        if (args.Count != 1 || Resolve(agent, args[0]) is not { } list)
        {
            return Reply.Usage("usage: amneziageo routing remove <id|name>");
        }

        if (agent.Snapshot.SelectedRoutingList == list.Id
            || agent.Snapshot.Configs.Any(config => config.RoutingListId == list.Id))
        {
            return Reply.Usage($"'{list.Name}' is in use; pick another with 'routing use' first");
        }

        return Reply.Report(await agent.SendAsync(IpcContract.OpRemoveRoutingList, list.Id.ToString(CultureInfo.InvariantCulture)).ConfigureAwait(false), $"removed {list.Name}");
    }

    private static async Task<int> SettingsAsync(IAgentLink agent, IReadOnlyList<string> args)
    {
        if (args.Count != 1 || Resolve(agent, args[0]) is not { } list)
        {
            return Reply.Usage("usage: amneziageo routing settings <id|name>");
        }

        var ack = await agent.SendAsync(IpcContract.OpGetRoutingSettings, list.Id.ToString(CultureInfo.InvariantCulture)).ConfigureAwait(false);
        if (!ack.Ok)
        {
            return Reply.Report(ack);
        }

        if (Output.Json)
        {
            Output.Line(ack.Message);
            return Exit.Ok;
        }

        var settings = Parse(ack.Message);
        Output.Pairs(
        [
            ("list", $"#{list.Id.ToString(CultureInfo.InvariantCulture)} {list.Name}"),
            ("mode", settings.Mode),
            ("global proxy", settings.UseGlobalProxy ? "on" : "off"),
            ("all UDP", settings.AllUdp ? "on" : "off"),
            ("exclusions", settings.Exclusions.Length > 0 ? settings.Exclusions.ReplaceLineEndings(", ") : "-"),
        ]);
        return Exit.Ok;
    }

    private static async Task<int> ConfigureAsync(IAgentLink agent, IReadOnlyList<string> args)
    {
        var flags = Flags.Parse(args);
        if (!flags.Allowed("exclusions", "exclusions-file", "all-udp", "global-proxy"))
        {
            return Reply.Usage(flags.Error!);
        }

        if (flags.Positional.Count != 1 || Resolve(agent, flags.Positional[0]) is not { } list)
        {
            return Reply.Usage("usage: amneziageo routing configure <id|name> [--exclusions <a,b>] [--exclusions-file <p>] [--all-udp on|off] [--global-proxy on|off]");
        }

        var id = list.Id.ToString(CultureInfo.InvariantCulture);
        var stored = await agent.SendAsync(IpcContract.OpGetRoutingSettings, id).ConfigureAwait(false);
        if (!stored.Ok)
        {
            return Reply.Report(stored);
        }

        // Read-modify-write: the agent drops the whole row when every field lands on its default.
        var settings = Parse(stored.Message);
        var exclusions = settings.Exclusions;
        if (flags.Value("exclusions-file") is { Length: > 0 } path)
        {
            if (!File.Exists(path))
            {
                return Reply.Usage($"{path} does not exist");
            }

            exclusions = File.ReadAllText(path);
        }
        else if (flags.Value("exclusions") is { } literal)
        {
            exclusions = literal;
        }

        var allUdp = settings.AllUdp;
        if (flags.Value("all-udp") is { } udpToken && !Toggle.TryParse(udpToken, out allUdp))
        {
            return Reply.Usage("--all-udp takes on or off");
        }

        var globalProxy = settings.UseGlobalProxy;
        if (flags.Value("global-proxy") is { } proxyToken && !Toggle.TryParse(proxyToken, out globalProxy))
        {
            return Reply.Usage("--global-proxy takes on or off");
        }

        return Reply.Report(await agent.SendAsync(
            IpcContract.OpSetRoutingSettings,
            id,
            exclusions,
            Toggle.Text(allUdp),
            globalProxy ? "full" : "split",
            Toggle.Text(globalProxy)).ConfigureAwait(false));
    }

    private static Task<IpcAck> RulesAsync(IAgentLink agent, long id) =>
        agent.SendAsync(IpcContract.OpGetRoutingList, id.ToString(CultureInfo.InvariantCulture));

    private static string[] Split(string payload) =>
        payload.Length == 0 ? [] : payload.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    // Rules compare on the token alone, so "geosite:x" matches "proxy|geosite:x|server=de".
    private static bool Same(string left, string right) =>
        string.Equals(Rules.Plain(left), Rules.Plain(right), StringComparison.OrdinalIgnoreCase);

    // Reads a stored rule into its parts.
    private static RuleRow Read(string rule)
    {
        var fields = RuleFields.Split(Rules.Bare(rule));
        return new RuleRow(fields.Token.Trim(), Rules.Role(rule), fields.ServerMode, fields.Server, fields.FallbackMode, fields.Fallback);
    }

    // A bucket that reaches no server has no field to show.
    private static string Column(RuleRow row, RuleTargetMode mode, string name) =>
        row.Role == "proxy" ? RuleFields.Word(mode, name) : "-";

    private static int Created(string message, string name)
    {
        Output.Info($"created {name} (#{message})");
        return Exit.Ok;
    }

    private static string Invalid(string rule) =>
        $"'{rule}' is not a rule; expected [proxy|direct|block]|<geosite:x|geoip:x|domain:x|cidr:x|app:x>";

    private static RoutingSettingsPayload Parse(string json)
    {
        var parsed = JsonSerializer.Deserialize<RoutingSettingsPayload>(json, IpcJson.Options);
        return parsed ?? new RoutingSettingsPayload(string.Empty, false, "split", false);
    }

    /// <summary>
    /// Traffic settings of a routing list, as the agent reports them.
    /// </summary>
    private sealed record RoutingSettingsPayload(string Exclusions, bool AllUdp, string Mode, bool UseGlobalProxy);

    /// <summary>
    /// One stored rule read into its parts.
    /// </summary>
    private sealed record RuleRow(
        string Rule,
        string Role,
        RuleTargetMode ServerMode,
        string Server,
        RuleTargetMode FallbackMode,
        string Fallback);
}
