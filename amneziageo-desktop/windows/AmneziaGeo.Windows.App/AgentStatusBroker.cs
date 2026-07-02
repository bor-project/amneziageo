using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using AmneziaGeo.Decl;
using AmneziaGeo.Ipc;
using Microsoft.Extensions.Logging;

namespace AmneziaGeo.Windows.App;

/// <summary>
/// Builds status snapshots and pushes them to connected UI clients over the status pipe.
/// </summary>
internal sealed class AgentStatusBroker(ConfigRepository configRepo, IStateStore store, GeoConfigurator geo, GeoFileUpdater geoFileUpdater, GeoUpdateChecker geoUpdateChecker, AgentControl control, SettingsStore settingsStore, UpdateChecker updateChecker, UpdateState updateState, RouteManager routes, LogRingBuffer logBuffer, ILogger<AgentStatusBroker> logger)
{
    private readonly List<PipeConnection> _clients = [];
    private readonly Lock _gate = new();
    private string? _lastJson;

    // Connections that announced themselves as UI sessions (attach-ui). The tunnel's lifetime is tied to
    // their presence: when the last one drops while a tunnel is up, the tunnel is disconnected after a
    // short grace. Subset of _clients; guarded by _gate.
    private readonly HashSet<PipeConnection> _uiSessions = [];

    // Pending "UI gone" teardown, cancelled if a UI re-attaches within the grace window - this covers the
    // UI client's own auto-reconnect after a transient pipe drop, so the tunnel does not flap. Guarded by _gate.
    private CancellationTokenSource? _teardownGrace;

    // How long the tunnel survives after the last UI session drops. Must exceed the UI client's reconnect
    // delay (StatusPipeClient retries ~2s) so a brief pipe hiccup followed by a reconnect does not tear the
    // tunnel down; short enough that a real window-close/crash disconnects promptly.
    private static readonly TimeSpan _uiTeardownGrace = TimeSpan.FromSeconds(5);

    // Per-source download/apply progress while an update is in flight, keyed by source name. The value
    // is the download percent (0-100), or -1 while the routing lists re-materialize (indeterminate).
    // Presence in the map means "updating"; the snapshot overlays it onto each SourceEntry so the UI can
    // spin the refresh icon and show a percentage.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, int> _updating = new(StringComparer.Ordinal);

    // Per-source "a newer remote file exists" flag, set by the update-check and cleared when the source
    // is re-downloaded. Surfaced on each SourceEntry so the UI can badge the row.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool> _updateAvailable = new(StringComparer.Ordinal);

    // Per-source last download/parse failure (e.g. a wrong URL that returned an HTML page), set when an
    // update fails and cleared on a successful download. Surfaced on each SourceEntry so the row can tell
    // the user why a freshly added source has no categories instead of silently showing "не загружен".
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> _lastError = new(StringComparer.Ordinal);

    // Coalesced geo-refresh coordinator (#83): at most one download/re-materialize session runs at a time.
    // A trigger that arrives while a session is in flight is neither dropped nor run in parallel - it is
    // queued (its sources unioned, its force-resolve flag OR-ed) and runs once the current session finishes,
    // so a user "Обновить" during an auto-refresh takes effect without two sessions clobbering each other.
    private readonly object _geoSessionGate = new();
    private bool _geoRunning;
    private bool _geoQueued;
    private bool _geoQueuedForce;
    private readonly HashSet<string> _geoQueuedNames = new(StringComparer.Ordinal);

    /// <summary>
    /// The profile whose live status the connection card reflects: the running target while connected,
    /// otherwise the selected target. The radio uses the selected target (snapshot SelectedTarget).
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

            // Only a UI session leaving can end the tunnel - a transient command client (the CLI) never
            // does. Done after releasing the lock so the grace-teardown scheduling does not run under it.
            if (wasUi)
            {
                OnUiSessionEnded();
            }
        }
    }

    /// <summary>
    /// Registers a connection as a presence-holding UI session and cancels any pending "UI gone" teardown.
    /// The cancel covers the UI client's own auto-reconnect after a transient pipe drop: the prior
    /// connection's exit may have scheduled a teardown that this reattach must call off, so the tunnel
    /// stays up across the blip.
    /// </summary>
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

    /// <summary>
    /// Called when a UI session's connection drops. If it was the last UI and a tunnel is up, schedules a
    /// disconnect after a short grace (cancelled if a UI re-attaches in that window). This is what ties the
    /// VPN's lifetime to the app: closing the window or a UI crash brings the tunnel down, while the
    /// privileged agent service keeps running idle.
    /// </summary>
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
                return; // a UI re-attached within the grace window
            }

            lock (_gate)
            {
                if (_uiSessions.Count > 0)
                {
                    return;
                }
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

        // Attach is handled here (not in ExecuteCommandAsync) because it needs the connection identity to
        // register it as a presence-holding UI session.
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
                IpcContract.OpAddBalancer => await AddBalancerAsync(command.Args, ct),
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
                IpcContract.OpUpdateSources => await UpdateSourcesAsync(ct),
                IpcContract.OpUpdateSource => await UpdateSourceAsync(command.Args, ct),
                IpcContract.OpCheckSources => await CheckSourcesAsync(ct),
                IpcContract.OpCheckSource => await CheckSourceAsync(command.Args, ct),
                IpcContract.OpGetConfig => await GetConfigAsync(command.Args, ct),
                IpcContract.OpImportConfig => await ImportConfigAsync(command.Args, ct),
                IpcContract.OpEditConfig => await EditConfigAsync(command.Args, ct),
                IpcContract.OpRemoveConfig => await RemoveConfigAsync(command.Args, ct),
                IpcContract.OpRemoveBalancer => await RemoveBalancerAsync(command.Args, ct),
                IpcContract.OpRenameConfig => await RenameConfigAsync(command.Args, ct),
                IpcContract.OpCopyConfig => await CopyConfigAsync(command.Args, ct),
                IpcContract.OpRenameProfile => await RenameProfileAsync(command.Args, ct),
                IpcContract.OpExportBundle => await ExportBundleAsync(command.Args, ct),
                IpcContract.OpImportBundle => await ImportBundleAsync(command.Args, ct),
                IpcContract.OpCheckUpdate => await CheckUpdateAsync(ct),
                IpcContract.OpDownloadGeo => await DownloadGeoAsync(ct),
                _ => new IpcAck(false, $"unknown command: {command.Op}"),
            };
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "command {Op} failed", command.Op);
            return new IpcAck(false, ex.Message);
        }
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
        return new IpcAck(true, $"импортирован {args[0]}");
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

        // Overwrites the stored text in place; membership/geo/routing are untouched. A thrown validation
        // error is turned into a failed ack by ExecuteCommandAsync's catch. A running member uses the new
        // text only after the next reconnect.
        await configRepo.EditFromTextAsync(args[0], args[1], ct);
        logger.LogInformation("edited config {Name}", args[0]);
        return new IpcAck(true, $"сохранён {args[0]}");
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

        // Refuse to pull a config out from under the running tunnel: if it is a member of the bound
        // target while connected, deleting it would change the live member set (and could orphan the
        // active member). The caller disconnects, or removes it from the profile, first.
        var bound = BoundTarget;
        if (control.Running && bound is not null)
        {
            var boundBalancer = await store.GetBalancerAsync(bound, ct);
            if (boundBalancer is not null && string.Equals(boundBalancer.Config, name, StringComparison.Ordinal))
            {
                return new IpcAck(false, $"config {name} is in use by the running profile {bound}; disconnect first");
            }
        }

        await configRepo.RemoveAsync(name, ct);
        await ClearBindingIfTargetAsync(name, ct);
        logger.LogInformation("removed config {Name}", name);
        return new IpcAck(true, $"removed config {name}");
    }

    private async Task<IpcAck> RemoveBalancerAsync(IReadOnlyList<string> args, CancellationToken ct)
    {
        if (args.Count < 1 || string.IsNullOrWhiteSpace(args[0]))
        {
            return new IpcAck(false, "remove-balancer requires a name");
        }

        var name = args[0];
        if (await store.GetBalancerAsync(name, ct) is null)
        {
            return new IpcAck(false, $"unknown profile: {name}");
        }

        // Refuse to delete the profile the tunnel is currently running on; disconnect first.
        if (control.Running && string.Equals(name, BoundTarget, StringComparison.Ordinal))
        {
            return new IpcAck(false, $"profile {name} is running; disconnect first");
        }

        await store.RemoveBalancerAsync(name, ct);
        await ClearBindingIfTargetAsync(name, ct);
        logger.LogInformation("removed profile {Name}", name);
        return new IpcAck(true, $"removed profile {name}");
    }

    // Drops the persisted target binding when the just-removed profile/config was the selected target, so
    // a deleted referent does not leave a dangling selection the connection card would keep showing.
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

        if (await configRepo.ExistsAsync(destination, ct) || await store.GetBalancerAsync(destination, ct) is not null)
        {
            return new IpcAck(false, $"имя {destination} уже занято");
        }

        await configRepo.CopyAsync(source, destination, ct);
        logger.LogInformation("copied config {Source} -> {Dest}", source, destination);
        return new IpcAck(true, $"конфигурация скопирована: {destination}");
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
            return new IpcAck(true, "имя не изменилось");
        }

        if (!await configRepo.ExistsAsync(oldName, ct))
        {
            return new IpcAck(false, $"unknown config: {oldName}");
        }

        if (await configRepo.ExistsAsync(newName, ct) || await store.GetBalancerAsync(newName, ct) is not null)
        {
            return new IpcAck(false, $"имя {newName} уже занято");
        }

        // Refuse while the config is live under the running tunnel: moving the .conf out from under the
        // active member would break it. Disconnect first.
        if (control.Running && await IsRunningMemberAsync(oldName, ct))
        {
            return new IpcAck(false, $"config {oldName} is in use by the running tunnel; disconnect first");
        }

        await configRepo.RenameAsync(oldName, newName, ct);

        // A single-config target named after the config follows the rename so the selection keeps working.
        if (string.Equals(oldName, control.Target, StringComparison.Ordinal))
        {
            control.SetTarget(newName);
            await store.SetSettingAsync(AgentControl.SelectedTargetKey, newName, ct);
        }

        logger.LogInformation("renamed config {Old} -> {New}", oldName, newName);
        return new IpcAck(true, $"переименован в {newName}");
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
            return new IpcAck(true, "имя не изменилось");
        }

        var balancer = await store.GetBalancerAsync(oldName, ct);
        if (balancer is null)
        {
            return new IpcAck(false, $"unknown profile: {oldName}");
        }

        if (await store.GetBalancerAsync(newName, ct) is not null || await configRepo.ExistsAsync(newName, ct))
        {
            return new IpcAck(false, $"имя {newName} уже занято");
        }

        // Refuse to rename the profile the tunnel is running on; disconnect first.
        if (control.Running && string.Equals(oldName, BoundTarget, StringComparison.Ordinal))
        {
            return new IpcAck(false, $"profile {oldName} is running; disconnect first");
        }

        // Move the balancer row, then carry the routing assignment (keyed by profile name) and the
        // selection/binding across to the new name.
        await store.SaveBalancerAsync(balancer with { Name = newName }, ct);
        await store.RemoveBalancerAsync(oldName, ct);

        var (listId, useRouting) = await store.GetProfileRoutingAsync(oldName, ct);
        if (listId is not null || useRouting)
        {
            await store.SetProfileRoutingAsync(newName, listId, useRouting, ct);
            await store.SetProfileRoutingAsync(oldName, null, false, ct);
        }

        if (string.Equals(oldName, control.Target, StringComparison.Ordinal))
        {
            control.SetTarget(newName);
            await store.SetSettingAsync(AgentControl.SelectedTargetKey, newName, ct);
        }

        logger.LogInformation("renamed profile {Old} -> {New}", oldName, newName);
        return new IpcAck(true, $"переименован в {newName}");
    }

    // The selective export selection, parsed from OpExportBundle's arg0 JSON. All three arrays are optional.
    private sealed record SelectionRequest(string[]? Profiles, string[]? Configs, string[]? RoutingLists);

    // Exports a SELECTIVE bundle (#91): the caller picks which configs, routing lists, and profiles to
    // include, by name, via a tree of checkboxes - any combination, not just a single profile.
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
            return new IpcAck(false, "не удалось разобрать выбор для экспорта");
        }

        // Resolve the EFFECTIVE set of config/routing-list names: the explicitly picked entries, plus
        // (for every selected profile) the config and routing list it binds - so picking a profile travels
        // with what it needs to reconnect, without the caller having to also tick its dependencies by hand.
        var configNames = new HashSet<string>(selection.Configs ?? [], StringComparer.Ordinal);
        var routingNames = new HashSet<string>(selection.RoutingLists ?? [], StringComparer.Ordinal);
        var profileNames = new HashSet<string>(selection.Profiles ?? [], StringComparer.Ordinal);

        foreach (var profileName in profileNames)
        {
            var balancer = await store.GetBalancerAsync(profileName, ct);
            if (balancer is null)
            {
                continue;
            }

            if (!string.IsNullOrEmpty(balancer.Config))
            {
                configNames.Add(balancer.Config);
            }

            var (listId, _) = await store.GetProfileRoutingAsync(profileName, ct);
            if (listId is not null && await store.GetRoutingListAsync(listId.Value, ct) is { } boundList)
            {
                routingNames.Add(boundList.Name);
            }
        }

        if (configNames.Count == 0 && routingNames.Count == 0 && profileNames.Count == 0)
        {
            return new IpcAck(false, "Не выбрано ничего для экспорта");
        }

        var configBlocks = new List<PortableBundle.ConfigBlock>();
        foreach (var name in configNames)
        {
            // Skip silently if a named config vanished between selection and export.
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

                var rules = list.Rules.Select(GeoConfigurator.Format).ToList();
                PortableBundle.RoutingSettingsBlock? settingsBlock = null;
                var settings = await store.GetRoutingSettingsAsync(list.Id, ct);
                if (settings is not null)
                {
                    settingsBlock = new PortableBundle.RoutingSettingsBlock(settings.LocalDns, settings.Exclusions, settings.AllUdp);
                }

                routingBlocks.Add(new PortableBundle.RoutingBlock(name, rules, settingsBlock));
            }
        }

        var profileBlocks = new List<PortableBundle.ProfileBlock>();
        foreach (var name in profileNames)
        {
            var balancer = await store.GetBalancerAsync(name, ct);
            if (balancer is null)
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
                string.IsNullOrEmpty(balancer.Config) ? null : balancer.Config,
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

    // Imports a SELECTIVE bundle (#91), recreating every carried config, routing list, and profile as new,
    // independent entities under fresh (de-duplicated) names using the FreeName/SanitizeFileName policy
    // below. All-or-nothing per call: a single malformed config's text throws and aborts the whole import
    // (the outer command dispatch's catch turns it into a failed ack); already-written rows from the same
    // call are not rolled back.
    private async Task<IpcAck> ImportBundleAsync(IReadOnlyList<string> args, CancellationToken ct)
    {
        if (args.Count < 1 || string.IsNullOrWhiteSpace(args[0]))
        {
            return new IpcAck(false, "import-bundle requires the bundle json");
        }

        PortableBundle.Bundle? bundle;
        try
        {
            bundle = PortableBundle.Deserialize(args[0]);
        }
        catch (JsonException ex)
        {
            return new IpcAck(false, $"не удалось разобрать файл: {ex.Message}");
        }

        if (bundle is null || !string.Equals(bundle.Format, PortableBundle.FormatTag, StringComparison.Ordinal))
        {
            return new IpcAck(false, "это не файл AmneziaGeo");
        }

        if (bundle.Version > PortableBundle.CurrentVersion)
        {
            return new IpcAck(false, $"файл новее (v{bundle.Version}); обновите приложение");
        }

        // Config and profile names live in one global namespace (rename refuses a name used by either);
        // routing-list names are a separate space.
        var taken = new HashSet<string>(StringComparer.Ordinal);
        foreach (var existing in await configRepo.ListAsync(ct))
        {
            taken.Add(existing);
        }

        foreach (var existing in await store.ListBalancerNamesAsync(ct))
        {
            taken.Add(existing);
        }

        var listNames = new HashSet<string>(
            (await store.ListRoutingListsAsync(ct)).Select(l => l.Name),
            StringComparer.Ordinal);

        var configNameMap = new Dictionary<string, string>(StringComparer.Ordinal);
        var routingMap = new Dictionary<string, (string Name, long Id)>(StringComparer.Ordinal);
        var renames = new List<string>();

        foreach (var block in bundle.Configs)
        {
            var finalName = FreeName(SanitizeFileName(block.Name), taken);
            taken.Add(finalName);
            if (!string.Equals(finalName, block.Name, StringComparison.Ordinal))
            {
                renames.Add($"«{block.Name}» → «{finalName}»");
            }

            // Validates [Interface]/[Peer]; a malformed bundle throws here and aborts the import.
            await configRepo.AddFromTextAsync(finalName, block.ConfigText, ct);

            if (block.Transport is { } tr)
            {
                await store.SetConfigTransportAsync(new ConfigTransport(finalName, tr.UseWebSocket, tr.Host, tr.Port, tr.Mtu), ct);
            }

            if (block.Geo is { } g)
            {
                // Re-materializes the rule tokens against this machine's geo data (empty until downloaded).
                await geo.ApplyAsync(finalName, g.Split, g.Rules, ct);
            }

            configNameMap[block.Name] = finalName;
        }

        foreach (var block in bundle.RoutingLists)
        {
            var finalName = FreeName(block.Name, listNames);
            listNames.Add(finalName);
            if (!string.Equals(finalName, block.Name, StringComparison.Ordinal))
            {
                renames.Add($"«{block.Name}» → «{finalName}»");
            }

            var newId = await geo.ApplyToRoutingListAsync(0, finalName, block.Rules, ct);
            if (block.Settings is { } s)
            {
                await store.SetRoutingSettingsAsync(new RoutingSettings(newId, s.LocalDns, s.Exclusions, s.AllUdp, "split"), ct);
            }

            routingMap[block.Name] = (finalName, newId);
        }

        foreach (var block in bundle.Profiles)
        {
            var finalName = FreeName(block.Name, taken);
            taken.Add(finalName);
            if (!string.Equals(finalName, block.Name, StringComparison.Ordinal))
            {
                renames.Add($"«{block.Name}» → «{finalName}»");
            }

            var config = block.Config is not null && configNameMap.TryGetValue(block.Config, out var cn) ? cn : string.Empty;
            await store.SaveBalancerAsync(new BalancerGroup(finalName, config), ct);

            // No EnsureDefaultTargetAsync here: a bulk import must not silently steal the connection target.
            if (block.RoutingList is not null && routingMap.TryGetValue(block.RoutingList, out var rl))
            {
                await store.SetProfileRoutingAsync(finalName, rl.Id, block.UseRouting, ct);
            }
        }

        var summary = $"Импортировано: конфигураций {bundle.Configs.Count}, списков маршрутизации {bundle.RoutingLists.Count}, профилей {bundle.Profiles.Count}.";
        if (renames.Count > 0)
        {
            summary += renames.Count <= 5
                ? "\nПереименовано при импорте: " + string.Join(", ", renames) + "."
                : "\nПереименовано при импорте: несколько имён совпали с уже существующими.";
        }

        logger.LogInformation(
            "imported bundle: {Configs} configs, {Routing} routing lists, {Profiles} profiles",
            bundle.Configs.Count,
            bundle.RoutingLists.Count,
            bundle.Profiles.Count);
        return new IpcAck(true, summary);
    }

    // True when any profile still binds the given config, so removing the config would unbind that profile too.
    private async Task<bool> ConfigInUseAsync(string config, CancellationToken ct)
    {
        foreach (var profileName in await store.ListBalancerNamesAsync(ct))
        {
            var profile = await store.GetBalancerAsync(profileName, ct);
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

    // A config is stored as <name>.conf, so its name (and a profile's, since they share a namespace) must be
    // a valid file name; replace anything that is not.
    private static string SanitizeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = name.Select(c => Array.IndexOf(invalid, c) >= 0 ? '_' : c).ToArray();
        var clean = new string(chars).Trim();
        return clean.Length == 0 ? "config" : clean;
    }

    // Makes the given profile the connection target when none is selected yet, so a fresh user who just
    // added (or imported) a profile can connect it straight away. The header connect button drives the
    // bound target, which would otherwise stay empty and make a connect a silent no-op. Idempotent: once
    // any target is set this does nothing, so adding further profiles never steals the selection.
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

    private async Task<IpcAck> AddBalancerAsync(IReadOnlyList<string> args, CancellationToken ct)
    {
        if (args.Count < 1 || string.IsNullOrWhiteSpace(args[0]))
        {
            return new IpcAck(false, "add-balancer requires a profile name");
        }

        var name = args[0];
        var config = args.Count > 1 ? args[1] : string.Empty;
        if (!string.IsNullOrEmpty(config) && !await configRepo.ExistsAsync(config, ct))
        {
            return new IpcAck(false, $"unknown config: {config}");
        }

        var existing = await store.GetBalancerAsync(name, ct);
        var updated = new BalancerGroup(name, config);
        await store.SaveBalancerAsync(updated, ct);
        await EnsureDefaultTargetAsync(name, ct);
        var changed = existing is null || !string.Equals(existing.Config, updated.Config, StringComparison.Ordinal);

        // Only disrupt the live session when the *active* profile changed; creating or editing other
        // profiles (e.g. "+ Профиль") must not reconnect the running tunnel.
        if (changed && string.Equals(name, BoundTarget, StringComparison.Ordinal))
        {
            control.Invalidate();
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

        // Optional 4th arg: the wstunnel host. Empty reuses the config's own Endpoint host.
        var host = args.Count > 3 ? args[3].Trim() : string.Empty;

        // Optional 5th arg: the tunnel MTU (default 1420). Valid range: 576-1500.
        var mtu = 1420;
        if (args.Count > 4 && args[4].Trim().Length > 0)
        {
            if (!int.TryParse(args[4].Trim(), System.Globalization.CultureInfo.InvariantCulture, out mtu) || mtu is < 576 or > 1500)
            {
                return new IpcAck(false, "invalid MTU (576-1500)");
            }
        }

        await store.SetConfigTransportAsync(new ConfigTransport(args[0], on, host, port, mtu), ct);

        // Transport rewrites the dial path (UDP -> loopback wstunnel); like a routing change it only
        // applies cleanly on a fresh tunnel. If the changed config is in the running target, flag a
        // reconnect and let the UI prompt - the new setting is persisted and takes effect next connect.
        if (control.Running && await IsRunningMemberAsync(args[0], ct))
        {
            control.SetRestartRequired();
        }

        logger.LogInformation("set-websocket {Name}: on={On}, port={Port}", args[0], on, port);
        return new IpcAck(true, on
            ? $"WebSocket включён, порт {port} (применится при переподключении)"
            : "WebSocket выключен (применится при переподключении)");
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

        // Optional 2nd arg: the preferred DNS servers (comma/space-separated). Empty clears the override,
        // reverting non-tunneled resolution to the auto-detected system resolvers.
        var servers = args.Count > 1 ? args[1].Trim() : string.Empty;
        if (servers.Length == 0)
        {
            await store.RemoveConfigDnsAsync(args[0], ct);
        }
        else
        {
            await store.SetConfigDnsAsync(new ConfigDns(args[0], servers), ct);
        }

        // DNS feeds the per-tunnel resolver wiring, decided at connect time; like a routing/transport change
        // it applies cleanly only on a fresh tunnel. If the changed config is in the running target, flag a
        // reconnect - the new setting is persisted and takes effect on the next connect.
        if (control.Running && await IsRunningMemberAsync(args[0], ct))
        {
            control.SetRestartRequired();
        }

        logger.LogInformation("set-config-dns {Name}: servers='{Servers}'", args[0], servers);
        return new IpcAck(true, servers.Length == 0
            ? "DNS сброшен на автоопределение (применится при переподключении)"
            : $"DNS сохранён: {servers} (применится при переподключении)");
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

        // Arg 2: the bypass list (one entry per line / comma-separated). Detected local subnets are now part
        // of this list (added explicitly from the UI), so there is no separate auto-exclude flag.
        var exclusions = args.Count > 1 ? args[1].Trim() : string.Empty;
        await store.SetConfigExclusionsAsync(new ConfigExclusions(args[0], exclusions), ct);

        // Exclusions reshape AllowedIPs / split-DNS, decided at connect time; like a routing/DNS change they
        // apply cleanly only on a fresh tunnel. If the changed config is in the running target, flag a reconnect.
        if (control.Running && await IsRunningMemberAsync(args[0], ct))
        {
            control.SetRestartRequired();
        }

        logger.LogInformation("set-config-exclusions {Name}: {Len} chars", args[0], exclusions.Length);
        return new IpcAck(true, "Исключения сохранены (применятся при переподключении)");
    }

    // Returns the default LAN bypass set (RFC1918 ranges + connected subnets outside them) as newline-
    // separated CIDRs, so the "add local networks" button installs the full set - including what used to be
    // the hidden floor - into a profile's exclusions list.
    private IpcAck ListLocalSubnets()
    {
        return new IpcAck(true, string.Join('\n', routes.DefaultExclusionEntries()));
    }

    /// <summary>
    /// Returns whether a config is the running single-config target or a member of the running balancer.
    /// </summary>
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

        var balancer = await store.GetBalancerAsync(bound, ct);
        return balancer is not null && string.Equals(balancer.Config, config, StringComparison.Ordinal);
    }

    private async Task<IpcAck> ListGeoAsync(CancellationToken ct)
    {
        var tokens = await geo.CategoriesAsync(ct);
        return new IpcAck(true, string.Join('\n', tokens));
    }

    // Running applications + services for the per-app tunneling picker (#68). Enumerated in the agent
    // (SYSTEM) so image paths and service hosting PIDs the user-mode UI cannot read are available. Each row
    // is tab-separated: kind, label, value, detail (see IpcContract.OpListProcesses).
    private static IpcAck ListProcesses()
    {
        var lines = ProcessCatalog.List()
            .Select(e => string.Join('\t', e.Kind, e.Label, e.Value, e.Detail));
        return new IpcAck(true, string.Join('\n', lines));
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

        var resultId = await geo.ApplyToRoutingListAsync(id, name, args.Skip(2).ToList(), ct);

        // If the running profile routes through this list, its rules just changed under a live tunnel.
        // Routing changes only apply cleanly on a fresh tunnel (same rationale as assign-routing), so flag
        // a reconnect and let the UI show the "reconnect to apply" banner.
        if (control.Running && BoundTarget is not null)
        {
            var (listId, useRouting) = await store.GetProfileRoutingAsync(BoundTarget, ct);
            if (useRouting && listId == resultId)
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

        var tokens = list.Rules.Select(GeoConfigurator.Format);
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

        // Args after the id: local DNS, exclusions, all-UDP (on/off), mode (split/full). All optional; an
        // all-default tuple clears the row so "no row = defaults" holds (split mode, runtime-default
        // exclusions, system resolvers, no all-UDP).
        var localDns = args.Count > 1 ? args[1].Trim() : string.Empty;
        var exclusions = args.Count > 2 ? args[2].Trim() : string.Empty;
        var udpArg = args.Count > 3 ? args[3].Trim().ToLowerInvariant() : "off";
        var allUdp = udpArg is "on" or "1" or "true" or "yes";
        var mode = args.Count > 4 ? args[4].Trim().ToLowerInvariant() : "split";
        if (mode != "full")
        {
            mode = "split";
        }

        if (localDns.Length == 0 && exclusions.Length == 0 && !allUdp && mode == "split")
        {
            await store.RemoveRoutingSettingsAsync(id, ct);
        }
        else
        {
            await store.SetRoutingSettingsAsync(new RoutingSettings(id, localDns, exclusions, allUdp, mode), ct);
        }

        // These settings reshape AllowedIPs / split-DNS / UDP routing, decided at connect time. If the
        // running profile routes through this list, flag a reconnect so the UI prompts; the settings are
        // persisted and take effect on the next connect. (The agent does not consume them yet — wired in #89.)
        if (control.Running && BoundTarget is not null)
        {
            var (listId, useRouting) = await store.GetProfileRoutingAsync(BoundTarget, ct);
            if (useRouting && listId == id)
            {
                control.SetRestartRequired();
            }
        }

        logger.LogInformation("set-routing-settings {Id}: dns='{Dns}', excl={Len} chars, allUdp={Udp}, mode={Mode}", id, localDns, exclusions.Length, allUdp, mode);
        return new IpcAck(true, "Настройки маршрутизации сохранены (применятся при переподключении)");
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
            localDns = settings?.LocalDns ?? string.Empty,
            exclusions = settings?.Exclusions ?? string.Empty,
            allUdp = settings?.AllUdp ?? false,
            mode = settings?.Mode ?? "split",
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
        var balancer = await store.GetBalancerAsync(profile, ct);
        if (balancer is null)
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
            // Routing changes alter AllowedIPs/DNS and only apply cleanly on a fresh tunnel. Applying
            // them in place left a half-applied split/full state, so we do NOT re-apply live; we flag a
            // restart and let the UI prompt the user. The new setting is persisted and takes effect on
            // the next connect (when the balancer re-projects routing).
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
        if (await store.GetBalancerAsync(name, ct) is null && !await configRepo.ExistsAsync(name, ct))
        {
            return new IpcAck(false, $"unknown profile: {name}");
        }

        if (string.Equals(name, control.Target, StringComparison.Ordinal))
        {
            return new IpcAck(true, $"already active: {name}");
        }

        control.SetTarget(name);
        // Persist the selection so it survives an agent/host restart (the launch argument no longer
        // carries a default target).
        await store.SetSettingAsync(AgentControl.SelectedTargetKey, name, ct);
        logger.LogInformation("selected profile {Profile}", name);

        // No auto-switch: a connected tunnel keeps running its current target; the UI shows a
        // "reconnect to apply" notice (selected != running). The selection takes effect on the next
        // connect. The snapshot rebroadcast carries the new SelectedTarget so the radio updates.
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

        // Download + re-materialize off the command path so the ack returns immediately: a large file
        // over a slow link must not block the pipe or overrun the client's command timeout. The result
        // (categories / updated time) lands in the next status snapshot. Not "forced" - a brand-new source
        // always downloads (its file changes), which advances the resolve epoch on its own.
        EnqueueGeoRefresh([source], forceResolve: false);
        return new IpcAck(true, $"добавлен {name}, загрузка…");
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
        return new IpcAck(true, $"удалён {name}");
    }

    private async Task<IpcAck> UpdateSourcesAsync(CancellationToken ct)
    {
        // A user-initiated "Обновить все" is a full validation: re-resolve even sources the server reports
        // unchanged (their domains' IPs may still have moved), so force the resolve.
        var sources = await store.ListGeoSourcesAsync(ct);
        EnqueueGeoRefresh(sources, forceResolve: true);
        return new IpcAck(true, $"обновление запущено ({sources.Count})");
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

        // A user-initiated per-source update is also a full validation - force the resolve.
        EnqueueGeoRefresh([source], forceResolve: true);
        return new IpcAck(true, $"обновление {source.Name} запущено");
    }

    /// <summary>
    /// Checks every source for a newer remote file without downloading it, records the result per source
    /// for the snapshot, broadcasts, and returns how many of the sources have an update available. The
    /// whole sweep is time-bounded so a slow source cannot overrun the caller's timeout. Public so the
    /// periodic <see cref="GeoUpdateCheckService"/> can trigger the same sweep.
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

    /// <summary>
    /// IPC handler for a manual "check all sources" - runs the sweep and returns a human-readable summary.
    /// </summary>
    private async Task<IpcAck> CheckSourcesAsync(CancellationToken ct)
    {
        var (available, total) = await CheckAllSourcesAsync(ct);
        if (total == 0)
        {
            return new IpcAck(true, "Нет источников для проверки.");
        }

        return new IpcAck(true, available == 0
            ? $"Проверено источников: {total}. Обновлений нет."
            : $"Проверено источников: {total}. Доступно обновлений: {available}.");
    }

    /// <summary>
    /// Checks a single source for a newer remote file without downloading it.
    /// </summary>
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
            GeoUpdateChecker.Status.Available => $"Доступно обновление: {source.Name}.",
            GeoUpdateChecker.Status.UpToDate => $"{source.Name}: актуально.",
            _ => $"{source.Name}: не удалось проверить.",
        });
    }

    /// <summary>
    /// Runs the update-check for one source and records the result. A definite answer flips the
    /// per-source flag; an Unknown (network error / nothing to compare) leaves the prior state intact.
    /// </summary>
    private async Task<GeoUpdateChecker.Status> CheckOneAsync(GeoSource source, CancellationToken ct)
    {
        GeoUpdateChecker.Status status;
        try
        {
            // A per-source ceiling so one stalled connect can't ride HttpClient's default timeout past the
            // client's command timeout; it also bounds the single-source (check-source) path, which has no
            // outer sweep budget.
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

    /// <summary>
    /// A short, single-line message for a source's download/parse failure, suitable for the row. Our own
    /// validation failures (InvalidDataException) already carry a user-facing Russian message; for other
    /// errors (network, etc.) the exception text is used, capped so a long message can't blow up the row.
    /// </summary>
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

    /// <summary>
    /// Re-downloads the given sources and re-materializes the routing lists on a background task, then
    /// pushes a fresh snapshot. Kept off the IPC command path so a slow download never blocks the pipe
    /// or overruns the client's command timeout. A lightweight ticker broadcasts in-flight progress so
    /// the UI can spin the refresh icon and show a live percentage.
    /// </summary>
    // Queues a geo refresh for the given sources, coalesced through the single-session coordinator. When
    // <paramref name="forceResolve"/> is set the session re-validates even if nothing downloaded (a user
    // "Обновить" or the TTL-driven background refresh: the IPs behind unchanged domains still drift). Returns
    // immediately - the work runs off the command path so the ack is not blocked by a multi-megabyte download.
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

    // Runs geo sessions one at a time, draining any request queued while a session was in flight. The
    // _geoRunning flag flips under the same lock that queues work, so a trigger racing the end of a session
    // is never lost: it is either queued (running still true) or starts a fresh chain (running false).
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

    // One geo refresh session: download the changed sources, re-materialize the routing lists, then stamp the
    // refresh time and - when forced or a file actually changed - bump the resolve epoch so any live tunnel
    // re-resolves its domains (#83). Safe to call with an empty source set (a forced re-validate with no
    // pending download still advances the epoch).
    private async Task RunGeoSessionAsync(IReadOnlyList<GeoSource> sources, bool forceResolve)
    {
        // Claim only the sources not already in flight (TryAdd is atomic), so an overlapping run does not
        // start a duplicate download / re-materialize of the same source.
        var pending = sources.Where(source => _updating.TryAdd(source.Name, 0)).ToList();
        var changed = false;
        if (pending.Count > 0)
        {
            var pump = new CancellationTokenSource();
            var ticker = ProgressPumpAsync(pump.Token);
            try
            {
                // Download all sources concurrently: a slow or unreachable source (e.g. github.com on a
                // censored network) must not hold up the others, so "Обновить все" finishes in the time of
                // the slowest source, not the sum. Per-source state lives in concurrent maps and each
                // writes a distinct file, so the only contention is the brief metadata write (busy-retried).
                await Task.WhenAll(pending.Select(async source =>
                {
                    try
                    {
                        var before = await store.GetGeoFileAsync(source.Name);
                        var after = await geoFileUpdater.UpdateAsync(source, new SourceProgress(_updating, source.Name));
                        // Content-hash comparison, not a timestamp: a conditional GET that returns 304 hands
                        // back the same metadata, so an unchanged file leaves the hash equal and does not force
                        // a re-resolve. Written only to true from multiple tasks, so the race is benign.
                        if (before is null || !string.Equals(before.Sha256, after.Sha256, StringComparison.Ordinal))
                        {
                            changed = true;
                        }

                        // Downloaded: the local file is now current, so any pending update flag is stale and
                        // any prior failure is resolved.
                        _updateAvailable[source.Name] = false;
                        _lastError.TryRemove(source.Name, out _);
                        // Switch to indeterminate while the re-materialize runs below.
                        _updating[source.Name] = -1;
                    }
                    catch (Exception ex)
                    {
                        logger.LogWarning(ex, "geo source download failed: {Name}", source.Name);
                        _lastError[source.Name] = ShortError(ex);
                        _updating.TryRemove(source.Name, out _);
                    }
                }));

                // Stop the percentage broadcaster before re-materializing: there is no percent to show
                // while applying, and the spinner keeps running client-side from the broadcast "applying"
                // state - so the ticker's reads need not contend with the re-materialize writes.
                pump.Cancel();
                await ticker;
                await BroadcastIfChangedAsync(CancellationToken.None);

                try
                {
                    await geo.RematerializeAllRoutingListsAsync();
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

        // A forced (user / TTL) refresh re-validates even when nothing downloaded; a changed download likewise
        // needs the running tunnel to re-resolve. Either way, advance the resolve epoch so any live
        // DomainTracker re-resolves its domains on its next poll. Then stamp the refresh time for the TTL gate.
        if (forceResolve || changed)
        {
            await BumpResolveEpochAsync();
        }

        await store.SetSettingAsync("geo-last-refresh", DateTimeOffset.UtcNow.ToString("O", System.Globalization.CultureInfo.InvariantCulture));
    }

    // Internal counters (not user settings): the resolve epoch a running tunnel polls to know it must
    // re-resolve its tracked domains, and the last-refresh stamp the TTL background gate reads. Kept as raw
    // key/value rows so they never surface in the user-facing settings surface.
    private async Task BumpResolveEpochAsync()
    {
        var current = await store.GetSettingAsync("geo-resolve-epoch");
        var next = (long.TryParse(current, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var value) ? value : 0) + 1;
        await store.SetSettingAsync("geo-resolve-epoch", next.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// Triggers a background geo refresh when the address cache is older than its validity window, so the
    /// in-use lists stay current without the user pressing "Обновить". A no-op when there are no sources, or
    /// when nothing uses geo routing (no assigned list and no running tunnel), or when the cache is still
    /// fresh. The work is coalesced through the shared session coordinator, so it never overlaps a manual
    /// update. Called by the periodic <see cref="GeoUpdateCheckService"/> (and so also on startup) (#83).
    /// </summary>
    public async Task RefreshStaleGeoAsync(CancellationToken ct)
    {
        var sources = await store.ListGeoSourcesAsync(ct);
        if (sources.Count == 0)
        {
            return;
        }

        // Only refresh when something actually consumes geo routing; otherwise there is nothing to keep fresh
        // and no reason to hit the network.
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

    /// <summary>
    /// Broadcasts the snapshot on a steady cadence while a download is in flight, so the spinner and
    /// percentage advance smoothly. A single pump (rather than a broadcast per progress callback) keeps
    /// snapshot rebuilds bounded; the change-dedup in <see cref="BroadcastIfChangedAsync"/> drops ticks
    /// that did not move the visible percent. Never throws - a store hiccup must not down the update.
    /// </summary>
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

    /// <summary>
    /// Records a source's download percent into the in-flight map. Cheap and synchronous; the progress
    /// pump turns it into broadcasts.
    /// </summary>
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

        logger.LogInformation("set setting {Key} = {Value}", key, args[1]);
        return new IpcAck(true, $"set {key} = {args[1]} (applies on reconnect)");
    }

    /// <summary>
    /// Checks the configured update URL for a different application version, records the result for the
    /// snapshot, and returns a human-readable status. A different version (newer or older) counts as
    /// available since the installer permits rollback.
    /// </summary>
    private async Task<IpcAck> CheckUpdateAsync(CancellationToken ct)
    {
        var settings = await settingsStore.LoadAsync(ct);
        if (string.IsNullOrWhiteSpace(settings.UpdateUrl))
        {
            return new IpcAck(false, "URL обновлений не задан.");
        }

        var info = await updateChecker.CheckAsync(settings.UpdateUrl, Version(), ct);
        updateState.Latest = info;
        await BroadcastIfChangedAsync(ct);

        if (info is null)
        {
            return new IpcAck(false, "Не удалось получить сведения об обновлении.");
        }

        if (!info.Available)
        {
            return new IpcAck(true, "Установлена актуальная версия.");
        }

        return new IpcAck(true, info.IsDowngrade
            ? $"Доступен откат к версии {info.Version}."
            : $"Доступно обновление до версии {info.Version}.");
    }

    /// <summary>
    /// Seeds the default geo sources (if none) then SYNCHRONOUSLY downloads every source and
    /// re-materializes the routing lists, returning a result. Used by the installer's "download lists"
    /// step (the privileged agent does the download the unprivileged bootstrapper cannot). A download
    /// failure is reported (Ok=false) but is non-fatal to the caller.
    /// </summary>
    private async Task<IpcAck> DownloadGeoAsync(CancellationToken ct)
    {
        await GeoDefaults.SeedIfEmptyAsync(store, logger, ct);

        var sources = await store.ListGeoSourcesAsync(ct);
        if (sources.Count == 0)
        {
            return new IpcAck(true, "Нет источников для загрузки.");
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
                return new IpcAck(false, $"Списки скачаны, но не удалось обработать: {ex.Message}");
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
            ? new IpcAck(true, $"Списки загружены ({sources.Count}).")
            : new IpcAck(false, $"Загружено {sources.Count - failed.Count} из {sources.Count}; не удалось: {string.Join(", ", failed)}.");
    }

    private async Task<string> BuildJsonAsync(CancellationToken ct)
    {
        var snapshot = await BuildSnapshotAsync(ct);
        return JsonSerializer.Serialize(new IpcEnvelope(IpcContract.SnapshotType, snapshot), IpcJson.Options);
    }

    private async Task<StatusSnapshot> BuildSnapshotAsync(CancellationToken ct)
    {
        var states = await store.ListBalancerStatesAsync(ct);

        // The agent manages exactly one target. Derive each config's live status from that bound
        // group's state alone, so a transient connecting / disconnecting state shows on its member
        // cards and stale rows from other groups don't leak in.
        var boundState = BoundTarget is not null ? states.FirstOrDefault(s => s.Group == BoundTarget) : null;
        var boundConfig = boundState is null
            ? null
            : (await store.GetBalancerAsync(boundState.Group, ct))?.Config ?? boundState.Group;
        var boundStatus = boundState?.Status ?? ConnectionStatus.Disconnected;

        var configs = new List<ConfigEntry>();
        foreach (var name in await configRepo.ListAsync(ct))
        {
            var configText = await configRepo.ReadTextAsync(name, ct);
            var geoSettings = await store.GetTunnelGeoAsync(name, ct);
            var transport = await store.GetConfigTransportAsync(name, ct);
            var configDns = await store.GetConfigDnsAsync(name, ct);
            var configEx = await store.GetConfigExclusionsAsync(name, ct);
            // No stored row → show the runtime default set (RFC1918 ranges + connected subnets) so the user
            // sees what currently keeps the LAN direct and can edit it; saving from the UI then creates the
            // row, which the tunnel applies verbatim (the default is never frozen into storage on its own).
            var exclusions = configEx?.Exclusions ?? string.Join('\n', routes.DefaultExclusionEntries());
            var status = boundState is not null && string.Equals(name, boundConfig, StringComparison.Ordinal)
                ? MemberDisplayStatus(boundState.Status, string.Equals(name, boundState.ActiveMember, StringComparison.Ordinal))
                : ConnectionStatus.Idle;
            var rules = geoSettings is not null ? geoSettings.Rules.Select(GeoConfigurator.Format).ToList() : [];
            configs.Add(new ConfigEntry(name, ReadEndpoint(configText), geoSettings?.GeoSplit ?? false, status, rules, transport?.UseWebSocket ?? false, transport?.WebSocketHost ?? string.Empty, transport?.WebSocketPort ?? 443, configDns?.Servers ?? string.Empty, exclusions, transport?.Mtu ?? 0));
        }

        var balancers = new List<BalancerEntry>();
        foreach (var name in await store.ListBalancerNamesAsync(ct))
        {
            var balancer = await store.GetBalancerAsync(name, ct);
            if (balancer is null)
            {
                continue;
            }

            var state = states.FirstOrDefault(item => item.Group == name);
            var (routingListId, useRouting) = await store.GetProfileRoutingAsync(name, ct);
            balancers.Add(new BalancerEntry(
                name,
                state?.Status ?? ConnectionStatus.Disconnected,
                balancer.Config,
                routingListId,
                useRouting));
        }

        var routingLists = new List<RoutingListEntry>();
        foreach (var list in await store.ListRoutingListsAsync(ct))
        {
            routingLists.Add(new RoutingListEntry(list.Id, list.Name, list.Rules.Count, list.Routes.Count, list.Domains.Count));
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
            // A stale error is hidden while a retry is in flight so the row shows the spinner, not the old
            // failure.
            var error = !updating && _lastError.TryGetValue(source.Name, out var err) ? err : null;
            sources.Add(new SourceEntry(source.Name, source.Kind, source.Url, updated, meta?.CategoryCount ?? 0, updating, updating ? percent : 0, updateAvailable, error));
        }

        var update = updateState.Latest;
        return new StatusSnapshot(Version(), BoundTarget, configs, balancers, routingLists, control.Running, boundStatus, control.RestartRequired, control.Target, sources, logBuffer.Snapshot(),
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
            settings.TunnelAllUdp);
    }

    /// <summary>
    /// Maps a balancer group's status to the connection status shown on a member config card.
    /// Non-active members read Connecting while the group brings a member up, otherwise Idle.
    /// </summary>
    private static string MemberDisplayStatus(string groupStatus, bool isActive)
    {
        return groupStatus switch
        {
            "connected" => isActive ? ConnectionStatus.Connected : ConnectionStatus.Idle,
            "connecting" => ConnectionStatus.Connecting,
            "disconnecting" => isActive ? ConnectionStatus.Disconnecting : ConnectionStatus.Idle,
            _ => ConnectionStatus.Idle,
        };
    }

    // Extracts the Endpoint value from a config's wg-quick text (for the connection card label).
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
