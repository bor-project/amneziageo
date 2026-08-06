using System.Globalization;
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

        var reconnect = snapshot.PeriodicReconnect
            ? $"on, every {snapshot.PeriodicReconnectIntervalSeconds.ToString(CultureInfo.InvariantCulture)}s"
            : "off";

        var pairs = new List<(string, string)>
        {
            ("agent", snapshot.AgentVersion),
            ("state", snapshot.BoundStatus),
            ("tunnel", snapshot.Active ? "up" : "down"),
            ("bound to", snapshot.BoundTarget ?? "-"),
            ("selected", snapshot.SelectedTarget ?? "-"),
            ("routing", RoutingLabel(snapshot)),
            ("survive reboot", snapshot.SurviveReboot ? "on" : "off"),
            ("auto reconnect", reconnect),
            ("log level", snapshot.LogLevel),
        };

        if (snapshot.ConnectFailed)
        {
            pairs.Add(("last failure", $"{snapshot.ConnectFailReason} {snapshot.ConnectFailDetail}".Trim()));
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
