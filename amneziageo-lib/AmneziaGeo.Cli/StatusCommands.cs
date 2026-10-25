using System.Globalization;
using AmneziaGeo.Decl;
using AmneziaGeo.Ipc;

namespace AmneziaGeo.Cli;

/// <summary>
/// Connection state: what the agent runs, what it would run, and switching between the two.
/// </summary>
internal static class StatusCommands
{
    /// <summary>
    /// Runs one connection command.
    /// </summary>
    public static async Task<int> RunAsync(IAgentLink agent, string command, IReadOnlyList<string> args, CancellationToken ct)
    {
        switch (command)
        {
            case "status":
                Print(agent.Snapshot);
                return Exit.Ok;

            case "watch":
                return await WatchAsync(agent, ct).ConfigureAwait(false);

            case "select" when args.Count == 1:
                return Reply.Report(await agent.SendAsync(IpcContract.OpSelectConfig, args[0]).ConfigureAwait(false), $"selected {args[0]}");

            case "select":
                return Reply.Usage("usage: amneziageo select <config>");

            case "up":
                return await UpAsync(agent, args).ConfigureAwait(false);

            case "down" when args.Count == 0:
                return Reply.Report(await agent.SendAsync(IpcContract.OpSetConnection, "disconnect").ConfigureAwait(false), "disconnected");

            default:
                return Reply.Usage($"usage: amneziageo {command}");
        }
    }

    /// <summary>
    /// Prints a snapshot as a status block plus the config table.
    /// </summary>
    public static void Print(StatusSnapshot snapshot)
    {
        if (Output.Json)
        {
            Output.AsJson(snapshot);
            return;
        }

        var pairs = new List<(string, string)>
        {
            ("agent", snapshot.AgentVersion),
            ("state", snapshot.BoundStatus),
            ("tunnel", snapshot.Active ? "up" : "down"),
            ("bound to", snapshot.BoundTarget ?? "-"),
            ("selected", snapshot.SelectedTarget ?? "-"),
            ("routing", RoutingLabel(snapshot)),
            ("names", NameLine(snapshot)),
            ("survive reboot", snapshot.SurviveReboot ? "on" : "off"),
            ("auto reconnect", Reconnect(snapshot)),
            ("hold traffic", Hold(snapshot)),
            ("log level", snapshot.LogLevel),
        };

        if (snapshot.ConnectFailed)
        {
            pairs.Add(("last failure", $"{snapshot.ConnectFailReason} {snapshot.ConnectFailDetail}".Trim()));
        }

        if (snapshot.UpdateSources is { } sources)
        {
            pairs.Add(("update source", UpdateSourceLine(sources, snapshot.UpdateSource)));
            pairs.Add(("update channel", snapshot.UpdateChannel));
            if (snapshot.UpdateSourceError.Length > 0)
            {
                pairs.Add(("update error", AckText.Localize(snapshot.UpdateSourceError)));
            }
        }

        Output.Pairs(pairs);

        var rows = snapshot.Configs
            .Select(config => (IReadOnlyList<string>)
            [
                config.Name == snapshot.SelectedTarget ? "*" : " ",
                config.Name,
                config.Endpoint,
                config.Status,
            ])
            .ToList();

        Output.Line();
        Output.Table([" ", "CONFIG", "ENDPOINT", "STATE"], rows, "no configurations yet");
    }

    // The hold of the traffic of the configuration the agent runs, or would run; a dash while there is none.
    private static string Hold(StatusSnapshot snapshot)
    {
        var name = snapshot.BoundTarget ?? snapshot.SelectedTarget;
        var config = snapshot.Configs.FirstOrDefault(config => string.Equals(config.Name, name, StringComparison.Ordinal));
        return config is null ? "-" : config.HoldTraffic ? "on" : "off";
    }

    /// <summary>
    /// What the agent does with a dropped tunnel. An agent that always dials again takes the setting for the
    /// longest pause between the attempts only.
    /// </summary>
    public static string Reconnect(StatusSnapshot snapshot)
    {
        var every = $"every {snapshot.PeriodicReconnectIntervalSeconds.ToString(CultureInfo.InvariantCulture)}s";
        if (snapshot.ReconnectAlways)
        {
            return snapshot.PeriodicReconnect
                ? $"always, {every}"
                : $"always, pause up to {ConnectRetry.CeilingSeconds.ToString(CultureInfo.InvariantCulture)}s";
        }

        return snapshot.PeriodicReconnect ? $"on, {every}" : "off";
    }

    private static string NameLine(StatusSnapshot snapshot)
    {
        var upstream = snapshot.NameTransport.Length > 0 ? snapshot.NameTransport : "-";
        return snapshot.SystemNames.Length > 0 ? $"{upstream}, system asks by {snapshot.SystemNames}" : upstream;
    }

    // The source the check reads: its key with the host and the config it stands for.
    private static string UpdateSourceLine(IReadOnlyList<UpdateSourceEntry> sources, string key)
    {
        var entry = sources.FirstOrDefault(source => string.Equals(source.Key, key, StringComparison.OrdinalIgnoreCase));
        if (entry is null)
        {
            return key.Length > 0 ? key : "-";
        }

        return entry.Config.Length > 0 ? $"{entry.Key} ({entry.Host}, {entry.Config})" : $"{entry.Key} ({entry.Host})";
    }

    private static string RoutingLabel(StatusSnapshot snapshot)
    {
        if (snapshot.SelectedRoutingList is not { } id)
        {
            return "off";
        }

        var list = snapshot.RoutingLists?.FirstOrDefault(entry => entry.Id == id);
        return list?.Name ?? id.ToString(CultureInfo.InvariantCulture);
    }

    private static async Task<int> UpAsync(IAgentLink agent, IReadOnlyList<string> args)
    {
        if (args.Count > 1)
        {
            return Reply.Usage("usage: amneziageo up [<config>]");
        }

        if (args.Count == 1)
        {
            var selected = await agent.SendAsync(IpcContract.OpSelectConfig, args[0]).ConfigureAwait(false);
            if (!selected.Ok)
            {
                return Reply.Report(selected);
            }
        }

        return Reply.Report(await agent.SendAsync(IpcContract.OpSetConnection, "connect").ConfigureAwait(false), "connected");
    }

    private static async Task<int> WatchAsync(IAgentLink agent, CancellationToken ct)
    {
        var last = string.Empty;
        void OnSnapshot(StatusSnapshot snapshot)
        {
            var line = Line(snapshot);
            if (line == last)
            {
                return;
            }

            last = line;
            Output.Line($"{DateTime.Now:HH:mm:ss}  {line}");
        }

        agent.SnapshotReceived += OnSnapshot;
        OnSnapshot(agent.Snapshot);
        try
        {
            await Task.Delay(Timeout.Infinite, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            agent.SnapshotReceived -= OnSnapshot;
        }

        return Exit.Ok;
    }

    private static string Line(StatusSnapshot snapshot)
    {
        var failure = snapshot.ConnectFailed ? $" failure={snapshot.ConnectFailReason}:{snapshot.ConnectFailDetail}" : string.Empty;
        return $"state={snapshot.BoundStatus} tunnel={(snapshot.Active ? "up" : "down")} target={snapshot.BoundTarget ?? snapshot.SelectedTarget ?? "-"}{failure}";
    }
}
