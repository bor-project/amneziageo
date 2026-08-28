using System.Globalization;
using System.Text.Json;
using AmneziaGeo.Ipc;

namespace AmneziaGeo.Cli;

/// <summary>
/// Subscriptions: the addresses a library of configurations is kept in step with.
/// </summary>
internal static class SubscriptionCommands
{
    /// <summary>
    /// Runs one subscription command.
    /// </summary>
    public static async Task<int> RunAsync(IAgentLink agent, IReadOnlyList<string> args)
    {
        if (args.Count == 0)
        {
            return Reply.Usage("usage: amneziageo sub <list|add|refresh|remove>");
        }

        var rest = (IReadOnlyList<string>)[.. args.Skip(1)];
        return args[0] switch
        {
            "list" => await ListAsync(agent).ConfigureAwait(false),
            "add" => rest.Count is 1 or 2
                ? Reply.Report(await agent.SendAsync(IpcContract.OpAddSubscription, [.. rest]).ConfigureAwait(false))
                : Reply.Usage("usage: amneziageo sub add <url> [<name>]"),
            "refresh" => rest.Count switch
            {
                0 => Reply.Report(await agent.SendAsync(IpcContract.OpRefreshSubscription).ConfigureAwait(false)),
                1 => Reply.Report(await agent.SendAsync(IpcContract.OpRefreshSubscription, rest[0]).ConfigureAwait(false)),
                _ => Reply.Usage("usage: amneziageo sub refresh [<name>]"),
            },
            "remove" => rest.Count switch
            {
                1 => Reply.Report(await agent.SendAsync(IpcContract.OpRemoveSubscription, rest[0]).ConfigureAwait(false)),
                2 when string.Equals(rest[1], "--configs", StringComparison.Ordinal) =>
                    Reply.Report(await agent.SendAsync(IpcContract.OpRemoveSubscription, rest[0], "configs").ConfigureAwait(false)),
                _ => Reply.Usage("usage: amneziageo sub remove <name> [--configs]"),
            },
            _ => Reply.Usage($"unknown sub command '{args[0]}'"),
        };
    }

    private static async Task<int> ListAsync(IAgentLink agent)
    {
        var ack = await agent.SendAsync(IpcContract.OpListSubscriptions).ConfigureAwait(false);
        if (!ack.Ok)
        {
            return Reply.Report(ack);
        }

        var entries = Parse(ack.Message);
        if (Output.Json)
        {
            Output.AsJson(entries);
            return Exit.Ok;
        }

        var rows = entries
            .Select(entry => (IReadOnlyList<string>)
            [
                entry.Name,
                entry.Configs.ToString(CultureInfo.InvariantCulture) + (entry.Gone > 0 ? $" (-{entry.Gone})" : string.Empty),
                Traffic(entry),
                Moment(entry.ExpiresAt),
                Moment(entry.CheckedAt),
                entry.LastError.Length > 0 ? entry.LastError : "ok",
                entry.Url,
            ])
            .ToList();

        Output.Table(["NAME", "CONFIGS", "TRAFFIC", "EXPIRES", "READ", "STATE", "URL"], rows, "no subscriptions yet");
        return Exit.Ok;
    }

    private static IReadOnlyList<SubscriptionEntry> Parse(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<List<SubscriptionEntry>>(json, IpcJson.Options) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static string Traffic(SubscriptionEntry entry)
    {
        var used = Size(entry.Upload + entry.Download);
        return entry.Total > 0 ? $"{used} / {Size(entry.Total)}" : used;
    }

    private static string Size(long bytes)
    {
        string[] units = ["B", "K", "M", "G", "T"];
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return string.Create(CultureInfo.InvariantCulture, $"{value:0.#}{units[unit]}");
    }

    private static string Moment(long unixSeconds)
    {
        return unixSeconds > 0
            ? DateTimeOffset.FromUnixTimeSeconds(unixSeconds).ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)
            : "-";
    }
}
