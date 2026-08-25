using System.Globalization;
using AmneziaGeo.Ipc;

namespace AmneziaGeo.Cli;

/// <summary>
/// Auto-switching: the order the servers are walked in, what carries the default route, and what stands beside
/// it waiting to take it back.
/// </summary>
internal static class FailoverCommands
{
    private const string _usage = "usage: amneziageo failover [show | on|off | return <minutes>|off | skip <config> | use <config>]";

    /// <summary>
    /// Runs one auto-switching command.
    /// </summary>
    public static async Task<int> RunAsync(IAgentLink agent, IReadOnlyList<string> args)
    {
        if (args.Count == 0 || args[0] == "show")
        {
            return Show(agent.Snapshot);
        }

        // Everything below writes a setting nothing reads where only one tunnel goes up.
        if (!Available(agent.Snapshot))
        {
            return Unavailable();
        }

        switch (args[0])
        {
            case "return":
                return await ReturnAsync(agent, args).ConfigureAwait(false);

            case "skip" or "use":
                return await ParticipateAsync(agent, args).ConfigureAwait(false);
        }

        if (args.Count == 1 && Toggle.TryParse(args[0], out var on))
        {
            var ack = await agent.SendAsync(IpcContract.OpSetSetting, SettingKeys.FailoverEnabled, Toggle.Text(on)).ConfigureAwait(false);
            return Reply.Report(ack, on ? "the route moves off a server that stops answering" : "the route stays where it is");
        }

        return Reply.Usage(_usage);
    }

    /// <summary>
    /// Whether this agent raises the several tunnels the default route is carried between.
    /// </summary>
    public static bool Available(StatusSnapshot snapshot) => snapshot.MultiTunnel;

    /// <summary>
    /// Refuses a change this agent would store and never read.
    /// </summary>
    public static int Unavailable()
    {
        Output.Error("this agent raises one tunnel at a time, so nothing carries the route off a server here");
        return Exit.Unsupported;
    }

    // The priority list is the configuration order, so the table keeps that order whole and numbers only the
    // servers taking part. What each of them is doing comes from the same functions the agent decides by.
    private static int Show(StatusSnapshot snapshot)
    {
        var order = snapshot.Configs.Select(config => config.Name).ToList();
        var participants = FailoverPolicy.Participants(order, snapshot.FailoverSkipped);
        var settings = new FailoverSettings(snapshot.FailoverEnabled, snapshot.FailoverReturnMinutes);
        var holder = snapshot.DefaultRouteHeld;
        var holderUp = snapshot.Configs.Any(config => config.Name == holder && config.HandshakeAgeSeconds >= 0);
        var reserves = FailoverPolicy.Reserves(participants, holder, holderUp, settings).ToHashSet(StringComparer.Ordinal);

        if (Output.Json)
        {
            Output.AsJson(new
            {
                enabled = snapshot.FailoverEnabled,
                returnMinutes = snapshot.FailoverReturnMinutes,
                carries = holder,
                picked = snapshot.DefaultRouteOwner,
                servers = snapshot.Configs.Select(config => new
                {
                    name = config.Name,
                    priority = Priority(config.Name, participants),
                    role = Role(config.Name, participants, reserves, holder),
                    state = config.Status,
                    lossPercent = config.LossPercent,
                    rttMs = config.RttMs,
                    handshakeAgeSeconds = config.HandshakeAgeSeconds,
                    bitsPerSecond = config.RxBitsPerSecond + config.TxBitsPerSecond,
                }),
            });
            return Exit.Ok;
        }

        Output.Pairs(
        [
            ("auto switching", snapshot.FailoverEnabled ? "on" : "off"),
            ("going back", Back(snapshot.FailoverReturnMinutes)),
            ("carries", holder.Length > 0 ? holder : "-"),
            ("picked", snapshot.DefaultRouteOwner.Length > 0 ? snapshot.DefaultRouteOwner : "-"),
        ]);

        var rows = snapshot.Configs
            .Select(config => (IReadOnlyList<string>)
            [
                Place(config.Name, participants),
                config.Name,
                Role(config.Name, participants, reserves, holder),
                config.Status,
                Loss(config.LossPercent),
                Rtt(config.RttMs),
                Age(config.HandshakeAgeSeconds),
                Traffic(config),
            ])
            .ToList();

        Output.Line();
        Output.Table(["#", "CONFIG", "ROLE", "STATE", "LOSS", "RTT", "HANDSHAKE", "TRAFFIC"], rows, "no configurations yet");
        Note(snapshot, participants, holder);
        return Exit.Ok;
    }

    // Place in the walk, counted over the servers taking part; a skipped one keeps its row but no number.
    private static int Priority(string name, IReadOnlyList<string> participants)
    {
        for (var at = 0; at < participants.Count; at++)
        {
            if (string.Equals(participants[at], name, StringComparison.Ordinal))
            {
                return at + 1;
            }
        }

        return 0;
    }

    private static string Place(string name, IReadOnlyList<string> participants)
    {
        var at = Priority(name, participants);
        return at > 0 ? at.ToString(CultureInfo.InvariantCulture) : "-";
    }

    private static string Role(string name, IReadOnlyList<string> participants, IReadOnlyCollection<string> reserves, string holder)
    {
        if (Priority(name, participants) == 0)
        {
            return "skipped";
        }

        if (string.Equals(name, holder, StringComparison.Ordinal))
        {
            return "carries";
        }

        return reserves.Contains(name) ? "standby" : "-";
    }

    // What holds the route where it is, when a reader would otherwise see a list that never moves.
    private static void Note(StatusSnapshot snapshot, IReadOnlyList<string> participants, string holder)
    {
        if (!snapshot.MultiTunnel)
        {
            Output.Info(string.Empty);
            Output.Info("this agent raises one tunnel at a time, so nothing carries the route off a server here yet");
            return;
        }

        if (!snapshot.FailoverEnabled || holder.Length == 0)
        {
            return;
        }

        if (Priority(holder, participants) == 0)
        {
            Output.Info(string.Empty);
            Output.Info($"{holder} carries the route and is out of the list, so nothing carries it off; put it back with 'failover use {holder}'");
            return;
        }

        var above = FailoverPolicy.Above(participants, holder);
        if (snapshot.FailoverReturnMinutes > 0 && above.Count > 0)
        {
            Output.Info(string.Empty);
            Output.Info($"the route goes back up to {string.Join(", ", above)} once one of them answers for {snapshot.FailoverReturnMinutes.ToString(CultureInfo.InvariantCulture)} min and this tunnel falls silent");
        }
    }

    // Minutes a server standing higher must answer before the route goes back to it; off leaves it where it is.
    private static async Task<int> ReturnAsync(IAgentLink agent, IReadOnlyList<string> args)
    {
        if (args.Count != 2)
        {
            return Reply.Usage("usage: amneziageo failover return <minutes>|off");
        }

        if (Minutes(args[1]) is not { } minutes)
        {
            return Reply.Usage("failover return takes whole minutes between 0 and 1440, or off");
        }

        var ack = await agent.SendAsync(IpcContract.OpSetSetting, SettingKeys.FailoverReturnMinutes, minutes.ToString(CultureInfo.InvariantCulture)).ConfigureAwait(false);
        return Reply.Report(ack, Back(minutes));
    }

    // Leaves a server out of the walk, or puts it back. The setting carries the names left out, so a
    // configuration nobody names takes part.
    private static async Task<int> ParticipateAsync(IAgentLink agent, IReadOnlyList<string> args)
    {
        var skip = args[0] == "skip";
        if (args.Count != 2)
        {
            return Reply.Usage($"usage: amneziageo failover {args[0]} <config>");
        }

        var snapshot = agent.Snapshot;
        var name = args[1];
        if (!snapshot.Configs.Any(config => string.Equals(config.Name, name, StringComparison.Ordinal)))
        {
            return Reply.Usage($"unknown config: {name}");
        }

        var skipped = NameList.Split(snapshot.FailoverSkipped).ToList();
        if (skipped.Contains(name, StringComparer.Ordinal) == skip)
        {
            Output.Info(skip ? $"{name} is already out of the list" : $"{name} already takes part");
            return Exit.Ok;
        }

        if (skip)
        {
            skipped.Add(name);
        }
        else
        {
            skipped.Remove(name);
        }

        var ack = await agent.SendAsync(IpcContract.OpSetSetting, SettingKeys.FailoverSkipped, NameList.Join(skipped)).ConfigureAwait(false);
        var code = Reply.Report(ack, skip ? $"{name} is out of the list" : $"{name} takes part");
        if (!Output.Json && ack.Ok && skip && string.Equals(snapshot.DefaultRouteHeld, name, StringComparison.Ordinal))
        {
            Output.Info($"{name} carries the route now, and a server out of the list is never carried off it");
        }

        return code;
    }

    // Off is spelled as a number of minutes, so the two ways of saying it meet here.
    private static int? Minutes(string raw)
    {
        if (Toggle.TryParse(raw, out var on) && !on)
        {
            return 0;
        }

        return int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var minutes) && minutes is >= 0 and <= 1440
            ? minutes
            : null;
    }

    private static string Back(int minutes) =>
        minutes > 0 ? $"after {minutes.ToString(CultureInfo.InvariantCulture)} min of silence" : "off, the route stays where it is carried";

    private static string Loss(int percent) => LinkHealth.LossKnown(percent) ? $"{percent.ToString(CultureInfo.InvariantCulture)}%" : "-";

    private static string Rtt(int ms) => ms >= 0 ? $"{ms.ToString(CultureInfo.InvariantCulture)} ms" : "-";

    private static string Age(int seconds) => seconds >= 0 ? $"{seconds.ToString(CultureInfo.InvariantCulture)}s" : "-";

    // Both ways at once: what the return waits to fall silent is the traffic through the tunnel, whichever way
    // it goes.
    private static string Traffic(ConfigEntry config)
    {
        if (config.HandshakeAgeSeconds < 0)
        {
            return "-";
        }

        var bits = config.RxBitsPerSecond + config.TxBitsPerSecond;
        if (bits >= 1_000_000)
        {
            return $"{(bits / 1_000_000.0).ToString("0.0", CultureInfo.InvariantCulture)} Mbit/s";
        }

        return bits >= 1_000
            ? $"{(bits / 1_000).ToString(CultureInfo.InvariantCulture)} kbit/s"
            : $"{bits.ToString(CultureInfo.InvariantCulture)} bit/s";
    }
}
