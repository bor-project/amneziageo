using System.Globalization;
using AmneziaGeo.Decl;
using AmneziaGeo.Ipc;

namespace AmneziaGeo.Linux.Cli;

/// <summary>
/// Configuration catalogue: import, edit, export and the per-config transport, DNS and geo settings.
/// </summary>
internal static class ConfigCommands
{
    /// <summary>
    /// Runs one config command.
    /// </summary>
    public static async Task<int> RunAsync(AgentClient agent, IReadOnlyList<string> args)
    {
        if (args.Count == 0)
        {
            return Reply.Usage("usage: amneziageo config <list|show|link|import|edit|rename|copy|remove|dns|exclusions|websocket|geo>");
        }

        var rest = (IReadOnlyList<string>)[.. args.Skip(1)];
        return args[0] switch
        {
            "list" => List(agent),
            "show" => await ShowAsync(agent, rest).ConfigureAwait(false),
            "link" => await LinkAsync(agent, rest).ConfigureAwait(false),
            "import" => await ImportAsync(agent, rest).ConfigureAwait(false),
            "edit" => await EditAsync(agent, rest).ConfigureAwait(false),
            "rename" => rest.Count == 2
                ? Reply.Report(await agent.SendAsync(IpcContract.OpRenameConfig, rest[0], rest[1]).ConfigureAwait(false), $"renamed to {rest[1]}")
                : Reply.Usage("usage: amneziageo config rename <name> <new-name>"),
            "copy" => rest.Count == 2
                ? Reply.Report(await agent.SendAsync(IpcContract.OpCopyConfig, rest[0], rest[1]).ConfigureAwait(false), $"copied to {rest[1]}")
                : Reply.Usage("usage: amneziageo config copy <name> <new-name>"),
            "remove" => rest.Count == 1
                ? Reply.Report(await agent.SendAsync(IpcContract.OpRemoveConfig, rest[0]).ConfigureAwait(false), $"removed {rest[0]}")
                : Reply.Usage("usage: amneziageo config remove <name>"),
            "dns" => await DnsAsync(agent, rest).ConfigureAwait(false),
            "exclusions" => await ExclusionsAsync(agent, rest).ConfigureAwait(false),
            "websocket" => await WebSocketAsync(agent, rest).ConfigureAwait(false),
            "geo" => await GeoAsync(agent, rest).ConfigureAwait(false),
            _ => Reply.Usage($"unknown config command '{args[0]}'"),
        };
    }

    private static int List(AgentClient agent)
    {
        var configs = agent.Snapshot.Configs;
        if (Output.Json)
        {
            Output.AsJson(configs);
            return Exit.Ok;
        }

        var rows = configs
            .Select(config => (IReadOnlyList<string>)
            [
                config.Name,
                config.Endpoint.Length > 0 ? config.Endpoint : "-",
                config.GeoSplit ? $"on ({config.Rules.Count.ToString(CultureInfo.InvariantCulture)})" : "off",
                config.WebSocket ? $"{config.WebSocketHost}:{config.WebSocketPort.ToString(CultureInfo.InvariantCulture)}" : "-",
                config.Dns.Length > 0 ? config.Dns : "-",
                config.Status,
            ])
            .ToList();

        Output.Table(["NAME", "ENDPOINT", "GEO", "WEBSOCKET", "DNS", "STATE"], rows, "no configurations yet");
        return Exit.Ok;
    }

    private static async Task<int> ShowAsync(AgentClient agent, IReadOnlyList<string> args)
    {
        if (args.Count != 1)
        {
            return Reply.Usage("usage: amneziageo config show <name>");
        }

        return Reply.Payload(await agent.SendAsync(IpcContract.OpGetConfig, args[0]).ConfigureAwait(false));
    }

    private static async Task<int> LinkAsync(AgentClient agent, IReadOnlyList<string> args)
    {
        if (args.Count != 1)
        {
            return Reply.Usage("usage: amneziageo config link <name>");
        }

        var ack = await agent.SendAsync(IpcContract.OpGetConfig, args[0]).ConfigureAwait(false);
        if (!ack.Ok)
        {
            return Reply.Report(ack);
        }

        Output.Line(VpnLinkCodec.Encode(ack.Message, args[0]));
        return Exit.Ok;
    }

    private static async Task<int> ImportAsync(AgentClient agent, IReadOnlyList<string> args)
    {
        var flags = Flags.Parse(args, "stdin");
        if (!flags.Allowed("file", "link", "text", "stdin"))
        {
            return Reply.Usage(flags.Error!);
        }

        if (!TextInput.TryRead(flags, out var text, out var error))
        {
            return Reply.Usage(error);
        }

        var imported = VpnLinkCodec.TryDecode(text);
        var confText = imported?.ConfText ?? text;
        if (!VpnLinkCodec.LooksLikeConf(confText))
        {
            return Reply.Usage("the input is not a configuration: expected wg-quick text, a vpn:// link, or the text of a QR code");
        }

        var name = flags.Positional.Count > 0
            ? flags.Positional[0]
            : Suggest(agent, imported?.Name ?? VpnLinkCodec.HostName(confText));

        if (name is null)
        {
            return Reply.Usage("the configuration name could not be derived from the input; pass it explicitly");
        }

        if (flags.Positional.Count > 1)
        {
            return Reply.Usage("usage: amneziageo config import [<name>] (--file <path> | --link <url> | --text <s> | --stdin)");
        }

        var ack = await agent.SendAsync(IpcContract.OpImportConfig, name, confText).ConfigureAwait(false);
        return Reply.Report(ack, $"imported {name}");
    }

    private static async Task<int> EditAsync(AgentClient agent, IReadOnlyList<string> args)
    {
        var flags = Flags.Parse(args, "stdin");
        if (!flags.Allowed("file", "text", "stdin"))
        {
            return Reply.Usage(flags.Error!);
        }

        if (flags.Positional.Count != 1)
        {
            return Reply.Usage("usage: amneziageo config edit <name> (--file <path> | --text <s> | --stdin)");
        }

        if (!TextInput.TryRead(flags, out var text, out var error))
        {
            return Reply.Usage(error);
        }

        var confText = VpnLinkCodec.TryDecode(text)?.ConfText ?? text;
        return Reply.Report(await agent.SendAsync(IpcContract.OpEditConfig, flags.Positional[0], confText).ConfigureAwait(false), $"saved {flags.Positional[0]}");
    }

    private static async Task<int> DnsAsync(AgentClient agent, IReadOnlyList<string> args)
    {
        if (args.Count is < 1 or > 2)
        {
            return Reply.Usage("usage: amneziageo config dns <name> [<servers>]");
        }

        var servers = args.Count > 1 ? args[1] : string.Empty;
        return Reply.Report(await agent.SendAsync(IpcContract.OpSetConfigDns, args[0], servers).ConfigureAwait(false));
    }

    private static async Task<int> ExclusionsAsync(AgentClient agent, IReadOnlyList<string> args)
    {
        var flags = Flags.Parse(args, "stdin", "clear");
        if (!flags.Allowed("file", "stdin", "list", "clear"))
        {
            return Reply.Usage(flags.Error!);
        }

        if (flags.Positional.Count != 1)
        {
            return Reply.Usage("usage: amneziageo config exclusions <name> (--file <path> | --stdin | --list a,b,c | --clear)");
        }

        var text = flags.Has("clear") ? string.Empty : flags.Value("list");
        if (text is null && !TextInput.TryRead(flags, out text, out var error))
        {
            return Reply.Usage(error);
        }

        return Reply.Report(await agent.SendAsync(IpcContract.OpSetConfigExclusions, flags.Positional[0], text ?? string.Empty).ConfigureAwait(false));
    }

    private static async Task<int> WebSocketAsync(AgentClient agent, IReadOnlyList<string> args)
    {
        var flags = Flags.Parse(args);
        if (!flags.Allowed("host", "port", "mtu", "ipv6"))
        {
            return Reply.Usage(flags.Error!);
        }

        if (flags.Positional.Count != 2 || !Toggle.TryParse(flags.Positional[1], out var on))
        {
            return Reply.Usage("usage: amneziageo config websocket <name> on|off [--host <h>] [--port <n>] [--mtu <n>] [--ipv6 on|off]");
        }

        var stored = agent.Snapshot.Configs.FirstOrDefault(config => config.Name == flags.Positional[0]);
        var port = flags.Value("port") ?? (stored?.WebSocketPort ?? 443).ToString(CultureInfo.InvariantCulture);
        var host = flags.Value("host") ?? stored?.WebSocketHost ?? string.Empty;
        var mtu = flags.Value("mtu") ?? (stored?.Mtu ?? 0).ToString(CultureInfo.InvariantCulture);
        var ipv6 = flags.Value("ipv6") ?? ((stored?.UseIpv6 ?? false) ? "on" : "off");
        if (!Toggle.TryParse(ipv6, out var useIpv6))
        {
            return Reply.Usage("--ipv6 takes on or off");
        }

        return Reply.Report(await agent.SendAsync(
            IpcContract.OpSetWebSocket,
            flags.Positional[0],
            Toggle.Text(on),
            port,
            host,
            mtu,
            Toggle.Text(useIpv6)).ConfigureAwait(false));
    }

    private static async Task<int> GeoAsync(AgentClient agent, IReadOnlyList<string> args)
    {
        if (args.Count < 2 || !Toggle.TryParse(args[1], out var on))
        {
            return Reply.Usage("usage: amneziageo config geo <name> on|off [rule...]");
        }

        // A per-config split has one bucket, so a role prefix carries nothing here.
        var rules = args.Skip(2).Select(Rules.StripProxyRole).ToArray();
        if (Rules.FirstInvalidBare(rules) is { } invalid)
        {
            return Reply.Usage($"'{invalid}' is not a rule; expected <geosite:x|geoip:x|domain:x|cidr:x|app:x>, roles belong to routing lists");
        }

        return Reply.Report(await agent.SendAsync(IpcContract.OpSetGeo, [args[0], Toggle.Text(on), .. rules]).ConfigureAwait(false));
    }

    // A free name derived from the imported link, so an unnamed import still lands somewhere sensible.
    private static string? Suggest(AgentClient agent, string? candidate)
    {
        if (candidate is not { Length: > 0 })
        {
            return null;
        }

        var taken = agent.Snapshot.Configs.Select(config => config.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return UniqueName.ResolveParen(candidate, taken);
    }
}
