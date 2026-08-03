using System.Globalization;
using System.Text.Json;
using AmneziaGeo.Ipc;

namespace AmneziaGeo.Linux.Cli;

/// <summary>
/// Logs, runtime state and the checks a headless install needs.
/// </summary>
internal static class DiagCommands
{
    private static readonly string[] _tables = ["ageo", "routes"];

    /// <summary>
    /// Runs one diagnostics command.
    /// </summary>
    public static async Task<int> RunAsync(AgentClient agent, string group, IReadOnlyList<string> args)
    {
        return group switch
        {
            "log" => await LogAsync(agent, args).ConfigureAwait(false),
            "runtime" => Reply.Payload(await agent.SendAsync(IpcContract.OpGetRuntimeConfig).ConfigureAwait(false)),
            "cache" => await CacheAsync(agent, args).ConfigureAwait(false),
            "subnets" => Reply.Payload(await agent.SendAsync(IpcContract.OpListLocalSubnets).ConfigureAwait(false)),
            "doctor" => Doctor(agent),
            _ => Reply.Usage($"unknown command '{group}'"),
        };
    }

    private static async Task<int> LogAsync(AgentClient agent, IReadOnlyList<string> args)
    {
        if (args.Count == 0)
        {
            return Reply.Usage("usage: amneziageo log <tail|follow|clear|export>");
        }

        var flags = Flags.Parse([.. args.Skip(1)]);
        if (!flags.Allowed("table", "limit", "level", "search", "out", "interval"))
        {
            return Reply.Usage(flags.Error!);
        }

        var table = flags.Value("table") ?? "ageo";
        if (!_tables.Contains(table))
        {
            return Reply.Usage("--table takes ageo or routes");
        }

        return args[0] switch
        {
            "tail" => await TailAsync(agent, table, flags).ConfigureAwait(false),
            "follow" => await FollowAsync(agent, table, flags).ConfigureAwait(false),
            "clear" => Reply.Report(await agent.SendAsync(IpcContract.OpClearLog, table).ConfigureAwait(false), $"cleared {table}"),
            "export" => await ExportAsync(agent, table, flags).ConfigureAwait(false),
            _ => Reply.Usage($"unknown log command '{args[0]}'"),
        };
    }

    private static async Task<int> TailAsync(AgentClient agent, string table, Flags flags)
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
            Output.Info($"the {table} log is empty; raise the level with 'amneziageo settings set log-level info'");
        }

        return Exit.Ok;
    }

    private static async Task<int> FollowAsync(AgentClient agent, string table, Flags flags)
    {
        var interval = int.TryParse(flags.Value("interval"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? Math.Clamp(parsed, 1, 60)
            : 2;

        using var stop = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            stop.Cancel();
        };

        var newest = default(string);
        try
        {
            while (!stop.IsCancellationRequested)
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

                await Task.Delay(TimeSpan.FromSeconds(interval), stop.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }

        return Exit.Ok;
    }

    private static async Task<int> ExportAsync(AgentClient agent, string table, Flags flags)
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

    private static async Task<int> CacheAsync(AgentClient agent, IReadOnlyList<string> args)
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

    private static int Doctor(AgentClient agent)
    {
        var snapshot = agent.Snapshot;
        var categories = snapshot.Sources?.Sum(source => source.CategoryCount) ?? 0;
        var checks = new List<(string Name, bool Ok, string Detail)>
        {
            ("control socket", File.Exists(AgentClient.SocketPath), AgentClient.SocketPath),
            ("agent", true, snapshot.AgentVersion),
            ("tun device", File.Exists("/dev/net/tun"), "/dev/net/tun"),
            ("iproute2", Which("ip") is not null, Which("ip") ?? "ip not found in PATH"),
            ("systemd unit", Systemd.Exists, Systemd.Exists ? Systemd.State() : $"{Systemd.UnitPath} not installed"),
            ("profile selected", snapshot.SelectedTarget is { Length: > 0 }, snapshot.SelectedTarget ?? "nothing selected"),
            ("survive reboot", snapshot.SurviveReboot, snapshot.SurviveReboot ? "on" : "off: the agent will not connect after a reboot"),
            ("auto reconnect", snapshot.PeriodicReconnect, snapshot.PeriodicReconnect ? $"every {snapshot.PeriodicReconnectIntervalSeconds.ToString(CultureInfo.InvariantCulture)}s" : "off: a dropped tunnel stays down"),
            ("geo sources", (snapshot.Sources?.Count ?? 0) > 0, $"{(snapshot.Sources?.Count ?? 0).ToString(CultureInfo.InvariantCulture)} configured"),
            ("geo categories", categories > 0, categories > 0
                ? categories.ToString(CultureInfo.InvariantCulture)
                : "none loaded: run 'amneziageo geo download'"),
        };

        if (Output.Json)
        {
            Output.AsJson(checks.Select(check => new { name = check.Name, ok = check.Ok, detail = check.Detail }));
            return checks.All(check => check.Ok) ? Exit.Ok : Exit.Failed;
        }

        var rows = checks.Select(check => (IReadOnlyList<string>)[check.Ok ? "ok" : "!!", check.Name, check.Detail]).ToList();
        Output.Table(["", "CHECK", "DETAIL"], rows);
        return checks.All(check => check.Ok) ? Exit.Ok : Exit.Failed;
    }

    private static string? Which(string binary)
    {
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? "/usr/sbin:/usr/bin:/sbin:/bin").Split(':', StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(directory, binary);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private static async Task<(IpcAck? Ack, IReadOnlyList<string> Lines)> ReadAsync(AgentClient agent, string table, Flags flags)
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
