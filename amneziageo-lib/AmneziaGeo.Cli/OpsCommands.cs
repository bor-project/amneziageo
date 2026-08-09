using System.Reflection;
using AmneziaGeo.Ipc;

namespace AmneziaGeo.Cli;

/// <summary>
/// The contract itself: raw calls, the operation list, and the operations no group owns.
/// </summary>
internal static class OpsCommands
{
    /// <summary>
    /// Operations a named command already covers, mapped to it.
    /// </summary>
    private static readonly Dictionary<string, string> _owners = new(StringComparer.Ordinal)
    {
        [IpcContract.OpSetConnection] = "up / down",
        [IpcContract.OpSelectConfig] = "select",
        [IpcContract.OpGetConfig] = "config show / config link",
        [IpcContract.OpImportConfig] = "config import",
        [IpcContract.OpEditConfig] = "config edit",
        [IpcContract.OpRenameConfig] = "config rename",
        [IpcContract.OpCopyConfig] = "config copy",
        [IpcContract.OpRemoveConfig] = "config remove",
        [IpcContract.OpReorderConfigs] = "config order",
        [IpcContract.OpSetConfigDns] = "config dns",
        [IpcContract.OpSetConfigExclusions] = "config exclusions",
        [IpcContract.OpSetWebSocket] = "config websocket",
        [IpcContract.OpSetGeo] = "config geo",
        [IpcContract.OpAssignRouting] = "routing use",
        [IpcContract.OpSaveRoutingList] = "routing create / set / add",
        [IpcContract.OpGetRoutingList] = "routing show",
        [IpcContract.OpRemoveRoutingList] = "routing remove",
        [IpcContract.OpGetRoutingSettings] = "routing settings",
        [IpcContract.OpSetRoutingSettings] = "routing configure",
        [IpcContract.OpListGeo] = "geo list",
        [IpcContract.OpGetGeoEntries] = "geo show",
        [IpcContract.OpUpdateSources] = "geo update",
        [IpcContract.OpUpdateSource] = "geo update <source>",
        [IpcContract.OpDownloadGeo] = "geo download",
        [IpcContract.OpCheckSources] = "geo check",
        [IpcContract.OpCheckSource] = "geo check <source>",
        [IpcContract.OpAddSource] = "source add",
        [IpcContract.OpEditSource] = "source edit",
        [IpcContract.OpRemoveSource] = "source remove",
        [IpcContract.OpSetSetting] = "settings set",
        [IpcContract.OpReadLog] = "log tail / log follow",
        [IpcContract.OpClearLog] = "log clear",
        [IpcContract.OpExportLog] = "log export",
        [IpcContract.OpLogClient] = "log say",
        [IpcContract.OpGetRuntimeConfig] = "runtime",
        [IpcContract.OpGetCacheEntries] = "cache",
        [IpcContract.OpListLocalSubnets] = "subnets",
        [IpcContract.OpListProcesses] = "apps",
        [IpcContract.OpCollectDiagnostics] = "diag collect",
        [IpcContract.OpCheckUpdate] = "update check",
        [IpcContract.OpDownloadUpdate] = "update download",
        [IpcContract.OpApplyUpdate] = "update install",
        [IpcContract.OpCancelUpdateDownload] = "update cancel",
        [IpcContract.OpExportBundle] = "bundle export",
        [IpcContract.OpImportBundle] = "bundle import",
    };

    /// <summary>
    /// Runs one contract command.
    /// </summary>
    public static async Task<int> RunAsync(IAgentLink agent, string group, IReadOnlyList<string> args)
    {
        return group switch
        {
            "ipc" => await IpcAsync(agent, args).ConfigureAwait(false),
            "ops" => await OpsAsync(agent, args).ConfigureAwait(false),
            "apps" => await AppsAsync(agent, args).ConfigureAwait(false),
            "update" => await UpdateAsync(agent, args).ConfigureAwait(false),
            _ => Reply.Usage($"unknown command '{group}'"),
        };
    }

    /// <summary>
    /// Every operation the contract declares.
    /// </summary>
    public static IReadOnlyList<string> All() =>
        [.. typeof(IpcContract)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(field => field.IsLiteral && field.Name.StartsWith("Op", StringComparison.Ordinal))
            .Select(field => (string)field.GetRawConstantValue()!)
            .Order(StringComparer.Ordinal)];

    private static async Task<int> IpcAsync(IAgentLink agent, IReadOnlyList<string> args)
    {
        if (args.Count == 0)
        {
            return Reply.Usage("usage: ipc <op> [arg...]; run 'ops' for the operation list");
        }

        var ack = await agent.SendAsync(args[0], [.. args.Skip(1)]).ConfigureAwait(false);
        if (Output.Json)
        {
            Output.AsJson(new { op = args[0], ok = ack.Ok, message = AckText.Localize(ack.Message) });
            return ack.Ok ? Exit.Ok : Exit.Failed;
        }

        if (!ack.Ok)
        {
            Output.Error($"{args[0]}: {AckText.Localize(ack.Message)}");
            return Exit.Failed;
        }

        if (ack.Message.Length > 0)
        {
            Output.Line(ack.Message);
        }

        return Exit.Ok;
    }

    // Probes each operation with no arguments: a refusal naming the operation means the agent does not implement it.
    private static async Task<int> OpsAsync(IAgentLink agent, IReadOnlyList<string> args)
    {
        var flags = Flags.Parse(args, "probe");
        if (!flags.Allowed("probe"))
        {
            return Reply.Usage(flags.Error!);
        }

        var operations = All();
        if (!flags.Has("probe"))
        {
            var listing = operations.Select(op => (IReadOnlyList<string>)[op, _owners.GetValueOrDefault(op, "ipc " + op)]).ToList();
            if (Output.Json)
            {
                Output.AsJson(operations.Select(op => new { op, command = _owners.GetValueOrDefault(op, "ipc " + op) }));
                return Exit.Ok;
            }

            Output.Table(["OPERATION", "COMMAND"], listing);
            return Exit.Ok;
        }

        // Sending an operation without arguments separates the two refusals: a validation error comes from
        // its handler, so the operation is wired; the not-wired key comes from the dispatcher's default.
        var probes = new List<(string Op, string State, string Detail)>();
        foreach (var op in operations)
        {
            if (Skipped(op))
            {
                probes.Add((op, "-", "skipped: it would act for real"));
                continue;
            }

            var ack = await agent.SendAsync(op).ConfigureAwait(false);
            var text = AckText.Localize(ack.Message);
            probes.Add((op, Unwired(ack.Message) ? "!!" : "ok", ack.Ok ? "answered" : Head(text)));
        }

        if (Output.Json)
        {
            Output.AsJson(probes.Select(probe => new { op = probe.Op, state = probe.State, detail = probe.Detail }));
            return probes.Any(probe => probe.State == "!!") ? Exit.Unsupported : Exit.Ok;
        }

        var rows = probes.Select(probe => (IReadOnlyList<string>)[probe.State, probe.Op, probe.Detail]).ToList();
        Output.Table(["", "OPERATION", "DETAIL"], rows);
        Output.Info(string.Empty);
        Output.Info("ok = the agent's own handler answered, !! = the agent has no handler, - = not sent");
        return probes.Any(probe => probe.State == "!!") ? Exit.Unsupported : Exit.Ok;
    }

    private static async Task<int> AppsAsync(IAgentLink agent, IReadOnlyList<string> args)
    {
        var flags = Flags.Parse(args);
        if (!flags.Allowed("filter"))
        {
            return Reply.Usage(flags.Error!);
        }

        var ack = await agent.SendAsync(IpcContract.OpListProcesses).ConfigureAwait(false);
        if (!ack.Ok)
        {
            return Reply.Report(ack);
        }

        if (Output.Json)
        {
            Output.AsJson(Rows(ack.Message, flags.Value("filter")).Select(row => new
            {
                kind = row[0],
                label = row[1],
                value = row[2],
                detail = row.Count > 3 ? row[3] : string.Empty,
            }));
            return Exit.Ok;
        }

        Output.Table(["KIND", "LABEL", "RULE", "DETAIL"], Rows(ack.Message, flags.Value("filter")), "nothing is running that can be tunneled per app");
        return Exit.Ok;
    }

    private static async Task<int> UpdateAsync(IAgentLink agent, IReadOnlyList<string> args)
    {
        return (args.Count > 0 ? args[0] : string.Empty) switch
        {
            "check" => await CheckUpdateAsync(agent).ConfigureAwait(false),
            "download" => await DownloadUpdateAsync(agent).ConfigureAwait(false),
            "install" => Reply.Report(await agent.SendAsync(IpcContract.OpApplyUpdate).ConfigureAwait(false)),
            "cancel" => Reply.Report(await agent.SendAsync(IpcContract.OpCancelUpdateDownload).ConfigureAwait(false)),
            _ => Reply.Usage("usage: update check | update download | update install | update cancel"),
        };
    }

    // Starts the download and waits for the agent to settle; the progress rides the status snapshot.
    private static async Task<int> DownloadUpdateAsync(IAgentLink agent)
    {
        var started = await agent.SendAsync(IpcContract.OpDownloadUpdate).ConfigureAwait(false);
        if (!started.Ok)
        {
            return Reply.Report(started);
        }

        var settled = false;
        for (var i = 0; i < 3600 && !settled; i++)
        {
            var pending = agent.Snapshot;
            settled = pending.UpdateDownloaded || pending.UpdateDownloadFailed || (i > 4 && !pending.UpdateDownloading);
            if (!settled)
            {
                await Task.Delay(500).ConfigureAwait(false);
            }
        }

        var snapshot = agent.Snapshot;
        if (Output.Json)
        {
            Output.AsJson(new
            {
                ok = snapshot.UpdateDownloaded,
                version = snapshot.UpdateVersion,
                path = snapshot.UpdateSetupPath,
            });
            return snapshot.UpdateDownloaded ? Exit.Ok : Exit.Failed;
        }

        if (!snapshot.UpdateDownloaded)
        {
            Output.Error("the update was not downloaded; the agent log carries the reason");
            return Exit.Failed;
        }

        Output.Info($"downloaded {snapshot.UpdateVersion}");
        return Exit.Ok;
    }

    private static async Task<int> CheckUpdateAsync(IAgentLink agent)
    {
        var ack = await agent.SendAsync(IpcContract.OpCheckUpdate, "silent").ConfigureAwait(false);
        if (!ack.Ok)
        {
            return Reply.Report(ack);
        }

        var snapshot = agent.Snapshot;
        if (Output.Json)
        {
            Output.AsJson(new
            {
                message = AckText.Localize(ack.Message),
                available = snapshot.UpdateAvailable,
                version = snapshot.UpdateVersion,
            });
            return Exit.Ok;
        }

        Output.Pairs(
        [
            ("result", AckText.Localize(ack.Message)),
            ("available", snapshot.UpdateAvailable ? "yes" : "no"),
            ("version", snapshot.UpdateVersion.Length > 0 ? snapshot.UpdateVersion : "-"),
        ]);
        return Exit.Ok;
    }

    // list-processes answers tab-separated rows: kind, label, value, detail.
    private static List<IReadOnlyList<string>> Rows(string message, string? filter)
    {
        var rows = new List<IReadOnlyList<string>>();
        foreach (var line in message.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = line.TrimEnd('\r');
            if (filter is { Length: > 0 } && !trimmed.Contains(filter, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            rows.Add(trimmed.Split('\t'));
        }

        return rows;
    }

    // Operations that need no arguments to do their work: sending them is not a probe but the act itself.
    private static bool Skipped(string op) =>
        op is IpcContract.OpSetConnection or IpcContract.OpSelectConfig or IpcContract.OpLogClient
            or IpcContract.OpUpdateSources or IpcContract.OpUpdateSource
            or IpcContract.OpDownloadGeo or IpcContract.OpCollectDiagnostics or IpcContract.OpClearLog
            or IpcContract.OpAddConfig or IpcContract.OpImportConfig or IpcContract.OpEditConfig
            or IpcContract.OpImportBundle or IpcContract.OpRemoveConfig
            or IpcContract.OpRemoveRoutingList or IpcContract.OpRemoveSource
            or IpcContract.OpReportUpdateDownload or IpcContract.OpCancelUpdateDownload
            or IpcContract.OpDownloadUpdate or IpcContract.OpApplyUpdate;

    // Both agents answer an unimplemented operation with their own resource key.
    private static bool Unwired(string message) =>
        IpcMessage.TryParse(message, out var key, out _) && key is "Linux_OpNotWired" or "Android_OpNotWired";

    private static string Head(string text)
    {
        var line = text.Split('\n')[0].Trim();
        return line.Length <= 90 ? line : line[..90] + "...";
    }
}
