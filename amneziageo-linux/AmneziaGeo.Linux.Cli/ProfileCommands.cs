using System.Globalization;
using AmneziaGeo.Ipc;

namespace AmneziaGeo.Linux.Cli;

/// <summary>
/// Profiles: the configuration a connect binds to, and its routing assignment.
/// </summary>
internal static class ProfileCommands
{
    /// <summary>
    /// Runs one profile command.
    /// </summary>
    public static async Task<int> RunAsync(AgentClient agent, IReadOnlyList<string> args)
    {
        if (args.Count == 0)
        {
            return Reply.Usage("usage: amneziageo profile <list|add|rename|remove|routing>");
        }

        var rest = (IReadOnlyList<string>)[.. args.Skip(1)];
        return args[0] switch
        {
            "list" => List(agent),
            "add" => rest.Count is 1 or 2
                ? Reply.Report(await agent.SendAsync(IpcContract.OpAddProfile, rest[0], rest.Count > 1 ? rest[1] : string.Empty).ConfigureAwait(false), $"saved {rest[0]}")
                : Reply.Usage("usage: amneziageo profile add <name> [<config>]"),
            "rename" => rest.Count == 2
                ? Reply.Report(await agent.SendAsync(IpcContract.OpRenameProfile, rest[0], rest[1]).ConfigureAwait(false), $"renamed to {rest[1]}")
                : Reply.Usage("usage: amneziageo profile rename <name> <new-name>"),
            "remove" => rest.Count == 1
                ? Reply.Report(await agent.SendAsync(IpcContract.OpRemoveProfile, rest[0]).ConfigureAwait(false), $"removed {rest[0]}")
                : Reply.Usage("usage: amneziageo profile remove <name>"),
            "routing" => await RoutingAsync(agent, rest).ConfigureAwait(false),
            _ => Reply.Usage($"unknown profile command '{args[0]}'"),
        };
    }

    private static int List(AgentClient agent)
    {
        var snapshot = agent.Snapshot;
        if (Output.Json)
        {
            Output.AsJson(snapshot.Profiles);
            return Exit.Ok;
        }

        var rows = snapshot.Profiles
            .Select(profile => (IReadOnlyList<string>)
            [
                profile.Name == snapshot.SelectedTarget ? "*" : " ",
                profile.Name,
                profile.Config.Length > 0 ? profile.Config : "-",
                Assignment(snapshot, profile),
                profile.Status,
            ])
            .ToList();

        Output.Table([" ", "NAME", "CONFIG", "ROUTING", "STATE"], rows, "no profiles yet");
        return Exit.Ok;
    }

    private static string Assignment(StatusSnapshot snapshot, ProfileEntry profile)
    {
        if (profile.RoutingListId is not { } id)
        {
            return "-";
        }

        var list = snapshot.RoutingLists?.FirstOrDefault(entry => entry.Id == id);
        var name = list is null ? id.ToString(CultureInfo.InvariantCulture) : $"{list.Name} (#{list.Id.ToString(CultureInfo.InvariantCulture)})";
        return profile.UseRouting ? name : $"{name} off";
    }

    private static async Task<int> RoutingAsync(AgentClient agent, IReadOnlyList<string> args)
    {
        if (args.Count is < 2 or > 3)
        {
            return Reply.Usage("usage: amneziageo profile routing <name> <list-id|list-name|none> [on|off]");
        }

        var listId = "none";
        if (args[1] != "none" && args[1] != "0")
        {
            if (RoutingCommands.Resolve(agent, args[1]) is not { } list)
            {
                return Reply.Usage($"routing list '{args[1]}' not found");
            }

            listId = list.Id.ToString(CultureInfo.InvariantCulture);
        }

        var use = listId != "none";
        if (args.Count == 3 && !Toggle.TryParse(args[2], out use))
        {
            return Reply.Usage("the last argument takes on or off");
        }

        return Reply.Report(await agent.SendAsync(IpcContract.OpAssignRouting, args[0], listId, Toggle.Text(use)).ConfigureAwait(false));
    }
}
