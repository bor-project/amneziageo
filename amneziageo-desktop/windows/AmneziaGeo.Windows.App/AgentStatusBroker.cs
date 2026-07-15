using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using AmneziaGeo.Decl;
using AmneziaGeo.Ipc;
using Microsoft.Extensions.Logging;

namespace AmneziaGeo.Windows.App;

/// <summary>
/// Status snapshots broker for UI clients.
/// </summary>
internal sealed class AgentStatusBroker(ConfigRepository configRepo, IStateStore store, GeoConfigurator geo, GeoFileUpdater geoFileUpdater, GeoUpdateChecker geoUpdateChecker, AgentControl control, SettingsStore settingsStore, UpdateChecker updateChecker, UpdateState updateState, RouteManager routes, LogRingBuffer logBuffer, LogLevelController logLevel, DiagnosticsCollector diagnostics, ResettableFileSink logFileSink, ILogger<AgentStatusBroker> logger)
{
    private readonly List<PipeConnection> _clients = [];
    private readonly Lock _gate = new();
    private string? _lastJson;

    // UI sessions; tunnel outlives them only by the grace window. Guarded by _gate.
    private readonly HashSet<PipeConnection> _uiSessions = [];

    // Pending UI-gone teardown; cancelled on reattach. Guarded by _gate.
    private CancellationTokenSource? _teardownGrace;

    // Grace window; must exceed the UI client reconnect delay.
    private static readonly TimeSpan _uiTeardownGrace = TimeSpan.FromSeconds(5);

    // Per-source progress: 0-100 while downloading, -1 while re-materializing. Presence means "updating".
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, int> _updating = new(StringComparer.Ordinal);

    // Per-source "update available" flag; surfaced on SourceEntry.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool> _updateAvailable = new(StringComparer.Ordinal);

    // Per-source last failure message; surfaced on SourceEntry.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> _lastError = new(StringComparer.Ordinal);

    // At most one geo-refresh session at a time; concurrent triggers queue (sources unioned, force OR-ed).
    private readonly object _geoSessionGate = new();
    private bool _geoRunning;
    private bool _geoQueued;
    private bool _geoQueuedForce;
    private readonly HashSet<string> _geoQueuedNames = new(StringComparer.Ordinal);

    /// <summary>
    /// Profile reflected on the connection card.
    /// </summary>
    public string? BoundTarget => control.Running ? (control.RunningTarget ?? control.Target) : control.Target;

    /// <summary>
    /// Handles a connected client: sends the current snapshot, then reads until the client disconnects.
    /// </summary>
    public async Task HandleClientAsync(NamedPipeServerStream stream, CancellationToken ct)
    {
        var connection = new PipeConnection(stream);
        lock (_gate)
        {
            _clients.Add(connection);
        }

        logger.LogInformation("status client connected");
        try
        {
            var json = await BuildJsonAsync(ct);
            await connection.SendAsync(json, ct);
            using (var reader = new StreamReader(stream, new UTF8Encoding(false), false, 1024, leaveOpen: true))
            {
                while (!ct.IsCancellationRequested)
                {
                    var line = await reader.ReadLineAsync(ct);
                    if (line is null)
                    {
                        break;
                    }

                    if (line.Length == 0)
                    {
                        continue;
                    }

                    await HandleLineAsync(connection, line, ct);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException)
        {
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "status client handler failed");
        }
        finally
        {
            bool wasUi;
            lock (_gate)
            {
                _clients.Remove(connection);
                wasUi = _uiSessions.Remove(connection);
            }

            connection.Dispose();
            logger.LogInformation("status client disconnected");

            // Only a UI session drop can end the tunnel; scheduled outside the lock.
            if (wasUi)
            {
                OnUiSessionEnded();
            }
        }
    }

    private void MarkUiSession(PipeConnection connection)
    {
        lock (_gate)
        {
            _uiSessions.Add(connection);
            _teardownGrace?.Cancel();
            _teardownGrace?.Dispose();
            _teardownGrace = null;
        }

        logger.LogInformation("UI session attached");
    }

    private void OnUiSessionEnded()
    {
        CancellationToken graceToken;
        lock (_gate)
        {
            if (_uiSessions.Count > 0 || !control.Running)
            {
                // Another UI is still attached, or there is no tunnel to bring down.
                return;
            }

            _teardownGrace?.Cancel();
            _teardownGrace?.Dispose();
            _teardownGrace = new CancellationTokenSource();
            graceToken = _teardownGrace.Token;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(_uiTeardownGrace, graceToken);
            }
            catch (OperationCanceledException)
            {
                return; // UI re-attached
            }

            lock (_gate)
            {
                if (_uiSessions.Count > 0)
                {
                    return;
                }
            }

            // Survive-reboot keeps the tunnel up independent of the UI (#196); never tear it down on UI exit.
            var settings = await settingsStore.LoadAsync(CancellationToken.None);
            if (settings.SurviveReboot)
            {
                return;
            }

            if (control.Running)
            {
                logger.LogInformation("no UI connected; disconnecting tunnel");
                control.SetRunning(false);
            }
        });
    }

    /// <summary>
    /// Pushes a fresh snapshot to all clients when it differs from the last one sent.
    /// </summary>
    public async Task BroadcastIfChangedAsync(CancellationToken ct)
    {
        lock (_gate)
        {
            if (_clients.Count == 0)
            {
                return;
            }
        }

        var json = await BuildJsonAsync(ct);
        PipeConnection[] targets;
        lock (_gate)
        {
            if (json == _lastJson)
            {
                return;
            }

            _lastJson = json;
            targets = [.. _clients];
        }

        foreach (var target in targets)
        {
            try
            {
                await target.SendAsync(json, ct);
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException)
            {
            }
        }
    }

    private async Task HandleLineAsync(PipeConnection connection, string line, CancellationToken ct)
    {
        var envelope = JsonSerializer.Deserialize<IpcEnvelope>(line, IpcJson.Options);
        if (envelope is not { Type: IpcContract.CommandType, Command: not null })
        {
            return;
        }

        // Attach needs the connection identity, so it stays here.
        if (envelope.Command.Op == IpcContract.OpAttachUi)
        {
            MarkUiSession(connection);
            var attachAck = JsonSerializer.Serialize(new IpcEnvelope(IpcContract.AckType, Ack: new IpcAck(true, "attached")), IpcJson.Options);
            await connection.SendAsync(attachAck, ct);
            return;
        }

        var ack = await ExecuteCommandAsync(envelope.Command, ct);
        var ackLine = JsonSerializer.Serialize(new IpcEnvelope(IpcContract.AckType, Ack: ack), IpcJson.Options);
        await connection.SendAsync(ackLine, ct);
        if (ack.Ok)
        {
            await BroadcastIfChangedAsync(ct);
        }
    }

    private async Task<IpcAck> ExecuteCommandAsync(IpcCommand command, CancellationToken ct)
    {
        try
        {
            return command.Op switch
            {
                IpcContract.OpAddConfig => await AddConfigAsync(command.Args, ct),
                IpcContract.OpAddProfile => await AddProfileAsync(command.Args, ct),
                IpcContract.OpSetGeo => await SetGeoAsync(command.Args, ct),
                IpcContract.OpSetWebSocket => await SetWebSocketAsync(command.Args, ct),
                IpcContract.OpSetConfigDns => await SetConfigDnsAsync(command.Args, ct),
                IpcContract.OpSetConfigExclusions => await SetConfigExclusionsAsync(command.Args, ct),
                IpcContract.OpListLocalSubnets => ListLocalSubnets(),
                IpcContract.OpListGeo => await ListGeoAsync(ct),
                IpcContract.OpListProcesses => ListProcesses(),
                IpcContract.OpSaveRoutingList => await SaveRoutingListAsync(command.Args, ct),
                IpcContract.OpRemoveRoutingList => await RemoveRoutingListAsync(command.Args, ct),
                IpcContract.OpGetRoutingList => await GetRoutingListAsync(command.Args, ct),
                IpcContract.OpSetRoutingSettings => await SetRoutingSettingsAsync(command.Args, ct),
                IpcContract.OpGetRoutingSettings => await GetRoutingSettingsAsync(command.Args, ct),
                IpcContract.OpAssignRouting => await AssignRoutingAsync(command.Args, ct),
                IpcContract.OpSetConnection => SetConnection(command.Args),
                IpcContract.OpSetSetting => await SetSettingAsync(command.Args, ct),
                IpcContract.OpSelectProfile => await SelectProfileAsync(command.Args, ct),
                IpcContract.OpAddSource => await AddSourceAsync(command.Args, ct),
                IpcContract.OpRemoveSource => await RemoveSourceAsync(command.Args, ct),
                IpcContract.OpEditSource => await EditSourceAsync(command.Args, ct),
                IpcContract.OpUpdateSources => await UpdateSourcesAsync(ct),
                IpcContract.OpUpdateSource => await UpdateSourceAsync(command.Args, ct),
                IpcContract.OpCheckSources => await CheckSourcesAsync(ct),
                IpcContract.OpCheckSource => await CheckSourceAsync(command.Args, ct),
                IpcContract.OpGetConfig => await GetConfigAsync(command.Args, ct),
                IpcContract.OpImportConfig => await ImportConfigAsync(command.Args, ct),
                IpcContract.OpEditConfig => await EditConfigAsync(command.Args, ct),
                IpcContract.OpRemoveConfig => await RemoveConfigAsync(command.Args, ct),
                IpcContract.OpRemoveProfile => await RemoveProfileAsync(command.Args, ct),
                IpcContract.OpRenameConfig => await RenameConfigAsync(command.Args, ct),
                IpcContract.OpCopyConfig => await CopyConfigAsync(command.Args, ct),
                IpcContract.OpRenameProfile => await RenameProfileAsync(command.Args, ct),
                IpcContract.OpExportBundle => await ExportBundleAsync(command.Args, ct),
                IpcContract.OpImportBundle => await ImportBundleAsync(command.Args, ct),
                IpcContract.OpCheckUpdate => await CheckUpdateAsync(ct),
                IpcContract.OpDownloadGeo => await DownloadGeoAsync(ct),
                IpcContract.OpCollectDiagnostics => await CollectDiagnosticsAsync(ct),
                IpcContract.OpListLogs => ListLogs(),
                IpcContract.OpReadLog => ReadLog(command.Args),
                IpcContract.OpClearLog => ClearLog(),
                IpcContract.OpLogClient => LogClient(command.Args),
                _ => new IpcAck(false, $"unknown command: {command.Op}"),
            };
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "command {Op} failed", command.Op);
            return new IpcAck(false, ex.Message);
        }
    }

    // Records a UI-side diagnostic line in the agent log; the UI process keeps no log of its own.
    private IpcAck LogClient(IReadOnlyList<string> args)
    {
        if (args.Count < 1 || string.IsNullOrWhiteSpace(args[0]))
        {
            return new IpcAck(false, "log-client requires a message");
        }

        logger.LogWarning("ui: {Detail}", args[0]);
        return new IpcAck(true, "logged");
    }

    private async Task<IpcAck> AddConfigAsync(IReadOnlyList<string> args, CancellationToken ct)
    {
        if (args.Count < 2)
        {
            return new IpcAck(false, "add-config requires a name and a file path");
        }

        await configRepo.AddAsync(args[0], args[1], ct);
        logger.LogInformation("added config {Name}", args[0]);
        return new IpcAck(true, $"added config {args[0]}");
    }

    private async Task<IpcAck> GetConfigAsync(IReadOnlyList<string> args, CancellationToken ct)
    {
        if (args.Count < 1 || string.IsNullOrWhiteSpace(args[0]))
        {
            return new IpcAck(false, "get-config requires a name");
        }

        if (!await configRepo.ExistsAsync(args[0], ct))
        {
            return new IpcAck(false, $"unknown config: {args[0]}");
        }

        return new IpcAck(true, await configRepo.ReadTextAsync(args[0], ct));
    }

    private async Task<IpcAck> ImportConfigAsync(IReadOnlyList<string> args, CancellationToken ct)
    {
        if (args.Count < 2)
        {
            return new IpcAck(false, "import-config requires a name and config text");
        }

        await configRepo.AddFromTextAsync(args[0], args[1], ct);
        logger.LogInformation("imported config {Name}", args[0]);
        return new IpcAck(true, IpcMessage.Key("Agent_ConfigImported", args[0]));
    }

    private async Task<IpcAck> EditConfigAsync(IReadOnlyList<string> args, CancellationToken ct)
    {
        if (args.Count < 2)
        {
            return new IpcAck(false, "edit-config requires a name and config text");
        }

        if (!await configRepo.ExistsAsync(args[0], ct))
        {
            return new IpcAck(false, $"unknown config: {args[0]}");
        }

        // Running members pick up the new text on the next reconnect.
        await configRepo.EditFromTextAsync(args[0], args[1], ct);
        logger.LogInformation("edited config {Name}", args[0]);
        return new IpcAck(true, IpcMessage.Key("Agent_ConfigSaved", args[0]));
    }

    private async Task<IpcAck> RemoveConfigAsync(IReadOnlyList<string> args, CancellationToken ct)
    {
        if (args.Count < 1 || string.IsNullOrWhiteSpace(args[0]))
        {
            return new IpcAck(false, "remove-config requires a name");
        }

        var name = args[0];
        if (!await configRepo.ExistsAsync(name, ct))
        {
            return new IpcAck(false, $"unknown config: {name}");
        }

        // Refuse while the config is a live member of the running target.
        var bound = BoundTarget;
        if (control.Running && bound is not null)
        {
            var boundProfile = await store.GetProfileAsync(bound, ct);
            if (boundProfile is not null && string.Equals(boundProfile.Config, name, StringComparison.Ordinal))
            {
                return new IpcAck(false, $"config {name} is in use by the running profile {bound}; disconnect first");
            }
        }

        await configRepo.RemoveAsync(name, ct);
        await ClearBindingIfTargetAsync(name, ct);
        logger.LogInformation("removed config {Name}", name);
        return new IpcAck(true, $"removed config {name}");
    }

    private async Task<IpcAck> RemoveProfileAsync(IReadOnlyList<string> args, CancellationToken ct)
    {
        if (args.Count < 1 || string.IsNullOrWhiteSpace(args[0]))
        {
            return new IpcAck(false, "remove-profile requires a name");
        }

        var name = args[0];
        if (await store.GetProfileAsync(name, ct) is null)
        {
            return new IpcAck(false, $"unknown profile: {name}");
        }

        // Refuse while the profile is running.
        if (control.Running && string.Equals(name, BoundTarget, StringComparison.Ordinal))
        {
            return new IpcAck(false, $"profile {name} is running; disconnect first");
        }

        await store.RemoveProfileAsync(name, ct);
        await ClearBindingIfTargetAsync(name, ct);
        logger.LogInformation("removed profile {Name}", name);
        return new IpcAck(true, $"removed profile {name}");
    }

    // Clear target binding when the removed profile/config was selected.
    private async Task ClearBindingIfTargetAsync(string name, CancellationToken ct)
    {
        if (string.Equals(name, control.Target, StringComparison.Ordinal))
        {
            control.ClearTarget();
            await store.SetSettingAsync(AgentControl.SelectedTargetKey, string.Empty, ct);
        }
    }

    private async Task<IpcAck> CopyConfigAsync(IReadOnlyList<string> args, CancellationToken ct)
    {
        if (args.Count < 2 || string.IsNullOrWhiteSpace(args[0]) || string.IsNullOrWhiteSpace(args[1]))
        {
            return new IpcAck(false, "copy-config requires the source and destination name");
        }

        var source = args[0];
        var destination = args[1].Trim();
        if (!await configRepo.ExistsAsync(source, ct))
        {
            return new IpcAck(false, $"unknown config: {source}");
        }

        if (await configRepo.ExistsAsync(destination, ct) || await store.GetProfileAsync(destination, ct) is not null)
        {
            return new IpcAck(false, IpcMessage.Key("Agent_NameTaken", destination));
        }

        await configRepo.CopyAsync(source, destination, ct);
        logger.LogInformation("copied config {Source} -> {Dest}", source, destination);
        return new IpcAck(true, IpcMessage.Key("Agent_ConfigCopied", destination));
    }

    private async Task<IpcAck> RenameConfigAsync(IReadOnlyList<string> args, CancellationToken ct)
    {
        if (args.Count < 2 || string.IsNullOrWhiteSpace(args[0]) || string.IsNullOrWhiteSpace(args[1]))
        {
            return new IpcAck(false, "rename-config requires the current and new name");
        }

        var oldName = args[0];
        var newName = args[1].Trim();
        if (string.Equals(oldName, newName, StringComparison.Ordinal))
        {
            return new IpcAck(true, IpcMessage.Key("Agent_NameUnchanged"));
        }

        if (!await configRepo.ExistsAsync(oldName, ct))
        {
            return new IpcAck(false, $"unknown config: {oldName}");
        }

        if (await configRepo.ExistsAsync(newName, ct) || await store.GetProfileAsync(newName, ct) is not null)
        {
            return new IpcAck(false, IpcMessage.Key("Agent_NameTaken", newName));
        }

        // Refuse while the config is a live member of the running tunnel.
        if (control.Running && await IsRunningMemberAsync(oldName, ct))
        {
            return new IpcAck(false, $"config {oldName} is in use by the running tunnel; disconnect first");
        }

        await configRepo.RenameAsync(oldName, newName, ct);

        // A same-named single-config target follows the rename.
        if (string.Equals(oldName, control.Target, StringComparison.Ordinal))
        {
            control.SetTarget(newName);
            await store.SetSettingAsync(AgentControl.SelectedTargetKey, newName, ct);
        }

        logger.LogInformation("renamed config {Old} -> {New}", oldName, newName);
        return new IpcAck(true, IpcMessage.Key("Agent_RenamedTo", newName));
    }

    private async Task<IpcAck> RenameProfileAsync(IReadOnlyList<string> args, CancellationToken ct)
    {
        if (args.Count < 2 || string.IsNullOrWhiteSpace(args[0]) || string.IsNullOrWhiteSpace(args[1]))
        {
            return new IpcAck(false, "rename-profile requires the current and new name");
        }

        var oldName = args[0];
        var newName = args[1].Trim();
        if (string.Equals(oldName, newName, StringComparison.Ordinal))
        {
            return new IpcAck(true, IpcMessage.Key("Agent_NameUnchanged"));
        }

        var profile = await store.GetProfileAsync(oldName, ct);
        if (profile is null)
        {
            return new IpcAck(false, $"unknown profile: {oldName}");
        }

        if (await store.GetProfileAsync(newName, ct) is not null || await configRepo.ExistsAsync(newName, ct))
        {
            return new IpcAck(false, IpcMessage.Key("Agent_NameTaken", newName));
        }

        // Renaming the running profile is allowed: the live tunnel keeps running under its old in-memory
        // binding and the UI raises a "reconnect to apply" banner, as for any change to a live tunnel.
        var wasLiveTarget = control.Running && string.Equals(oldName, BoundTarget, StringComparison.Ordinal);

        // Carry routing assignment and selection across to the new name.
        await store.SaveProfileAsync(profile with { Name = newName }, ct);
        await store.RemoveProfileAsync(oldName, ct);

        var (listId, useRouting) = await store.GetProfileRoutingAsync(oldName, ct);
        if (listId is not null || useRouting)
        {
            await store.SetProfileRoutingAsync(newName, listId, useRouting, ct);
            await store.SetProfileRoutingAsync(oldName, null, false, ct);
        }

        // Carry the live status row so the renamed profile keeps showing its real state; without it the
        // bound status reads Disconnected (state is still keyed under the old name) until a reconnect.
        if (await store.GetProfileStateAsync(oldName, ct) is { } liveState)
        {
            await store.SaveProfileStateAsync(liveState with { Name = newName }, ct);
        }

        // Follow the rename in the live binding so the supervisor keeps resolving the profile: a stale
        // running target would look like a broken binding on the next re-dial and drop the tunnel.
        control.RetargetName(oldName, newName);
        if (string.Equals(newName, control.Target, StringComparison.Ordinal))
        {
            await store.SetSettingAsync(AgentControl.SelectedTargetKey, newName, ct);
        }

        if (wasLiveTarget)
        {
            control.SetRestartRequired();
        }

        logger.LogInformation("renamed profile {Old} -> {New}", oldName, newName);
        return new IpcAck(true, IpcMessage.Key("Agent_RenamedTo", newName));
    }

    // Export selection from OpExportBundle's arg0 JSON; all arrays optional. RoutingRules maps a routing
    // list name to the rule tokens to KEEP; an absent list keeps all its rules.
    private sealed record SelectionRequest(
        string[]? Profiles,
        string[]? Configs,
        string[]? RoutingLists,
        Dictionary<string, string[]>? RoutingRules);

    // Export a selective bundle: caller picks configs, routing lists, and profiles by name.
    private async Task<IpcAck> ExportBundleAsync(IReadOnlyList<string> args, CancellationToken ct)
    {
        if (args.Count < 1 || string.IsNullOrWhiteSpace(args[0]))
        {
            return new IpcAck(false, "export-bundle requires a selection json");
        }

        SelectionRequest? selection;
        try
        {
            selection = JsonSerializer.Deserialize<SelectionRequest>(
                args[0],
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch (JsonException)
        {
            selection = null;
        }

        if (selection is null)
        {
            return new IpcAck(false, IpcMessage.Key("Agent_ExportSelectionParseFailed"));
        }

        // Resolve the EFFECTIVE set of config/routing-list names: the explicitly picked entries, plus
        // (for every selected profile) the config and routing list it binds - so picking a profile travels
        // with what it needs to reconnect, without the caller having to also tick its dependencies by hand.
        var configNames = new HashSet<string>(selection.Configs ?? [], StringComparer.Ordinal);
        var routingNames = new HashSet<string>(selection.RoutingLists ?? [], StringComparer.Ordinal);
        var profileNames = new HashSet<string>(selection.Profiles ?? [], StringComparer.Ordinal);

        foreach (var profileName in profileNames)
        {
            var profile = await store.GetProfileAsync(profileName, ct);
            if (profile is null)
            {
                continue;
            }

            if (!string.IsNullOrEmpty(profile.Config))
            {
                configNames.Add(profile.Config);
            }

            var (listId, _) = await store.GetProfileRoutingAsync(profileName, ct);
            if (listId is not null && await store.GetRoutingListAsync(listId.Value, ct) is { } boundList)
            {
                routingNames.Add(boundList.Name);
            }
        }

        if (configNames.Count == 0 && routingNames.Count == 0 && profileNames.Count == 0)
        {
            return new IpcAck(false, IpcMessage.Key("Agent_NothingSelectedForExport"));
        }

        var configBlocks = new List<PortableBundle.ConfigBlock>();
        foreach (var name in configNames)
        {
            // Skip if the config vanished between selection and export.
            if (!await configRepo.ExistsAsync(name, ct))
            {
                continue;
            }

            var configText = await configRepo.ReadTextAsync(name, ct);

            PortableBundle.TransportBlock? transport = null;
            var tr = await store.GetConfigTransportAsync(name, ct);
            if (tr is not null)
            {
                transport = new PortableBundle.TransportBlock(tr.UseWebSocket, tr.WebSocketHost, tr.WebSocketPort, tr.Mtu);
            }

            PortableBundle.GeoBlock? geoBlock = null;
            var ownGeo = await store.GetTunnelGeoAsync(name, ct);
            if (ownGeo is not null && (ownGeo.GeoSplit || ownGeo.Rules.Count > 0))
            {
                geoBlock = new PortableBundle.GeoBlock(ownGeo.GeoSplit, ownGeo.Rules.Select(GeoConfigurator.Format).ToList());
            }

            configBlocks.Add(new PortableBundle.ConfigBlock(name, configText, transport, geoBlock));
        }

        var routingBlocks = new List<PortableBundle.RoutingBlock>();
        if (routingNames.Count > 0)
        {
            var allLists = await store.ListRoutingListsAsync(ct);
            foreach (var name in routingNames)
            {
                var list = allLists.FirstOrDefault(l => string.Equals(l.Name, name, StringComparison.Ordinal));
                if (list is null)
                {
                    continue;
                }

                // Role-tagged: a routing list's rules carry a bucket (proxy/direct/block/exclude). Formatting them
                // bare would re-import every rule as Proxy and drop the bucket. Also matches the tokens the export
                // tree shows (get-routing-list), so the keep-filter below compares like for like.
                var rules = list.Rules.Select(GeoConfigurator.FormatWithRole).ToList();

                // Drop rules the user unchecked in the export tree (machine-specific app rules, etc).
                // No entry for this list = keep everything.
                if (selection.RoutingRules is not null
                    && selection.RoutingRules.TryGetValue(name, out var kept))
                {
                    var keepSet = new HashSet<string>(kept, StringComparer.Ordinal);
                    rules = rules.Where(keepSet.Contains).ToList();
                }

                PortableBundle.RoutingSettingsBlock? settingsBlock = null;
                var settings = await store.GetRoutingSettingsAsync(list.Id, ct);
                if (settings is not null)
                {
                    settingsBlock = new PortableBundle.RoutingSettingsBlock(settings.Exclusions, settings.AllUdp, settings.UseIpv6);
                }

                routingBlocks.Add(new PortableBundle.RoutingBlock(name, rules, settingsBlock));
            }
        }

        var profileBlocks = new List<PortableBundle.ProfileBlock>();
        foreach (var name in profileNames)
        {
            var profile = await store.GetProfileAsync(name, ct);
            if (profile is null)
            {
                continue;
            }

            var (listId, useRouting) = await store.GetProfileRoutingAsync(name, ct);
            string? routingListName = null;
            if (listId is not null && await store.GetRoutingListAsync(listId.Value, ct) is { } list)
            {
                routingListName = list.Name;
            }

            profileBlocks.Add(new PortableBundle.ProfileBlock(
                name,
                string.IsNullOrEmpty(profile.Config) ? null : profile.Config,
                routingListName,
                useRouting));
        }

        var bundle = new PortableBundle.Bundle(
            PortableBundle.FormatTag,
            PortableBundle.CurrentVersion,
            configBlocks,
            routingBlocks,
            profileBlocks);

        logger.LogInformation(
            "exported bundle: {Configs} configs, {Routing} routing lists, {Profiles} profiles",
            configBlocks.Count,
            routingBlocks.Count,
            profileBlocks.Count);
        return new IpcAck(true, PortableBundle.Serialize(bundle));
    }

    // Import a selective bundle: recreates configs, routing lists, and profiles under fresh names. All-or-nothing; no rollback of rows already written.
    private async Task<IpcAck> ImportBundleAsync(IReadOnlyList<string> args, CancellationToken ct)
    {
        if (args.Count < 1 || string.IsNullOrWhiteSpace(args[0]))
        {
            return new IpcAck(false, "import-bundle requires the bundle json");
        }

        // How to treat a name already present: new (add a numbered copy), replace, skip, or merge.
        var policy = args.Count > 1 ? args[1] : "new";

        PortableBundle.Bundle? bundle;
        try
        {
            bundle = PortableBundle.Deserialize(args[0]);
        }
        catch (JsonException ex)
        {
            return new IpcAck(false, IpcMessage.Key("Agent_BundleParseFailed", ex.Message));
        }

        if (bundle is null || !string.Equals(bundle.Format, PortableBundle.FormatTag, StringComparison.Ordinal))
        {
            return new IpcAck(false, IpcMessage.Key("Agent_NotAnAmneziaGeoFile"));
        }

        if (bundle.Version > PortableBundle.CurrentVersion)
        {
            return new IpcAck(false, IpcMessage.Key("Agent_BundleTooNew", bundle.Version));
        }

        // Config and profile names live in one global namespace (rename refuses a name used by either);
        // routing-list names are a separate space. Snapshots taken before import drive collision detection.
        var existingConfigs = new HashSet<string>(await configRepo.ListAsync(ct), StringComparer.Ordinal);
        var existingProfiles = new HashSet<string>(await store.ListProfileNamesAsync(ct), StringComparer.Ordinal);
        var existingLists = (await store.ListRoutingListsAsync(ct))
            .ToDictionary(l => l.Name, l => l, StringComparer.Ordinal);

        // Growing namespaces so the add-as-new path never reuses a name taken earlier in THIS import.
        var taken = new HashSet<string>(existingConfigs, StringComparer.Ordinal);
        foreach (var p in existingProfiles)
        {
            taken.Add(p);
        }

        var listNames = new HashSet<string>(existingLists.Keys, StringComparer.Ordinal);

        var configNameMap = new Dictionary<string, string>(StringComparer.Ordinal);
        var routingMap = new Dictionary<string, (string Name, long Id)>(StringComparer.Ordinal);
        var renames = new List<string>();

        foreach (var block in bundle.Configs)
        {
            var incoming = SanitizeFileName(block.Name);

            // Same-name config already here and a non-default policy: act in place, keeping its bindings.
            if (existingConfigs.Contains(incoming) && policy != "new")
            {
                if (policy == "skip")
                {
                    configNameMap[block.Name] = incoming;
                    continue;
                }

                // Replace and merge both take the file's text/transport; they differ only in the geo rules.
                await configRepo.EditFromTextAsync(incoming, block.ConfigText, ct);
                if (block.Transport is { } trE)
                {
                    await store.SetConfigTransportAsync(new ConfigTransport(incoming, trE.UseWebSocket, trE.Host, trE.Port, trE.Mtu), ct);
                }

                if (block.Geo is { } gE)
                {
                    var rules = gE.Rules;
                    if (policy == "merge")
                    {
                        var own = await store.GetTunnelGeoAsync(incoming, ct);
                        var keep = own?.Rules.Select(GeoConfigurator.Format) ?? Enumerable.Empty<string>();
                        rules = keep.Concat(gE.Rules).Distinct(StringComparer.Ordinal).ToList();
                    }

                    await geo.ApplyAsync(incoming, gE.Split, rules, ct);
                }

                configNameMap[block.Name] = incoming;
                continue;
            }

            var finalName = FreeName(incoming, taken);
            taken.Add(finalName);
            if (!string.Equals(finalName, block.Name, StringComparison.Ordinal))
            {
                renames.Add($"«{block.Name}» → «{finalName}»");
            }

            // Malformed config text throws here and aborts the import.
            await configRepo.AddFromTextAsync(finalName, block.ConfigText, ct);

            if (block.Transport is { } tr)
            {
                await store.SetConfigTransportAsync(new ConfigTransport(finalName, tr.UseWebSocket, tr.Host, tr.Port, tr.Mtu), ct);
            }

            if (block.Geo is { } g)
            {
                // Re-materialize rule tokens against local geo data.
                await geo.ApplyAsync(finalName, g.Split, g.Rules, ct);
            }

            configNameMap[block.Name] = finalName;
        }

        foreach (var block in bundle.RoutingLists)
        {
            // Same-name list already here and a non-default policy: act on the existing row (id kept, so
            // profiles bound to it stay bound).
            if (existingLists.TryGetValue(block.Name, out var existingList) && policy != "new")
            {
                if (policy == "skip")
                {
                    routingMap[block.Name] = (existingList.Name, existingList.Id);
                    continue;
                }

                // Role-tagged, so a merge keeps the existing rules in their own buckets. A pre-role bundle carries
                // bare tokens; those import as Proxy, as they did before roles existed.
                List<string> rules = policy == "merge"
                    ? existingList.Rules.Select(GeoConfigurator.FormatWithRole).Concat(block.Rules).Distinct(StringComparer.Ordinal).ToList()
                    : block.Rules.ToList();
                await geo.ApplyToRoutingListAsync(existingList.Id, existingList.Name, rules, ct);
                if (block.Settings is { } sE)
                {
                    await store.SetRoutingSettingsAsync(new RoutingSettings(existingList.Id, sE.Exclusions, sE.AllUdp, "split", sE.UseIpv6), ct);
                }

                routingMap[block.Name] = (existingList.Name, existingList.Id);
                continue;
            }

            var finalName = FreeName(block.Name, listNames);
            listNames.Add(finalName);
            if (!string.Equals(finalName, block.Name, StringComparison.Ordinal))
            {
                renames.Add($"«{block.Name}» → «{finalName}»");
            }

            var newId = await geo.ApplyToRoutingListAsync(0, finalName, block.Rules, ct);
            if (block.Settings is { } s)
            {
                await store.SetRoutingSettingsAsync(new RoutingSettings(newId, s.Exclusions, s.AllUdp, "split", s.UseIpv6), ct);
            }

            routingMap[block.Name] = (finalName, newId);
        }

        foreach (var block in bundle.Profiles)
        {
            var config = block.Config is not null && configNameMap.TryGetValue(block.Config, out var cn) ? cn : string.Empty;
            long? routingId = block.RoutingList is not null && routingMap.TryGetValue(block.RoutingList, out var rl)
                ? rl.Id
                : null;

            // Same-name profile already here and a non-default policy.
            if (existingProfiles.Contains(block.Name) && policy != "new")
            {
                if (policy == "skip")
                {
                    continue;
                }

                // Keep an existing config binding the file leaves empty (both replace and merge), so a
                // restore whose profile omits the config never orphans a working profile. Symmetric with
                // routing below, which is likewise preserved when the file carries none.
                var boundConfig = config;
                if (config.Length == 0)
                {
                    boundConfig = (await store.GetProfileAsync(block.Name, ct))?.Config ?? string.Empty;
                }

                await store.SaveProfileAsync(new Profile(block.Name, boundConfig), ct);

                // No auto-target here: bulk import must not steal the selection.
                if (routingId is not null)
                {
                    await store.SetProfileRoutingAsync(block.Name, routingId.Value, block.UseRouting, ct);
                }

                continue;
            }

            var finalName = FreeName(block.Name, taken);
            taken.Add(finalName);
            if (!string.Equals(finalName, block.Name, StringComparison.Ordinal))
            {
                renames.Add($"«{block.Name}» → «{finalName}»");
            }

            await store.SaveProfileAsync(new Profile(finalName, config), ct);

            // No auto-target here: bulk import must not steal the selection.
            if (routingId is not null)
            {
                await store.SetProfileRoutingAsync(finalName, routingId.Value, block.UseRouting, ct);
            }
        }

        logger.LogInformation(
            "imported bundle: {Configs} configs, {Routing} routing lists, {Profiles} profiles",
            bundle.Configs.Count,
            bundle.RoutingLists.Count,
            bundle.Profiles.Count);

        if (renames.Count == 0)
        {
            return new IpcAck(true, IpcMessage.Key(
                "Agent_BundleImported",
                bundle.Configs.Count,
                bundle.RoutingLists.Count,
                bundle.Profiles.Count));
        }

        if (renames.Count <= 5)
        {
            return new IpcAck(true, IpcMessage.Key(
                "Agent_BundleImportedRenamed",
                bundle.Configs.Count,
                bundle.RoutingLists.Count,
                bundle.Profiles.Count,
                string.Join(", ", renames)));
        }

        return new IpcAck(true, IpcMessage.Key(
            "Agent_BundleImportedRenamedMany",
            bundle.Configs.Count,
            bundle.RoutingLists.Count,
            bundle.Profiles.Count));
    }

    // True when a profile still binds the config.
    private async Task<bool> ConfigInUseAsync(string config, CancellationToken ct)
    {
        foreach (var profileName in await store.ListProfileNamesAsync(ct))
        {
            var profile = await store.GetProfileAsync(profileName, ct);
            if (profile is not null && string.Equals(profile.Config, config, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    // Returns the desired name if free, otherwise appends " (2)", " (3)", … until one is not taken.
    private static string FreeName(string desired, HashSet<string> taken)
    {
        var baseName = desired.Trim();
        if (baseName.Length == 0)
        {
            baseName = "Профиль";
        }

        if (!taken.Contains(baseName))
        {
            return baseName;
        }

        for (var i = 2; i < 10000; i++)
        {
            var candidate = $"{baseName} ({i})";
            if (!taken.Contains(candidate))
            {
                return candidate;
            }
        }

        return $"{baseName} ({Guid.NewGuid():N})";
    }

    // Config/profile names must be valid file names; replace invalid chars.
    private static string SanitizeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = name.Select(c => Array.IndexOf(invalid, c) >= 0 ? '_' : c).ToArray();
        var clean = new string(chars).Trim();
        return clean.Length == 0 ? "config" : clean;
    }

    // Set the profile as target when none is set; idempotent.
    private async Task EnsureDefaultTargetAsync(string name, CancellationToken ct)
    {
        if (!string.IsNullOrEmpty(control.Target))
        {
            return;
        }

        control.SetTarget(name);
        await store.SetSettingAsync(AgentControl.SelectedTargetKey, name, ct);
        logger.LogInformation("auto-selected profile {Profile} as connection target (none was set)", name);
    }

    private async Task<IpcAck> AddProfileAsync(IReadOnlyList<string> args, CancellationToken ct)
    {
        if (args.Count < 1 || string.IsNullOrWhiteSpace(args[0]))
        {
            return new IpcAck(false, "add-profile requires a profile name");
        }

        var name = args[0];
        var config = args.Count > 1 ? args[1] : string.Empty;
        if (!string.IsNullOrEmpty(config) && !await configRepo.ExistsAsync(config, ct))
        {
            return new IpcAck(false, $"unknown config: {config}");
        }

        var existing = await store.GetProfileAsync(name, ct);
        var updated = new Profile(name, config);
        await store.SaveProfileAsync(updated, ct);
        await EnsureDefaultTargetAsync(name, ct);
        var changed = existing is null || !string.Equals(existing.Config, updated.Config, StringComparison.Ordinal);

        // The active profile's config changed: a running tunnel needs a reconnect (flag the banner), a stopped
        // one just re-reads on the next connect.
        if (changed && string.Equals(name, BoundTarget, StringComparison.Ordinal))
        {
            if (control.Running)
            {
                control.SetRestartRequired();
            }
            else
            {
                control.Invalidate();
            }
        }

        logger.LogInformation("saved profile {Name} (config '{Config}')", name, config);
        return new IpcAck(true, $"saved profile {name}");
    }

    private async Task<IpcAck> SetGeoAsync(IReadOnlyList<string> args, CancellationToken ct)
    {
        if (args.Count < 2)
        {
            return new IpcAck(false, "set-geo requires a config name and on/off");
        }

        if (!await configRepo.ExistsAsync(args[0], ct))
        {
            return new IpcAck(false, $"unknown config: {args[0]}");
        }

        var on = args[1].Equals("on", StringComparison.OrdinalIgnoreCase);
        var (rules, routes, domains) = await geo.ApplyAsync(args[0], on, args.Skip(2).ToList(), ct);
        logger.LogInformation("set-geo {Name}: split={On}, {Rules} rules -> {Routes} routes, {Domains} domains", args[0], on, rules, routes, domains);
        return new IpcAck(true, $"saved: {rules} rules, {routes} routes, {domains} domains (applies on reconnect)");
    }

    private async Task<IpcAck> SetWebSocketAsync(IReadOnlyList<string> args, CancellationToken ct)
    {
        if (args.Count < 3)
        {
            return new IpcAck(false, "set-websocket requires a config name, on/off, and a port");
        }

        if (!await configRepo.ExistsAsync(args[0], ct))
        {
            return new IpcAck(false, $"unknown config: {args[0]}");
        }

        var on = args[1].Equals("on", StringComparison.OrdinalIgnoreCase);
        if (!int.TryParse(args[2], System.Globalization.CultureInfo.InvariantCulture, out var port) || port is < 1 or > 65535)
        {
            return new IpcAck(false, "invalid websocket port (1-65535)");
        }

        // Optional 4th arg: wstunnel host; empty reuses the Endpoint host.
        var host = args.Count > 3 ? args[3].Trim() : string.Empty;

        // Optional 5th arg: tunnel MTU (default 1420, range 576-1500).
        var mtu = 1420;
        if (args.Count > 4 && args[4].Trim().Length > 0)
        {
            if (!int.TryParse(args[4].Trim(), System.Globalization.CultureInfo.InvariantCulture, out mtu) || mtu is < 576 or > 1500)
            {
                return new IpcAck(false, "invalid MTU (576-1500)");
            }
        }

        await store.SetConfigTransportAsync(new ConfigTransport(args[0], on, host, port, mtu), ct);

        // Transport applies on a fresh tunnel; flag a reconnect when the running target is affected.
        if (control.Running && await IsRunningMemberAsync(args[0], ct))
        {
            control.SetRestartRequired();
        }

        logger.LogInformation("config {Name}: transport set - websocket={On}, port={Port}, mtu={Mtu}, host={Host}",
            args[0], on, port, mtu, host.Length == 0 ? "(endpoint)" : host);
        return new IpcAck(true, on
            ? IpcMessage.Key("Agent_WebSocketEnabled", port)
            : IpcMessage.Key("Agent_WebSocketDisabled"));
    }

    private async Task<IpcAck> SetConfigDnsAsync(IReadOnlyList<string> args, CancellationToken ct)
    {
        if (args.Count < 1 || string.IsNullOrWhiteSpace(args[0]))
        {
            return new IpcAck(false, "set-config-dns requires a config name");
        }

        if (!await configRepo.ExistsAsync(args[0], ct))
        {
            return new IpcAck(false, $"unknown config: {args[0]}");
        }

        // Optional 2nd arg: preferred DNS servers; empty clears the override.
        var servers = args.Count > 1 ? args[1].Trim() : string.Empty;
        if (servers.Length == 0)
        {
            await store.RemoveConfigDnsAsync(args[0], ct);
        }
        else
        {
            await store.SetConfigDnsAsync(new ConfigDns(args[0], servers), ct);
        }

        // DNS applies on a fresh tunnel; flag a reconnect when the running target is affected.
        if (control.Running && await IsRunningMemberAsync(args[0], ct))
        {
            control.SetRestartRequired();
        }

        logger.LogInformation("set-config-dns {Name}: servers='{Servers}'", args[0], servers);
        return new IpcAck(true, servers.Length == 0
            ? IpcMessage.Key("Agent_DnsReset")
            : IpcMessage.Key("Agent_DnsSaved", servers));
    }

    private async Task<IpcAck> SetConfigExclusionsAsync(IReadOnlyList<string> args, CancellationToken ct)
    {
        if (args.Count < 1 || string.IsNullOrWhiteSpace(args[0]))
        {
            return new IpcAck(false, "set-config-exclusions requires a config name");
        }

        if (!await configRepo.ExistsAsync(args[0], ct))
        {
            return new IpcAck(false, $"unknown config: {args[0]}");
        }

        // Arg 2: bypass list (line/comma-separated); local subnets are included explicitly.
        var exclusions = args.Count > 1 ? args[1].Trim() : string.Empty;
        await store.SetConfigExclusionsAsync(new ConfigExclusions(args[0], exclusions), ct);

        // Exclusions apply on a fresh tunnel; flag a reconnect when the running target is affected.
        if (control.Running && await IsRunningMemberAsync(args[0], ct))
        {
            control.SetRestartRequired();
        }

        logger.LogInformation("set-config-exclusions {Name}: {Len} chars", args[0], exclusions.Length);
        return new IpcAck(true, IpcMessage.Key("Agent_ExclusionsSaved"));
    }

    // Default LAN bypass CIDRs (RFC1918 + connected subnets), newline-separated.
    private IpcAck ListLocalSubnets()
    {
        return new IpcAck(true, string.Join('\n', routes.DefaultExclusionEntries()));
    }

    private async Task<bool> IsRunningMemberAsync(string config, CancellationToken ct)
    {
        var bound = BoundTarget;
        if (bound is null)
        {
            return false;
        }

        if (string.Equals(bound, config, StringComparison.Ordinal))
        {
            return true;
        }

        var profile = await store.GetProfileAsync(bound, ct);
        return profile is not null && string.Equals(profile.Config, config, StringComparison.Ordinal);
    }

    private async Task<IpcAck> ListGeoAsync(CancellationToken ct)
    {
        var tokens = await geo.CategoriesAsync(ct);
        return new IpcAck(true, string.Join('\n', tokens));
    }

    // Apps + services for per-app tunneling; enumerated as SYSTEM to read restricted paths. Rows are tab-separated: kind, label, value, detail.
    private static IpcAck ListProcesses()
    {
        var lines = ProcessCatalog.List()
            .Select(e => string.Join('\t', e.Kind, e.Label, e.Value, e.Detail));
        return new IpcAck(true, string.Join('\n', lines));
    }

    private static bool IsAppRule(string rule)
    {
        var bar = rule.IndexOf('|');
        var token = bar > 0 ? rule[(bar + 1)..] : rule;
        return token.StartsWith("app:", StringComparison.OrdinalIgnoreCase);
    }

    // Rules that only take effect on a fresh tunnel: any app rule, plus the whole Direct/Block/Exclude buckets
    // (proxy geo is reconciled live by the domain tracker; these are not).
    private static bool RequiresReconnect(string rule)
    {
        if (IsAppRule(rule))
        {
            return true;
        }

        var bar = rule.IndexOf('|');
        var role = bar > 0 ? rule[..bar].ToLowerInvariant() : "proxy";
        return role is "direct" or "block" or "exclude";
    }

    private async Task<IpcAck> SaveRoutingListAsync(IReadOnlyList<string> args, CancellationToken ct)
    {
        if (args.Count < 2)
        {
            return new IpcAck(false, "save-routing-list requires id and name");
        }

        if (!long.TryParse(args[0], out var id) || id < 0)
        {
            return new IpcAck(false, "invalid routing list id");
        }

        var name = args[1].Trim();
        if (name.Length == 0)
        {
            return new IpcAck(false, "name is required");
        }

        // Proxy geo (domains/geoip) applies live; app rules and the Direct/Block buckets need a fresh tunnel.
        var previousReconnect = id > 0 && await store.GetRoutingListAsync(id, ct) is { } previous
            ? previous.Rules.Select(GeoConfigurator.FormatWithRole).Where(RequiresReconnect).ToHashSet(StringComparer.Ordinal)
            : new HashSet<string>(StringComparer.Ordinal);

        var resultId = await geo.ApplyToRoutingListAsync(id, name, args.Skip(2).ToList(), ct);

        // Flag a reconnect only when the running profile routes through this list and a connect-time rule changed.
        if (control.Running && BoundTarget is not null)
        {
            var (listId, useRouting) = await store.GetProfileRoutingAsync(BoundTarget, ct);
            var newReconnect = args.Skip(2).Where(RequiresReconnect).ToHashSet(StringComparer.Ordinal);
            if (useRouting && listId == resultId && !newReconnect.SetEquals(previousReconnect))
            {
                control.SetRestartRequired();
            }
        }

        logger.LogInformation("saved routing list {Id} '{Name}' ({Rules} rules)", resultId, name, args.Count - 2);
        return new IpcAck(true, resultId.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    private async Task<IpcAck> RemoveRoutingListAsync(IReadOnlyList<string> args, CancellationToken ct)
    {
        if (args.Count < 1 || !long.TryParse(args[0], out var id) || id <= 0)
        {
            return new IpcAck(false, "remove-routing-list requires a positive id");
        }

        await store.RemoveRoutingListAsync(id, ct);
        logger.LogInformation("removed routing list {Id}", id);
        return new IpcAck(true, $"removed routing list {id}");
    }

    private async Task<IpcAck> GetRoutingListAsync(IReadOnlyList<string> args, CancellationToken ct)
    {
        if (args.Count < 1 || !long.TryParse(args[0], out var id) || id <= 0)
        {
            return new IpcAck(false, "get-routing-list requires a positive id");
        }

        var list = await store.GetRoutingListAsync(id, ct);
        if (list is null)
        {
            return new IpcAck(false, $"unknown routing list: {id}");
        }

        var tokens = list.Rules.Select(GeoConfigurator.FormatWithRole);
        return new IpcAck(true, string.Join('\n', tokens));
    }

    private async Task<IpcAck> SetRoutingSettingsAsync(IReadOnlyList<string> args, CancellationToken ct)
    {
        if (args.Count < 1 || !long.TryParse(args[0], out var id) || id <= 0)
        {
            return new IpcAck(false, "set-routing-settings requires a positive routing list id");
        }

        if (await store.GetRoutingListAsync(id, ct) is null)
        {
            return new IpcAck(false, $"unknown routing list: {id}");
        }

        // Args after id: exclusions, all-UDP, mode, use-IPv6, use-global-proxy. All optional; all-default clears the row.
        var exclusions = args.Count > 1 ? args[1].Trim() : string.Empty;
        var udpArg = args.Count > 2 ? args[2].Trim().ToLowerInvariant() : "off";
        var allUdp = udpArg is "on" or "1" or "true" or "yes";
        var globalArg = args.Count > 5 ? args[5].Trim().ToLowerInvariant() : "off";
        var useGlobalProxy = globalArg is "on" or "1" or "true" or "yes";

        var v6Arg = args.Count > 4 ? args[4].Trim().ToLowerInvariant() : "off";
        var useIpv6 = v6Arg is "on" or "1" or "true" or "yes";

        // Mode mirrors the global-proxy flag: full routes everything minus Direct, split tunnels only Proxy.
        var mode = useGlobalProxy ? "full" : "split";

        if (exclusions.Length == 0 && !allUdp && !useIpv6 && !useGlobalProxy)
        {
            await store.RemoveRoutingSettingsAsync(id, ct);
        }
        else
        {
            await store.SetRoutingSettingsAsync(new RoutingSettings(id, exclusions, allUdp, mode, useIpv6, useGlobalProxy), ct);
        }

        // Settings apply on a fresh tunnel; flag a reconnect when the running profile routes through this list.
        if (control.Running && BoundTarget is not null)
        {
            var (listId, useRouting) = await store.GetProfileRoutingAsync(BoundTarget, ct);
            if (useRouting && listId == id)
            {
                control.SetRestartRequired();
            }
        }

        logger.LogInformation("set-routing-settings {Id}: excl={Len} chars, allUdp={Udp}, mode={Mode}, useIpv6={V6}, globalProxy={Global}", id, exclusions.Length, allUdp, mode, useIpv6, useGlobalProxy);
        return new IpcAck(true, IpcMessage.Key("Agent_RoutingSettingsSaved"));
    }

    private async Task<IpcAck> GetRoutingSettingsAsync(IReadOnlyList<string> args, CancellationToken ct)
    {
        if (args.Count < 1 || !long.TryParse(args[0], out var id) || id <= 0)
        {
            return new IpcAck(false, "get-routing-settings requires a positive id");
        }

        if (await store.GetRoutingListAsync(id, ct) is null)
        {
            return new IpcAck(false, $"unknown routing list: {id}");
        }

        var settings = await store.GetRoutingSettingsAsync(id, ct);
        var json = System.Text.Json.JsonSerializer.Serialize(new
        {
            exclusions = settings?.Exclusions ?? string.Empty,
            allUdp = settings?.AllUdp ?? false,
            mode = settings?.Mode ?? "split",
            useIpv6 = settings?.UseIpv6 ?? false,
            useGlobalProxy = settings?.UseGlobalProxy ?? false,
        });
        return new IpcAck(true, json);
    }

    private async Task<IpcAck> AssignRoutingAsync(IReadOnlyList<string> args, CancellationToken ct)
    {
        if (args.Count < 3)
        {
            return new IpcAck(false, "assign-routing requires profile, list id (or 'none'), and on/off");
        }

        var profile = args[0];
        if (await store.GetProfileAsync(profile, ct) is null)
        {
            return new IpcAck(false, $"unknown profile: {profile}");
        }

        long? listId = null;
        if (!args[1].Equals("none", StringComparison.OrdinalIgnoreCase))
        {
            if (!long.TryParse(args[1], out var id) || id <= 0)
            {
                return new IpcAck(false, "invalid routing list id");
            }

            if (await store.GetRoutingListAsync(id, ct) is null)
            {
                return new IpcAck(false, $"unknown routing list: {id}");
            }

            listId = id;
        }

        var useRouting = args[2].Equals("on", StringComparison.OrdinalIgnoreCase);
        var (currentList, currentUse) = await store.GetProfileRoutingAsync(profile, ct);
        await store.SetProfileRoutingAsync(profile, listId, useRouting, ct);
        if ((currentList != listId || currentUse != useRouting)
            && control.Running
            && string.Equals(profile, BoundTarget, StringComparison.Ordinal))
        {
            // Routing applies on a fresh tunnel; flag a restart instead of re-applying live.
            control.SetRestartRequired();
        }

        logger.LogInformation("assigned profile {Profile}: list={List} use={Use}", profile, listId, useRouting);
        return new IpcAck(true, $"assigned {profile}: list={listId?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "none"} use={(useRouting ? "on" : "off")} (applies on reconnect)");
    }

    private IpcAck SetConnection(IReadOnlyList<string> args)
    {
        if (args.Count < 1)
        {
            return new IpcAck(false, "set-connection requires connect or disconnect");
        }

        var connect = args[0].Equals("connect", StringComparison.OrdinalIgnoreCase);
        if (!connect && !args[0].Equals("disconnect", StringComparison.OrdinalIgnoreCase))
        {
            return new IpcAck(false, $"unknown connection state: {args[0]}");
        }

        control.SetRunning(connect);
        logger.LogInformation("set connection: {State}", connect ? "connect" : "disconnect");
        return new IpcAck(true, connect ? "connecting" : "disconnecting");
    }

    private async Task<IpcAck> SelectProfileAsync(IReadOnlyList<string> args, CancellationToken ct)
    {
        if (args.Count < 1 || string.IsNullOrWhiteSpace(args[0]))
        {
            return new IpcAck(false, "set-profile requires a profile name");
        }

        var name = args[0];
        if (await store.GetProfileAsync(name, ct) is null && !await configRepo.ExistsAsync(name, ct))
        {
            return new IpcAck(false, $"unknown profile: {name}");
        }

        if (string.Equals(name, control.Target, StringComparison.Ordinal))
        {
            return new IpcAck(true, $"already active: {name}");
        }

        control.SetTarget(name);
        // Persist the selection across restarts.
        await store.SetSettingAsync(AgentControl.SelectedTargetKey, name, ct);
        logger.LogInformation("selected profile {Profile}", name);

        // No auto-switch; the tunnel keeps running. Selection takes effect on the next connect.
        return new IpcAck(true, control.Running ? $"selected {name} (reconnect to apply)" : $"selected {name}");
    }

    private async Task<IpcAck> AddSourceAsync(IReadOnlyList<string> args, CancellationToken ct)
    {
        if (args.Count < 2)
        {
            return new IpcAck(false, "add-source requires a kind (geosite/geoip) and a url");
        }

        var kind = args[0].Equals("geoip", StringComparison.OrdinalIgnoreCase) ? "geoip" : "geosite";
        var url = args[1].Trim();
        if (url.Length == 0)
        {
            return new IpcAck(false, "url is required");
        }

        var existing = await store.ListGeoSourcesAsync(ct);
        var position = existing.Count == 0 ? 1 : existing.Max(s => s.Position) + 1;
        var name = $"{kind}-{position}";
        var source = new GeoSource(name, kind, url, position);
        await store.SaveGeoSourceAsync(source, ct);
        logger.LogInformation("added geo source {Name} ({Kind}) {Url}", name, kind, url);

        // Download + re-materialize off the command path; the ack returns immediately.
        EnqueueGeoRefresh([source], forceResolve: false);
        return new IpcAck(true, IpcMessage.Key("Agent_SourceAdded", name));
    }

    private async Task<IpcAck> RemoveSourceAsync(IReadOnlyList<string> args, CancellationToken ct)
    {
        if (args.Count < 1 || string.IsNullOrWhiteSpace(args[0]))
        {
            return new IpcAck(false, "remove-source requires a name");
        }

        var name = args[0];
        await store.RemoveGeoSourceAsync(name, ct);
        _updateAvailable.TryRemove(name, out _);
        _lastError.TryRemove(name, out _);
        try
        {
            var path = TunnelPaths.GeoDataFile(name);
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
        }

        await geo.RematerializeAllRoutingListsAsync(ct);
        logger.LogInformation("removed geo source {Name}", name);
        return new IpcAck(true, IpcMessage.Key("Agent_SourceRemoved", name));
    }

    private async Task<IpcAck> EditSourceAsync(IReadOnlyList<string> args, CancellationToken ct)
    {
        if (args.Count < 3)
        {
            return new IpcAck(false, "edit-source requires a name, a kind (geosite/geoip) and a url");
        }

        var name = args[0];
        var kind = args[1].Equals("geoip", StringComparison.OrdinalIgnoreCase) ? "geoip" : "geosite";
        var url = args[2].Trim();
        if (url.Length == 0)
        {
            return new IpcAck(false, "url is required");
        }

        var existing = (await store.ListGeoSourcesAsync(ct))
            .FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.Ordinal));
        if (existing is null)
        {
            return new IpcAck(false, $"unknown source: {name}");
        }

        // Keep the opaque name and position; only kind/url change.
        var source = new GeoSource(existing.Name, kind, url, existing.Position);
        await store.SaveGeoSourceAsync(source, ct);

        // On a url change the cached conditional-GET validators (keyed by the unchanged name) would make a
        // new host falsely return 304 and keep the old data; drop the cached file to force a full download.
        if (!string.Equals(existing.Url, url, StringComparison.Ordinal))
        {
            try
            {
                var path = TunnelPaths.GeoDataFile(name);
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch (IOException)
            {
            }
        }

        logger.LogInformation("edited geo source {Name} ({Kind}) {Url}", name, kind, url);

        // Download + re-materialize off the command path; the ack returns immediately.
        EnqueueGeoRefresh([source], forceResolve: true);
        return new IpcAck(true, IpcMessage.Key("Agent_SourceEdited", name));
    }

    private async Task<IpcAck> UpdateSourcesAsync(CancellationToken ct)
    {
        // User-initiated refresh forces re-resolve even for unchanged sources.
        var sources = await store.ListGeoSourcesAsync(ct);
        EnqueueGeoRefresh(sources, forceResolve: true);
        return new IpcAck(true, IpcMessage.Key("Agent_UpdateAllStarted", sources.Count));
    }

    private async Task<IpcAck> UpdateSourceAsync(IReadOnlyList<string> args, CancellationToken ct)
    {
        if (args.Count < 1 || string.IsNullOrWhiteSpace(args[0]))
        {
            return new IpcAck(false, "update-source requires a name");
        }

        var sources = await store.ListGeoSourcesAsync(ct);
        var source = sources.FirstOrDefault(s => string.Equals(s.Name, args[0], StringComparison.Ordinal));
        if (source is null)
        {
            return new IpcAck(false, $"unknown source: {args[0]}");
        }

        // User-initiated per-source update forces re-resolve.
        EnqueueGeoRefresh([source], forceResolve: true);
        return new IpcAck(true, IpcMessage.Key("Agent_UpdateSourceStarted", source.Name));
    }

    /// <summary>
    /// Check every source for a newer remote file; returns how many have an update available.
    /// </summary>
    public async Task<(int Available, int Total)> CheckAllSourcesAsync(CancellationToken ct)
    {
        var sources = await store.ListGeoSourcesAsync(ct);
        if (sources.Count == 0)
        {
            return (0, 0);
        }

        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(TimeSpan.FromSeconds(20));

        var available = 0;
        foreach (var source in sources)
        {
            if (await CheckOneAsync(source, budget.Token) == GeoUpdateChecker.Status.Available)
            {
                available++;
            }
        }

        await BroadcastIfChangedAsync(ct);
        return (available, sources.Count);
    }

    private async Task<IpcAck> CheckSourcesAsync(CancellationToken ct)
    {
        var (available, total) = await CheckAllSourcesAsync(ct);
        if (total == 0)
        {
            return new IpcAck(true, IpcMessage.Key("Agent_NoSourcesToCheck"));
        }

        return new IpcAck(true, available == 0
            ? IpcMessage.Key("Agent_CheckedNoUpdates", total)
            : IpcMessage.Key("Agent_CheckedUpdatesAvailable", total, available));
    }

    private async Task<IpcAck> CheckSourceAsync(IReadOnlyList<string> args, CancellationToken ct)
    {
        if (args.Count < 1 || string.IsNullOrWhiteSpace(args[0]))
        {
            return new IpcAck(false, "check-source requires a name");
        }

        var sources = await store.ListGeoSourcesAsync(ct);
        var source = sources.FirstOrDefault(s => string.Equals(s.Name, args[0], StringComparison.Ordinal));
        if (source is null)
        {
            return new IpcAck(false, $"unknown source: {args[0]}");
        }

        var status = await CheckOneAsync(source, ct);
        await BroadcastIfChangedAsync(ct);
        return new IpcAck(true, status switch
        {
            GeoUpdateChecker.Status.Available => IpcMessage.Key("Agent_SourceUpdateAvailable", source.Name),
            GeoUpdateChecker.Status.UpToDate => IpcMessage.Key("Agent_SourceUpToDate", source.Name),
            _ => IpcMessage.Key("Agent_SourceCheckFailed", source.Name),
        });
    }

    private async Task<GeoUpdateChecker.Status> CheckOneAsync(GeoSource source, CancellationToken ct)
    {
        GeoUpdateChecker.Status status;
        try
        {
            // Per-source ceiling; bounds the single-source path that has no outer budget.
            using var perSource = CancellationTokenSource.CreateLinkedTokenSource(ct);
            perSource.CancelAfter(TimeSpan.FromSeconds(10));
            status = await geoUpdateChecker.CheckAsync(source, perSource.Token);
        }
        catch (OperationCanceledException)
        {
            return GeoUpdateChecker.Status.Unknown;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "geo source check failed: {Name}", source.Name);
            return GeoUpdateChecker.Status.Unknown;
        }

        if (status == GeoUpdateChecker.Status.Available)
        {
            _updateAvailable[source.Name] = true;
        }
        else if (status == GeoUpdateChecker.Status.UpToDate)
        {
            _updateAvailable[source.Name] = false;
        }

        return status;
    }

    private static string ShortError(Exception ex)
    {
        var inner = ex is AggregateException agg && agg.InnerException is not null ? agg.InnerException : ex;
        var message = (inner.Message ?? string.Empty).Replace('\r', ' ').Replace('\n', ' ').Trim();
        if (message.Length == 0)
        {
            message = inner.GetType().Name;
        }

        return message.Length > 200 ? message[..200] + "…" : message;
    }

    // Queue a geo refresh, coalesced through the session coordinator. forceResolve re-validates even unchanged sources.
    private void EnqueueGeoRefresh(IReadOnlyList<GeoSource> sources, bool forceResolve)
    {
        lock (_geoSessionGate)
        {
            if (_geoRunning)
            {
                _geoQueued = true;
                _geoQueuedForce |= forceResolve;
                foreach (var source in sources)
                {
                    _geoQueuedNames.Add(source.Name);
                }

                return;
            }

            _geoRunning = true;
        }

        _ = RunGeoSessionChainAsync(sources, forceResolve);
    }

    // Run sessions one at a time; drain queued requests. The running flag and queue share one lock.
    private async Task RunGeoSessionChainAsync(IReadOnlyList<GeoSource> sources, bool forceResolve)
    {
        while (true)
        {
            try
            {
                await RunGeoSessionAsync(sources, forceResolve);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "geo refresh session failed");
            }

            HashSet<string> queuedNames;
            lock (_geoSessionGate)
            {
                if (!_geoQueued)
                {
                    _geoRunning = false;
                    return;
                }

                queuedNames = new HashSet<string>(_geoQueuedNames, StringComparer.Ordinal);
                forceResolve = _geoQueuedForce;
                _geoQueued = false;
                _geoQueuedForce = false;
                _geoQueuedNames.Clear();
            }

            // Re-read the stored sources so a source added or removed between the trigger and this run is
            // reflected; a queued name whose source is gone is simply dropped. A store failure here must not
            // escape the chain (that would leave _geoRunning stuck true and wedge all future refreshes) - fall
            // back to an empty set, which still runs a forced re-validate and loops to drain any further queue.
            try
            {
                var all = await store.ListGeoSourcesAsync(CancellationToken.None);
                sources = all.Where(source => queuedNames.Contains(source.Name)).ToList();
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "geo refresh: could not re-read sources for queued run");
                sources = [];
            }
        }
    }

    // One refresh session: download changed sources, re-materialize lists, bump the resolve epoch when forced or changed.
    private async Task RunGeoSessionAsync(IReadOnlyList<GeoSource> sources, bool forceResolve)
    {
        // Time the session for diagnostics.
        var geoSw = System.Diagnostics.Stopwatch.StartNew();
        logger.LogDebug("geo refresh session: {Count} source(s) [{Names}], forceResolve={Force}",
            sources.Count, string.Join(",", sources.Select(source => source.Name)), forceResolve);

        // Claim only sources not already in flight.
        var pending = sources.Where(source => _updating.TryAdd(source.Name, 0)).ToList();
        var changed = false;
        if (pending.Count > 0)
        {
            var pump = new CancellationTokenSource();
            var ticker = ProgressPumpAsync(pump.Token);
            try
            {
                // Download sources concurrently; slow ones don't block the others.
                await Task.WhenAll(pending.Select(async source =>
                {
                    try
                    {
                        var srcSw = System.Diagnostics.Stopwatch.StartNew();
                        var before = await store.GetGeoFileAsync(source.Name);
                        var after = await geoFileUpdater.UpdateAsync(source, new SourceProgress(_updating, source.Name));
                        // Compare by content hash, not timestamp; 304 leaves the hash equal.
                        var srcChanged = before is null || !string.Equals(before.Sha256, after.Sha256, StringComparison.Ordinal);
                        if (srcChanged)
                        {
                            changed = true;
                        }

                        logger.LogDebug("geo source {Name}: {State} in {Ms} ms",
                            source.Name, srcChanged ? "changed" : "unchanged (304/same hash)", srcSw.ElapsedMilliseconds);

                        // Downloaded: clear the update flag and prior failure.
                        _updateAvailable[source.Name] = false;
                        _lastError.TryRemove(source.Name, out _);
                        // Indeterminate while re-materializing.
                        _updating[source.Name] = -1;
                    }
                    catch (Exception ex)
                    {
                        logger.LogWarning(ex, "geo source download failed: {Name}", source.Name);
                        _lastError[source.Name] = ShortError(ex);
                        _updating.TryRemove(source.Name, out _);
                    }
                }));

                // Stop the progress pump before re-materializing.
                pump.Cancel();
                await ticker;
                await BroadcastIfChangedAsync(CancellationToken.None);

                try
                {
                    using (logger.Step("re-materialize routing lists"))
                    {
                        await geo.RematerializeAllRoutingListsAsync();
                    }
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "geo source re-materialize failed");
                }
            }
            finally
            {
                foreach (var source in pending)
                {
                    _updating.TryRemove(source.Name, out _);
                }

                pump.Cancel();
                await ticker;
                pump.Dispose();
                await BroadcastIfChangedAsync(CancellationToken.None);
            }
        }

        // Advance the resolve epoch when forced or changed; stamp the refresh time.
        if (forceResolve || changed)
        {
            await BumpResolveEpochAsync();
        }

        await store.SetSettingAsync("geo-last-refresh", DateTimeOffset.UtcNow.ToString("O", System.Globalization.CultureInfo.InvariantCulture));
        logger.LogDebug("geo refresh session done: changed={Changed}, re-resolve triggered={Bumped} [{Ms} ms]",
            changed, forceResolve || changed, geoSw.ElapsedMilliseconds);
    }

    // Internal counters (not user settings): resolve epoch and last-refresh stamp.
    private async Task BumpResolveEpochAsync()
    {
        var current = await store.GetSettingAsync("geo-resolve-epoch");
        var next = (long.TryParse(current, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var value) ? value : 0) + 1;
        await store.SetSettingAsync("geo-resolve-epoch", next.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// Refresh the geo cache when it is older than its validity window.
    /// </summary>
    public async Task RefreshStaleGeoAsync(CancellationToken ct)
    {
        var sources = await store.ListGeoSourcesAsync(ct);
        if (sources.Count == 0)
        {
            return;
        }

        // Only refresh when something consumes geo routing.
        var assigned = await store.ListAssignedRoutingListIdsAsync(ct);
        if (assigned.Count == 0 && !control.Running)
        {
            return;
        }

        var settings = await settingsStore.LoadAsync(ct);
        var validity = TimeSpan.FromHours(Math.Clamp(settings.GeoCacheValidityHours, 1, 24 * 30));
        var last = await store.GetSettingAsync("geo-last-refresh", ct);
        if (last is not null
            && DateTimeOffset.TryParse(last, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind, out var when)
            && DateTimeOffset.UtcNow - when < validity)
        {
            return;   // still within the validity window
        }

        EnqueueGeoRefresh(sources, forceResolve: true);
    }

    private async Task ProgressPumpAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await BroadcastIfChangedAsync(CancellationToken.None);
                await Task.Delay(TimeSpan.FromMilliseconds(700), ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "progress broadcast failed");
            }
        }
    }

    private sealed class SourceProgress(System.Collections.Concurrent.ConcurrentDictionary<string, int> map, string name) : IProgress<int>
    {
        public void Report(int value)
        {
            map[name] = value;
        }
    }

    private async Task<IpcAck> SetSettingAsync(IReadOnlyList<string> args, CancellationToken ct)
    {
        if (args.Count < 2)
        {
            return new IpcAck(false, "set-setting requires a key and a value");
        }

        var key = args[0];
        if (!await settingsStore.SetAsync(key, args[1], ct))
        {
            return new IpcAck(false, $"invalid setting or value; keys: {string.Join(", ", SettingsStore.Keys())}");
        }

        // Log level applies live; push to this process's switch now.
        if (key == LogLevelWatcher.SettingKey)
        {
            logLevel.Set(args[1]);
            logger.LogInformation("log level set to {Level}", logLevel.Current);
            return new IpcAck(true, $"log level = {logLevel.Current}");
        }

        // Routing log toggle applies live; flip this process's switch now.
        if (key == RouteLog.SettingKey)
        {
            RouteLog.Enabled = args[1].Trim().ToLowerInvariant() is "true" or "on" or "1" or "yes";
            logger.LogInformation("routing log {State}", RouteLog.Enabled ? "on" : "off");
            return new IpcAck(true, RouteLog.Enabled ? "routing log on" : "routing log off");
        }

        logger.LogInformation("set setting {Key} = {Value}", key, args[1]);
        return new IpcAck(true, $"set {key} = {args[1]} (applies on reconnect)");
    }

    private async Task<IpcAck> CollectDiagnosticsAsync(CancellationToken ct)
    {
        try
        {
            var path = await diagnostics.CollectAsync(ct);
            return new IpcAck(true, path);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "diagnostics collection failed");
            return new IpcAck(false, IpcMessage.Key("Agent_DiagnosticsFailed", ex.Message));
        }
    }

    // Lists the on-disk log files for the in-app viewer (OpListLogs), newest generation first. The agent
    // reads these as SYSTEM so an unprivileged UI can view logs whose files it may not open directly.
    private IpcAck ClearLog()
    {
        logFileSink.Reset();

        // Drop every other on-disk log (routes.log + its rolled generations, any dated agent logs); the live
        // ageo.log was just re-created empty by the sink, so it is kept.
        var dir = TunnelPaths.LogDirectory();
        var others = Directory.EnumerateFiles(dir, "routes.log*")
            .Concat(Directory.EnumerateFiles(dir, "ageo*.log"))
            .Where(p => !string.Equals(Path.GetFileName(p), "ageo.log", StringComparison.OrdinalIgnoreCase))
            .ToList();
        foreach (var path in others)
        {
            try
            {
                File.Delete(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Held by another process; leave it.
            }
        }

        logger.LogInformation("agent log cleared");
        return new IpcAck(true, "log cleared");
    }

    private static IpcAck ListLogs()
    {
        var files = DiagnosticsCollector.EnumerateLogFiles()
            .Select(f => (f.Type, Info: new FileInfo(f.Path)))
            .Where(f => f.Info.Exists)
            .OrderByDescending(f => f.Info.LastWriteTimeUtc)
            .Select(f => new
            {
                name = f.Info.Name,
                type = f.Type,
                size = f.Info.Length,
                modified = f.Info.LastWriteTime.ToString("o"),
            })
            .ToList();
        return new IpcAck(true, JsonSerializer.Serialize(files));
    }

    // Reads a bounded window (the live tail by default) of one enumerated log file (OpReadLog). The name is
    // validated against the enumerated set, so this never becomes an arbitrary-file-read oracle for a local
    // user on the authenticated pipe.
    private static IpcAck ReadLog(IReadOnlyList<string> args)
    {
        if (args.Count < 1 || string.IsNullOrWhiteSpace(args[0]))
        {
            return new IpcAck(false, "read-log requires a file name");
        }

        var name = args[0];
        var target = DiagnosticsCollector.EnumerateLogFiles()
            .FirstOrDefault(f => string.Equals(Path.GetFileName(f.Path), name, StringComparison.OrdinalIgnoreCase));
        if (target.Path is null)
        {
            return new IpcAck(false, $"unknown log file: {name}");
        }

        var tailBytes = args.Count > 1 && long.TryParse(args[1], out var tb) ? tb : 262144;
        tailBytes = Math.Clamp(tailBytes, 4096, 1_048_576);
        long? beforeOffset = args.Count > 2 && long.TryParse(args[2], out var bo) && bo > 0 ? bo : null;

        using var stream = new FileStream(target.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var fileSize = stream.Length;
        var end = beforeOffset is { } b && b <= fileSize ? b : fileSize;
        var start = Math.Max(0, end - tailBytes);
        var length = (int)(end - start);
        var buffer = new byte[length];
        stream.Seek(start, SeekOrigin.Begin);
        stream.ReadExactly(buffer, 0, length);

        // Work in byte space: a 0x0A never appears inside a multibyte UTF-8 sequence, so newline scanning and
        // offset math are exact regardless of where the arbitrary window boundary fell (decoding first would
        // turn a split leading char into a replacement char and desync firstOffset from the real byte layout).
        var truncated = start > 0;
        var contentStart = 0;
        var firstOffset = start;
        if (truncated)
        {
            var nl = Array.IndexOf(buffer, (byte)'\n', 0, length);
            if (nl >= 0)
            {
                // The window began mid-file: its first line is a fragment. Drop up to and including the first
                // newline and report the offset of the first whole line so a page-older read ends exactly there.
                contentStart = nl + 1;
                firstOffset = start + contentStart;
            }
            else
            {
                // No newline in the window: it is one over-long line. Drop the fragment (no fabricated whole
                // line, no mid-char split) but keep the anchor at 'start' so repeated page-older still walks
                // backward toward the line's beginning instead of re-reading the same empty window forever.
                contentStart = length;
                firstOffset = start;
            }
        }

        // At the live-tail boundary, drop any half-written trailing multibyte char so it never decodes to a
        // replacement glyph; those bytes are picked up on the next read once the character is complete.
        var contentEnd = end == fileSize ? TrimPartialTrailingUtf8(buffer, contentStart, length) : length;
        var text = contentEnd > contentStart
            ? Encoding.UTF8.GetString(buffer, contentStart, contentEnd - contentStart)
            : string.Empty;

        var split = text.Split('\n');
        // Log lines end with a newline, so the split yields a trailing empty element; drop it. A live file
        // mid-write may end without one - then the last element is the partial newest line, which we keep.
        var count = split.Length;
        if (count > 0 && split[count - 1].Length == 0)
        {
            count--;
        }

        var lines = new List<string>(count);
        for (var i = 0; i < count; i++)
        {
            lines.Add(split[i].TrimEnd('\r'));
        }

        return new IpcAck(true, JsonSerializer.Serialize(new
        {
            lines,
            firstOffset,
            fileSize,
            truncated,
        }));
    }

    // Returns an end index (exclusive) into buffer[start..end) that excludes an incomplete trailing UTF-8
    // sequence, so decoding the window never yields a replacement char from a half-written final character.
    private static int TrimPartialTrailingUtf8(byte[] buffer, int start, int end)
    {
        var i = end - 1;
        while (i >= start && (buffer[i] & 0xC0) == 0x80)
        {
            i--; // step back over continuation bytes (10xxxxxx) to the lead byte
        }

        if (i < start)
        {
            return end;
        }

        var lead = buffer[i];
        var expected =
            (lead & 0x80) == 0x00 ? 1 : // 0xxxxxxx
            (lead & 0xE0) == 0xC0 ? 2 : // 110xxxxx
            (lead & 0xF0) == 0xE0 ? 3 : // 1110xxxx
            (lead & 0xF8) == 0xF0 ? 4 : // 11110xxx
            0;                          // not a valid lead byte

        if (expected == 0)
        {
            return end;
        }

        // Keep everything when the final character is complete; otherwise drop the incomplete lead+continuations.
        return end - i >= expected ? end : i;
    }

    private async Task<IpcAck> CheckUpdateAsync(CancellationToken ct)
    {
        var settings = await settingsStore.LoadAsync(ct);
        if (string.IsNullOrWhiteSpace(settings.UpdateUrl))
        {
            return new IpcAck(false, IpcMessage.Key("Agent_UpdateUrlNotSet"));
        }

        var result = await TryCheckUpdateAsync(settings.UpdateUrl, ct);
        if (result.Faulted)
        {
            return new IpcAck(false, IpcMessage.Key("Agent_UpdateServerUnavailable"));
        }

        updateState.Latest = result.Info;
        await BroadcastIfChangedAsync(ct);

        if (result.Info is null)
        {
            return new IpcAck(false, IpcMessage.Key("Agent_UpdateCheckFailed"));
        }

        if (!result.Info.Available)
        {
            return new IpcAck(true, IpcMessage.Key("Agent_UpToDate"));
        }

        return new IpcAck(true, IpcMessage.Key("Agent_UpdateAvailable", result.Info.Version));

        async Task<(UpdateInfo? Info, bool Faulted)> TryCheckUpdateAsync(string url, CancellationToken token)
        {
            try
            {
                return (await updateChecker.CheckAsync(url, Version(), token), false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "update check failed");
                return (null, true);
            }
        }
    }

    private async Task<IpcAck> DownloadGeoAsync(CancellationToken ct)
    {
        await GeoDefaults.SeedIfEmptyAsync(store, logger, ct);

        var sources = await store.ListGeoSourcesAsync(ct);
        if (sources.Count == 0)
        {
            return new IpcAck(true, IpcMessage.Key("Agent_NoSourcesToDownload"));
        }

        // Mark every source in-flight so each status snapshot carries per-source download percent: the
        // installer's bootstrapper reads those snapshots to drive a real progress bar instead of an
        // indeterminate spinner. Download concurrently (like "Обновить все") so the aggregate climbs
        // smoothly and the whole step takes the time of the slowest source, not the sum.
        foreach (var source in sources)
        {
            _updating[source.Name] = 0;
        }

        var failed = new System.Collections.Concurrent.ConcurrentBag<string>();
        var pump = new CancellationTokenSource();
        var ticker = ProgressPumpAsync(pump.Token);
        try
        {
            await Task.WhenAll(sources.Select(async source =>
            {
                try
                {
                    await geoFileUpdater.UpdateAsync(source, new SourceProgress(_updating, source.Name), ct);
                    _updateAvailable[source.Name] = false;
                    _lastError.TryRemove(source.Name, out _);
                }
                catch (Exception ex)
                {
                    failed.Add(source.Name);
                    _lastError[source.Name] = ShortError(ex);
                    logger.LogWarning(ex, "geo download failed: {Name}", source.Name);
                }
                finally
                {
                    // Done (success or fail): pin to 100 so the BA's aggregate reaches 100% rather than
                    // stalling on a source that errored mid-download.
                    _updating[source.Name] = 100;
                }
            }));

            try
            {
                await geo.RematerializeAllRoutingListsAsync(ct);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "geo re-materialize failed");
                return new IpcAck(false, IpcMessage.Key("Agent_ListsDownloadedProcessFailed", ex.Message));
            }
        }
        finally
        {
            foreach (var source in sources)
            {
                _updating.TryRemove(source.Name, out _);
            }

            pump.Cancel();
            await ticker;
            await BroadcastIfChangedAsync(CancellationToken.None);
        }

        return failed.IsEmpty
            ? new IpcAck(true, IpcMessage.Key("Agent_ListsDownloaded", sources.Count))
            : new IpcAck(false, IpcMessage.Key("Agent_ListsDownloadedPartial", sources.Count - failed.Count, sources.Count, string.Join(", ", failed)));
    }

    private async Task<string> BuildJsonAsync(CancellationToken ct)
    {
        var snapshot = await BuildSnapshotAsync(ct);
        return JsonSerializer.Serialize(new IpcEnvelope(IpcContract.SnapshotType, snapshot), IpcJson.Options);
    }

    private async Task<StatusSnapshot> BuildSnapshotAsync(CancellationToken ct)
    {
        var states = await store.ListProfileStatesAsync(ct);

        // Derive each config's status from the bound profile's state alone.
        var boundState = BoundTarget is not null ? states.FirstOrDefault(s => s.Name == BoundTarget) : null;
        var boundConfig = boundState is null
            ? null
            : (await store.GetProfileAsync(boundState.Name, ct))?.Config ?? boundState.Name;
        var boundStatus = boundState?.Status ?? ConnectionStatus.Disconnected;

        var configs = new List<ConfigEntry>();
        foreach (var name in await configRepo.ListAsync(ct))
        {
            var configText = await configRepo.ReadTextAsync(name, ct);
            var geoSettings = await store.GetTunnelGeoAsync(name, ct);
            var transport = await store.GetConfigTransportAsync(name, ct);
            var configDns = await store.GetConfigDnsAsync(name, ct);
            var configEx = await store.GetConfigExclusionsAsync(name, ct);
            // No row -> show the runtime default LAN bypass; saving freezes it.
            var exclusions = configEx?.Exclusions ?? string.Join('\n', routes.DefaultExclusionEntries());
            var status = boundState is not null && string.Equals(name, boundConfig, StringComparison.Ordinal)
                ? ProfileDisplayStatus(boundState.Status)
                : ConnectionStatus.Idle;
            var rules = geoSettings is not null ? geoSettings.Rules.Select(GeoConfigurator.Format).ToList() : [];
            configs.Add(new ConfigEntry(name, ReadEndpoint(configText), geoSettings?.GeoSplit ?? false, status, rules, transport?.UseWebSocket ?? false, transport?.WebSocketHost ?? string.Empty, transport?.WebSocketPort ?? 443, configDns?.Servers ?? string.Empty, exclusions, transport?.Mtu ?? 0));
        }

        var profiles = new List<ProfileEntry>();
        foreach (var name in await store.ListProfileNamesAsync(ct))
        {
            var profile = await store.GetProfileAsync(name, ct);
            if (profile is null)
            {
                continue;
            }

            var state = states.FirstOrDefault(item => item.Name == name);
            var (routingListId, useRouting) = await store.GetProfileRoutingAsync(name, ct);
            profiles.Add(new ProfileEntry(
                name,
                state?.Status ?? ConnectionStatus.Disconnected,
                profile.Config,
                routingListId,
                useRouting));
        }

        var routingLists = new List<RoutingListEntry>();
        foreach (var (id, name, ruleCount, routeCount, domainCount) in await store.ListRoutingListSummariesAsync(ct))
        {
            routingLists.Add(new RoutingListEntry(id, name, ruleCount, routeCount, domainCount));
        }

        var settings = await settingsStore.LoadAsync(ct);

        var geoFiles = (await store.ListGeoFilesAsync(ct)).ToDictionary(f => f.Name, StringComparer.Ordinal);
        var sources = new List<SourceEntry>();
        foreach (var source in await store.ListGeoSourcesAsync(ct))
        {
            geoFiles.TryGetValue(source.Name, out var meta);
            var updated = meta is null
                ? null
                : meta.UpdatedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm", System.Globalization.CultureInfo.InvariantCulture);
            var updating = _updating.TryGetValue(source.Name, out var percent);
            var updateAvailable = _updateAvailable.TryGetValue(source.Name, out var avail) && avail;
            // Hide stale errors while a retry is in flight.
            var error = !updating && _lastError.TryGetValue(source.Name, out var err) ? err : null;
            sources.Add(new SourceEntry(source.Name, source.Kind, source.Url, updated, meta?.CategoryCount ?? 0, updating, updating ? percent : 0, updateAvailable, error));
        }

        var update = updateState.Latest;
        return new StatusSnapshot(Version(), BoundTarget, configs, profiles, routingLists, control.Running, boundStatus, control.RestartRequired, control.Target, sources, logBuffer.Snapshot(),
            settings.UpdateUrl,
            update?.Available ?? false,
            update?.Version ?? string.Empty,
            update?.SetupUrl ?? string.Empty,
            update?.Description ?? string.Empty,
            settings.GeoAutoCheck,
            settings.GeoCheckIntervalHours,
            settings.GeoCacheValidityHours,
            control.ConnectFailed,
            AppSettings.EngineVersion,
            settings.TunnelAllUdp,
            settings.LogLevel,
            settings.RouteLog,
            control.ConnectFailed ? control.ConnectFailReason.ToString() : string.Empty,
            control.ConnectFailed ? (control.ConnectFailDetail ?? string.Empty) : string.Empty,
            control.RetryAttempt,
            settings.SurviveReboot,
            settings.PeriodicReconnect,
            settings.PeriodicReconnectIntervalSeconds);
    }

    private static string ProfileDisplayStatus(string profileStatus)
    {
        return profileStatus switch
        {
            "connected" => ConnectionStatus.Connected,
            "connecting" => ConnectionStatus.Connecting,
            "disconnecting" => ConnectionStatus.Disconnecting,
            _ => ConnectionStatus.Idle,
        };
    }

    // Extract Endpoint from a config's wg-quick text.
    private static string ReadEndpoint(string config)
    {
        foreach (var line in config.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith("Endpoint", StringComparison.OrdinalIgnoreCase))
            {
                return trimmed[(trimmed.IndexOf('=') + 1)..].Trim();
            }
        }

        return string.Empty;
    }

    private static string Version()
    {
        return typeof(AgentStatusBroker).Assembly.GetName().Version?.ToString() ?? "0";
    }
}
