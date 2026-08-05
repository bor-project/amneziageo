using System.Text.Json;
using AmneziaGeo.Ipc;

namespace AmneziaGeo.Cli;

/// <summary>
/// Portable bundles: moving configurations, routing lists and profiles between machines.
/// </summary>
internal static class BundleCommands
{
    private static readonly string[] _policies = ["new", "replace", "skip", "merge"];

    /// <summary>
    /// Runs one bundle command.
    /// </summary>
    public static async Task<int> RunAsync(IAgentLink agent, IReadOnlyList<string> args)
    {
        if (args.Count == 0)
        {
            return Reply.Usage("usage: amneziageo bundle <export|import>");
        }

        var rest = (IReadOnlyList<string>)[.. args.Skip(1)];
        return args[0] switch
        {
            "export" => await ExportAsync(agent, rest).ConfigureAwait(false),
            "import" => await ImportAsync(agent, rest).ConfigureAwait(false),
            _ => Reply.Usage($"unknown bundle command '{args[0]}'"),
        };
    }

    private static async Task<int> ExportAsync(IAgentLink agent, IReadOnlyList<string> args)
    {
        var flags = Flags.Parse(args, "all");
        if (!flags.Allowed("all", "profile", "config", "list", "out"))
        {
            return Reply.Usage(flags.Error!);
        }

        var snapshot = agent.Snapshot;
        var selection = flags.Has("all")
            ? new Selection(
                [.. snapshot.Profiles.Select(profile => profile.Name)],
                [.. snapshot.Configs.Select(config => config.Name)],
                [.. (snapshot.RoutingLists ?? []).Select(list => list.Name)])
            : new Selection([.. flags.Values("profile")], [.. flags.Values("config")], [.. flags.Values("list")]);

        if (selection.Profiles.Length + selection.Configs.Length + selection.RoutingLists.Length == 0)
        {
            return Reply.Usage("nothing selected: pass --all, or --profile/--config/--list");
        }

        var ack = await agent.SendAsync(IpcContract.OpExportBundle, JsonSerializer.Serialize(selection, IpcJson.Options)).ConfigureAwait(false);
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

        // The bundle carries private keys in the clear.
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            Output.Info($"wrote {path} (mode 600: it holds private keys)");
            return Exit.Ok;
        }

        Output.Info($"wrote {path}: it holds private keys, keep it out of shared folders");
        return Exit.Ok;
    }

    private static async Task<int> ImportAsync(IAgentLink agent, IReadOnlyList<string> args)
    {
        var flags = Flags.Parse(args, "stdin");
        if (!flags.Allowed("file", "stdin", "policy"))
        {
            return Reply.Usage(flags.Error!);
        }

        var policy = flags.Value("policy") ?? "new";
        if (!_policies.Contains(policy))
        {
            return Reply.Usage($"--policy takes one of {string.Join(", ", _policies)}");
        }

        if (!TextInput.TryRead(flags, out var text, out var error))
        {
            return Reply.Usage(error);
        }

        return Reply.Report(await agent.SendAsync(IpcContract.OpImportBundle, text, policy).ConfigureAwait(false));
    }

    /// <summary>
    /// What an export carries; a selected profile pulls in its config and routing list agent-side.
    /// </summary>
    private sealed record Selection(string[] Profiles, string[] Configs, string[] RoutingLists);
}
