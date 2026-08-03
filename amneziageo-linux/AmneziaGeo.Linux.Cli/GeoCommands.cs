using System.Globalization;
using System.Text.Json;
using AmneziaGeo.Ipc;

namespace AmneziaGeo.Linux.Cli;

/// <summary>
/// Geo databases: the categories they expose and the sources they come from.
/// </summary>
internal static class GeoCommands
{
    /// <summary>
    /// Runs one geo or source command.
    /// </summary>
    public static async Task<int> RunAsync(AgentClient agent, string group, IReadOnlyList<string> args)
    {
        if (args.Count == 0)
        {
            return Reply.Usage(group == "geo"
                ? "usage: amneziageo geo <list|show|update|download>"
                : "usage: amneziageo source <list|add|edit|remove>");
        }

        var rest = (IReadOnlyList<string>)[.. args.Skip(1)];
        if (group == "source")
        {
            return args[0] switch
            {
                "list" => Sources(agent),
                "add" => rest.Count == 2
                    ? Reply.Report(await agent.SendAsync(IpcContract.OpAddSource, rest[0], rest[1]).ConfigureAwait(false))
                    : Reply.Usage("usage: amneziageo source add geosite|geoip <url>"),
                "edit" => rest.Count == 3
                    ? Reply.Report(await agent.SendAsync(IpcContract.OpEditSource, rest[0], rest[1], rest[2]).ConfigureAwait(false))
                    : Reply.Usage("usage: amneziageo source edit <name> geosite|geoip <url>"),
                "remove" => rest.Count == 1
                    ? Reply.Report(await agent.SendAsync(IpcContract.OpRemoveSource, rest[0]).ConfigureAwait(false))
                    : Reply.Usage("usage: amneziageo source remove <name>"),
                _ => Reply.Usage($"unknown source command '{args[0]}'"),
            };
        }

        return args[0] switch
        {
            "list" => await ListAsync(agent, rest).ConfigureAwait(false),
            "show" => await ShowAsync(agent, rest).ConfigureAwait(false),
            "update" => rest.Count switch
            {
                0 => Reply.Report(await agent.SendAsync(IpcContract.OpUpdateSources).ConfigureAwait(false)),
                1 => Reply.Report(await agent.SendAsync(IpcContract.OpUpdateSource, rest[0]).ConfigureAwait(false)),
                _ => Reply.Usage("usage: amneziageo geo update [<source>]"),
            },
            "download" => Reply.Report(await agent.SendAsync(IpcContract.OpDownloadGeo).ConfigureAwait(false)),
            _ => Reply.Usage($"unknown geo command '{args[0]}'"),
        };
    }

    private static int Sources(AgentClient agent)
    {
        var sources = agent.Snapshot.Sources ?? [];
        if (Output.Json)
        {
            Output.AsJson(sources);
            return Exit.Ok;
        }

        var rows = sources
            .Select(source => (IReadOnlyList<string>)
            [
                source.Name,
                source.Kind,
                source.CategoryCount.ToString(CultureInfo.InvariantCulture),
                source.Updated ?? "never",
                source.Error ?? (source.Updating ? "updating" : "ok"),
                source.Url,
            ])
            .ToList();

        Output.Table(["NAME", "KIND", "CATEGORIES", "UPDATED", "STATE", "URL"], rows, "no geo sources yet");
        return Exit.Ok;
    }

    private static async Task<int> ListAsync(AgentClient agent, IReadOnlyList<string> args)
    {
        var flags = Flags.Parse(args);
        if (!flags.Allowed("filter"))
        {
            return Reply.Usage(flags.Error!);
        }

        var ack = await agent.SendAsync(IpcContract.OpListGeo).ConfigureAwait(false);
        if (!ack.Ok)
        {
            return Reply.Report(ack);
        }

        var tokens = ack.Message.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (flags.Value("filter") is { Length: > 0 } filter)
        {
            tokens = [.. tokens.Where(token => token.Contains(filter, StringComparison.OrdinalIgnoreCase))];
        }

        if (Output.Json)
        {
            Output.AsJson(tokens);
            return Exit.Ok;
        }

        foreach (var token in tokens)
        {
            Output.Line(token);
        }

        Output.Info($"{tokens.Length.ToString(CultureInfo.InvariantCulture)} categories");
        return Exit.Ok;
    }

    private static async Task<int> ShowAsync(AgentClient agent, IReadOnlyList<string> args)
    {
        var flags = Flags.Parse(args);
        if (!flags.Allowed("limit"))
        {
            return Reply.Usage(flags.Error!);
        }

        if (flags.Positional.Count != 1)
        {
            return Reply.Usage("usage: amneziageo geo show <rule> [--limit <n>]");
        }

        var limit = flags.Value("limit") ?? "300";
        var ack = await agent.SendAsync(IpcContract.OpGetGeoEntries, flags.Positional[0], limit).ConfigureAwait(false);
        if (!ack.Ok)
        {
            return Reply.Report(ack);
        }

        if (Output.Json)
        {
            Output.Line(ack.Message);
            return Exit.Ok;
        }

        var preview = JsonSerializer.Deserialize<GeoPreview>(ack.Message, IpcJson.Options);
        foreach (var entry in preview?.Entries ?? [])
        {
            Output.Line(entry);
        }

        Output.Info($"{(preview?.Entries.Count ?? 0).ToString(CultureInfo.InvariantCulture)} of {(preview?.Total ?? 0).ToString(CultureInfo.InvariantCulture)} entries");
        return Exit.Ok;
    }

    /// <summary>
    /// What a geo category expands to.
    /// </summary>
    private sealed record GeoPreview(int Total, IReadOnlyList<string> Entries);
}
