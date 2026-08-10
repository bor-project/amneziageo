using System.Globalization;
using System.Text.Json;
using AmneziaGeo.Ipc;

namespace AmneziaGeo.Cli;

/// <summary>
/// Logs, runtime state and the checks a headless install needs.
/// </summary>
internal static class DiagCommands
{
    private static readonly string[] _tables = ["ageo", "routes", "checks"];

    /// <summary>
    /// Runs one diagnostics command.
    /// </summary>
    public static async Task<int> RunAsync(IAgentLink agent, ICliHost host, string group, IReadOnlyList<string> args, CancellationToken ct)
    {
        return group switch
        {
            "log" => await LogAsync(agent, host, args, ct).ConfigureAwait(false),
            "runtime" => Reply.Payload(await agent.SendAsync(IpcContract.OpGetRuntimeConfig).ConfigureAwait(false)),
            "cache" => await CacheAsync(agent, args).ConfigureAwait(false),
            "subnets" => Reply.Payload(await agent.SendAsync(IpcContract.OpListLocalSubnets).ConfigureAwait(false)),
            "doctor" => Doctor(agent, host),
            "check" => await CheckAsync(agent, args).ConfigureAwait(false),
            "diag" => await DiagAsync(agent, args).ConfigureAwait(false),
            _ => Reply.Usage($"unknown command '{group}'"),
        };
    }

    private static async Task<int> LogAsync(IAgentLink agent, ICliHost host, IReadOnlyList<string> args, CancellationToken ct)
    {
        if (args.Count == 0)
        {
            return Reply.Usage($"usage: {host.ExeName} log <tail|follow|clear|export|say>");
        }

        if (args[0] == "say")
        {
            return args.Count == 2
                ? Reply.Report(await agent.SendAsync(IpcContract.OpLogClient, args[1]).ConfigureAwait(false), "written to the agent log")
                : Reply.Usage($"usage: {host.ExeName} log say <text>");
        }

        var flags = Flags.Parse([.. args.Skip(1)]);
        if (!flags.Allowed("table", "limit", "level", "search", "out", "interval"))
        {
            return Reply.Usage(flags.Error!);
        }

        var table = flags.Value("table") ?? "ageo";
        if (!_tables.Contains(table))
        {
            return Reply.Usage("--table takes ageo, routes or checks");
        }

        return args[0] switch
        {
            "tail" => await TailAsync(agent, host, table, flags).ConfigureAwait(false),
            "follow" => await FollowAsync(agent, table, flags, ct).ConfigureAwait(false),
            "clear" => Reply.Report(await agent.SendAsync(IpcContract.OpClearLog, table).ConfigureAwait(false), $"cleared {table}"),
            "export" => await ExportAsync(agent, table, flags).ConfigureAwait(false),
            _ => Reply.Usage($"unknown log command '{args[0]}'"),
        };
    }

    // The channel ladder, the sweep of every server, or one target; each answers with rows and a verdict.
    private static async Task<int> CheckAsync(IAgentLink agent, IReadOnlyList<string> args)
    {
        var target = args.Count > 0 ? args[0] : string.Empty;
        if (string.Equals(target, "servers", StringComparison.Ordinal))
        {
            return await ServersAsync(agent).ConfigureAwait(false);
        }

        var ack = target.Length == 0
            ? await agent.SendAsync(IpcContract.OpCheckChannel).ConfigureAwait(false)
            : await agent.SendAsync(IpcContract.OpCheckTarget, target).ConfigureAwait(false);

        if (!ack.Ok)
        {
            return Reply.Report(ack);
        }

        if (Output.Json)
        {
            Output.Line(ack.Message);
            return Exit.Ok;
        }

        return target.Length == 0 ? Channel(ack.Message) : Target(ack.Message);
    }

    private static int Channel(string payload)
    {
        var report = CheckReport.Parse(payload);
        var rows = report.Legs.Select(leg => (IReadOnlyList<string>)[leg.Name, leg.State, leg.Describe()]).ToList();
        Output.Table(["LEG", "STATE", "MEASURED"], rows, "nothing was measured");
        if (report.Advice is { } advice)
        {
            Output.Line(advice.Describe());
        }

        Output.Line(CheckPhrase.English(report.VerdictKey, report.VerdictArgs));
        return Exit.Ok;
    }

    // Every saved server, best first in the verdict.
    private static async Task<int> ServersAsync(IAgentLink agent)
    {
        var ack = await agent.SendAsync(IpcContract.OpCheckServers).ConfigureAwait(false);
        if (!ack.Ok)
        {
            return Reply.Report(ack);
        }

        if (Output.Json)
        {
            Output.Line(ack.Message);
            return Exit.Ok;
        }

        var report = SweepReport.Parse(ack.Message);
        var rows = report.Servers
            .Select(row => (IReadOnlyList<string>)[Mark(row), row.Config, row.State, row.Describe()])
            .ToList();

        Output.Table(["", "SERVER", "STATE", "MEASURED"], rows, "nothing was measured");
        if (report.Gateway is { } gateway)
        {
            Output.Line($"{gateway.Name}: {gateway.Describe()}");
        }

        Output.Line(CheckPhrase.English(report.VerdictKey, report.VerdictArgs));
        return Exit.Ok;
    }

    // The best of the sweep, and the one the tunnel is on.
    private static string Mark(SweepRow row)
    {
        if (row.Best)
        {
            return row.Live ? "*>" : "*";
        }

        return row.Live ? ">" : string.Empty;
    }

    private static int Target(string payload)
    {
        var report = TargetReport.Parse(payload);
        var rows = report.Facts.Select(fact => (IReadOnlyList<string>)[fact.Kind, fact.Name, fact.State, fact.Detail]).ToList();
        Output.Table(["KIND", "NAME", "STATE", "DETAIL"], rows, "nothing was found");
        Output.Line(TargetPhrase.English(report.VerdictKey, report.VerdictArgs));
        return Exit.Ok;
    }

    private static async Task<int> DiagAsync(IAgentLink agent, IReadOnlyList<string> args)
    {
        if (args.Count == 0 || args[0] != "collect")
        {
            return Reply.Usage("usage: diag collect");
        }

        var ack = await agent.SendAsync(IpcContract.OpCollectDiagnostics).ConfigureAwait(false);
        return ack.Ok ? Reply.Payload(ack) : Reply.Report(ack);
    }

    private static async Task<int> TailAsync(IAgentLink agent, ICliHost host, string table, Flags flags)
    {
        var page = await ReadAsync(agent, table, flags).ConfigureAwait(false);
        if (page.Ack is { Ok: false })
        {
            return Reply.Report(page.Ack);
        }

        if (Output.Json)
        {
            Output.Line(page.Ack!.Message);
            return Exit.Ok;
        }

        foreach (var line in page.Lines.Reverse())
        {
            Output.Line(line);
        }

        if (page.Lines.Count == 0)
        {
            Output.Info($"the {table} log is empty; raise the level with '{host.ExeName} settings set log-level info'");
        }

        return Exit.Ok;
    }

    private static async Task<int> FollowAsync(IAgentLink agent, string table, Flags flags, CancellationToken ct)
    {
        var interval = int.TryParse(flags.Value("interval"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? Math.Clamp(parsed, 1, 60)
            : 2;

        var newest = default(string);
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var page = await ReadAsync(agent, table, flags).ConfigureAwait(false);
                if (page.Ack is { Ok: false })
                {
                    return Reply.Report(page.Ack);
                }

                var fresh = newest is null ? page.Lines : Above(page.Lines, newest);
                foreach (var line in fresh.Reverse())
                {
                    Output.Line(line);
                }

                if (page.Lines.Count > 0)
                {
                    newest = page.Lines[0];
                }

                await Task.Delay(TimeSpan.FromSeconds(interval), ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }

        return Exit.Ok;
    }

    private static async Task<int> ExportAsync(IAgentLink agent, string table, Flags flags)
    {
        var ack = await agent.SendAsync(IpcContract.OpExportLog, table).ConfigureAwait(false);
        if (!ack.Ok)
        {
            return Reply.Report(ack);
        }

        if (flags.Value("out") is not { Length: > 0 } path)
        {
            Output.Line(ack.Message);
            return Exit.Ok;
        }

        await File.WriteAllTextAsync(path, ack.Message).ConfigureAwait(false);
        Output.Info($"wrote {path}");
        return Exit.Ok;
    }

    private static async Task<int> CacheAsync(IAgentLink agent, IReadOnlyList<string> args)
    {
        var flags = Flags.Parse(args);
        if (!flags.Allowed("filter"))
        {
            return Reply.Usage(flags.Error!);
        }

        var ack = await agent.SendAsync(IpcContract.OpGetCacheEntries).ConfigureAwait(false);
        if (!ack.Ok)
        {
            return Reply.Report(ack);
        }

        if (Output.Json)
        {
            Output.Line(ack.Message);
            return Exit.Ok;
        }

        var cache = JsonSerializer.Deserialize<CachePayload>(ack.Message, IpcJson.Options);
        var entries = (IEnumerable<CacheEntry>)(cache?.Entries ?? []);
        if (flags.Value("filter") is { Length: > 0 } filter)
        {
            entries = entries.Where(entry =>
                entry.Key.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                entry.Value.Contains(filter, StringComparison.OrdinalIgnoreCase));
        }

        var rows = entries.Select(entry => (IReadOnlyList<string>)[entry.Kind, entry.Key, entry.Value]).ToList();
        Output.Table(["KIND", "KEY", "VALUE"], rows, "the cache is empty");
        return Exit.Ok;
    }

    private static int Doctor(IAgentLink agent, ICliHost host)
    {
        var snapshot = agent.Snapshot;
        var categories = snapshot.Sources?.Sum(source => source.CategoryCount) ?? 0;
        var checks = new List<DoctorCheck>
        {
            new("agent", true, snapshot.AgentVersion),
        };

        checks.AddRange(host.DoctorChecks(snapshot));
        checks.AddRange(
        [
            new("config selected", snapshot.SelectedTarget is { Length: > 0 }, snapshot.SelectedTarget ?? "nothing selected"),
            new("survive reboot", snapshot.SurviveReboot, snapshot.SurviveReboot ? "on" : "off: the agent will not connect after a reboot"),
            new("auto reconnect", snapshot.PeriodicReconnect, snapshot.PeriodicReconnect
                ? $"every {snapshot.PeriodicReconnectIntervalSeconds.ToString(CultureInfo.InvariantCulture)}s"
                : "off: a dropped tunnel stays down"),
            new("geo sources", (snapshot.Sources?.Count ?? 0) > 0, $"{(snapshot.Sources?.Count ?? 0).ToString(CultureInfo.InvariantCulture)} configured"),
            new("geo categories", categories > 0, categories > 0
                ? categories.ToString(CultureInfo.InvariantCulture)
                : $"none loaded: run '{host.ExeName} geo download'"),
        ]);

        if (Output.Json)
        {
            Output.AsJson(checks.Select(check => new { name = check.Name, ok = check.Ok, detail = check.Detail }));
            return checks.All(check => check.Ok) ? Exit.Ok : Exit.Failed;
        }

        var rows = checks.Select(check => (IReadOnlyList<string>)[check.Ok ? "ok" : "!!", check.Name, check.Detail]).ToList();
        Output.Table(["", "CHECK", "DETAIL"], rows);
        return checks.All(check => check.Ok) ? Exit.Ok : Exit.Failed;
    }

    private static async Task<(IpcAck? Ack, IReadOnlyList<string> Lines)> ReadAsync(IAgentLink agent, string table, Flags flags)
    {
        var ack = await agent.SendAsync(
            IpcContract.OpReadLog,
            table,
            flags.Value("limit") ?? "200",
            "0",
            flags.Value("level") ?? string.Empty,
            flags.Value("search") ?? string.Empty).ConfigureAwait(false);

        if (!ack.Ok)
        {
            return (ack, []);
        }

        var page = JsonSerializer.Deserialize<LogPage>(ack.Message, IpcJson.Options);
        return (ack, page?.Lines ?? []);
    }

    // Lines arrive newest first; everything before the previous newest is new.
    private static IReadOnlyList<string> Above(IReadOnlyList<string> lines, string newest)
    {
        var index = lines.ToList().IndexOf(newest);
        return index < 0 ? lines : [.. lines.Take(index)];
    }

    /// <summary>
    /// One page of a log table.
    /// </summary>
    private sealed record LogPage(IReadOnlyList<string> Lines, long FirstId, bool HasOlder, int MatchCount);

    /// <summary>
    /// The agent's cached values.
    /// </summary>
    private sealed record CachePayload(int Total, bool Capped, IReadOnlyList<CacheEntry> Entries);

    /// <summary>
    /// One cached value.
    /// </summary>
    private sealed record CacheEntry(string Kind, string Key, string Value);
}
