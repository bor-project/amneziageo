using System.Globalization;
using AmneziaGeo.Ipc;

namespace AmneziaGeo.Linux.Cli;

/// <summary>
/// Connection state: what the agent runs, what it would run, and switching between the two.
/// </summary>
internal static class StatusCommands
{
    /// <summary>
    /// Runs one connection command.
    /// </summary>
    public static async Task<int> RunAsync(AgentClient agent, string command, IReadOnlyList<string> args)
    {
        switch (command)
        {
            case "status":
                Print(agent.Snapshot);
                return Exit.Ok;

            case "watch":
                return await WatchAsync(agent).ConfigureAwait(false);

            case "select" when args.Count == 1:
                return Reply.Report(await agent.SendAsync(IpcContract.OpSelectProfile, args[0]).ConfigureAwait(false), $"selected {args[0]}");

            case "select":
                return Reply.Usage("usage: amneziageo select <profile|config>");

            case "up":
                return await UpAsync(agent, args).ConfigureAwait(false);

            case "down" when args.Count == 0:
                return Reply.Report(await agent.SendAsync(IpcContract.OpSetConnection, "disconnect").ConfigureAwait(false), "disconnected");

            default:
                return Reply.Usage($"usage: amneziageo {command}");
        }
    }

    /// <summary>
    /// Prints a snapshot as a status block plus the profile table.
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
            ("survive reboot", snapshot.SurviveReboot ? "on" : "off"),
            ("auto reconnect", reconnect),
            ("log level", snapshot.LogLevel),
        };

        if (snapshot.ConnectFailed)
        {
            pairs.Add(("last failure", $"{snapshot.ConnectFailReason} {snapshot.ConnectFailDetail}".Trim()));
        }

        Output.Pairs(pairs);

        var rows = snapshot.Profiles
            .Select(profile => (IReadOnlyList<string>)
            [
                profile.Name == snapshot.SelectedTarget ? "*" : " ",
                profile.Name,
                profile.Config.Length > 0 ? profile.Config : "-",
                RoutingLabel(snapshot, profile),
                profile.Status,
            ])
            .ToList();

        Output.Line();
        Output.Table([" ", "PROFILE", "CONFIG", "ROUTING", "STATE"], rows, "no profiles yet");
    }

    private static string RoutingLabel(StatusSnapshot snapshot, ProfileEntry profile)
    {
        if (profile.RoutingListId is not { } id)
        {
            return "-";
        }

        var list = snapshot.RoutingLists?.FirstOrDefault(entry => entry.Id == id);
        var name = list?.Name ?? id.ToString(CultureInfo.InvariantCulture);
        return profile.UseRouting ? name : $"{name} (off)";
    }

    private static async Task<int> UpAsync(AgentClient agent, IReadOnlyList<string> args)
    {
        if (args.Count > 1)
        {
            return Reply.Usage("usage: amneziageo up [<profile|config>]");
        }

        if (args.Count == 1)
        {
            var selected = await agent.SendAsync(IpcContract.OpSelectProfile, args[0]).ConfigureAwait(false);
            if (!selected.Ok)
            {
                return Reply.Report(selected);
            }
        }

        return Reply.Report(await agent.SendAsync(IpcContract.OpSetConnection, "connect").ConfigureAwait(false), "connected");
    }

    private static async Task<int> WatchAsync(AgentClient agent)
    {
        using var stop = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            stop.Cancel();
        };

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
            await Task.Delay(Timeout.Infinite, stop.Token).ConfigureAwait(false);
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
