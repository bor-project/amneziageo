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
internal sealed class AgentStatusBroker(ConfigRepository configRepo, IStateStore store, GeoConfigurator geo, GeoFileUpdater geoFileUpdater, AgentControl control, SettingsStore settingsStore, ILogger<AgentStatusBroker> logger)
{
    private readonly List<PipeConnection> _clients = [];
    private readonly Lock _gate = new();
    private string? _lastJson;

    // Per-source download/apply progress while an update is in flight, keyed by source name. The value
    // is the download percent (0-100), or -1 while the routing lists re-materialize (indeterminate).
    // Presence in the map means "updating"; the snapshot overlays it onto each SourceEntry so the UI can
    // spin the refresh icon and show a percentage.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, int> _updating = new(StringComparer.Ordinal);

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
            lock (_gate)
            {
                _clients.Remove(connection);
            }

            connection.Dispose();
            logger.LogInformation("status client disconnected");
        }
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
                IpcContract.OpAddConfig => AddConfig(command.Args),
                IpcContract.OpAddBalancer => await AddBalancerAsync(command.Args, ct),
                IpcContract.OpSetGeo => await SetGeoAsync(command.Args, ct),
                IpcContract.OpListGeo => await ListGeoAsync(ct),
                IpcContract.OpSaveRoutingList => await SaveRoutingListAsync(command.Args, ct),
                IpcContract.OpRemoveRoutingList => await RemoveRoutingListAsync(command.Args, ct),
                IpcContract.OpGetRoutingList => await GetRoutingListAsync(command.Args, ct),
                IpcContract.OpAssignRouting => await AssignRoutingAsync(command.Args, ct),
                IpcContract.OpSetConnection => SetConnection(command.Args),
                IpcContract.OpSetSetting => await SetSettingAsync(command.Args, ct),
                IpcContract.OpSelectProfile => await SelectProfileAsync(command.Args, ct),
                IpcContract.OpAddSource => await AddSourceAsync(command.Args, ct),
                IpcContract.OpRemoveSource => await RemoveSourceAsync(command.Args, ct),
                IpcContract.OpUpdateSources => await UpdateSourcesAsync(ct),
                IpcContract.OpUpdateSource => await UpdateSourceAsync(command.Args, ct),
                _ => new IpcAck(false, $"unknown command: {command.Op}"),
            };
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "command {Op} failed", command.Op);
            return new IpcAck(false, ex.Message);
        }
    }

    private IpcAck AddConfig(IReadOnlyList<string> args)
    {
        if (args.Count < 2)
        {
            return new IpcAck(false, "add-config requires a name and a file path");
        }

        configRepo.Add(args[0], args[1]);
        logger.LogInformation("added config {Name}", args[0]);
        return new IpcAck(true, $"added config {args[0]}");
    }

    private async Task<IpcAck> AddBalancerAsync(IReadOnlyList<string> args, CancellationToken ct)
    {
        if (args.Count < 4)
        {
            return new IpcAck(false, "add-balancer requires a name, recheck, mode, and at least one member");
        }

        if (!int.TryParse(args[1], out var recheck) || recheck <= 0)
        {
            return new IpcAck(false, "invalid recheck seconds");
        }

        var mode = args[2].Equals("latency", StringComparison.OrdinalIgnoreCase) ? "latency" : "priority";
        var members = args.Skip(3).ToList();
        foreach (var member in members)
        {
            if (!configRepo.Exists(member))
            {
                return new IpcAck(false, $"unknown config: {member}");
            }
        }

        var existing = await store.GetBalancerAsync(args[0], ct);
        var updated = new BalancerGroup(args[0], recheck, members, mode);
        await store.SaveBalancerAsync(updated, ct);
        if (existing is null
            || existing.RecheckSeconds != updated.RecheckSeconds
            || !string.Equals(existing.Mode, updated.Mode, StringComparison.Ordinal)
            || !existing.Members.SequenceEqual(updated.Members, StringComparer.Ordinal))
        {
            control.Invalidate();
        }

        logger.LogInformation("saved balancer {Name} ({Count} members)", args[0], members.Count);
        return new IpcAck(true, $"saved balancer {args[0]}");
    }

    private async Task<IpcAck> SetGeoAsync(IReadOnlyList<string> args, CancellationToken ct)
    {
        if (args.Count < 2)
        {
            return new IpcAck(false, "set-geo requires a config name and on/off");
        }

        if (!configRepo.Exists(args[0]))
        {
            return new IpcAck(false, $"unknown config: {args[0]}");
        }

        var on = args[1].Equals("on", StringComparison.OrdinalIgnoreCase);
        var (rules, routes, domains) = await geo.ApplyAsync(args[0], on, args.Skip(2).ToList(), ct);
        logger.LogInformation("set-geo {Name}: split={On}, {Rules} rules -> {Routes} routes, {Domains} domains", args[0], on, rules, routes, domains);
        return new IpcAck(true, $"saved: {rules} rules, {routes} routes, {domains} domains (applies on reconnect)");
    }

    private async Task<IpcAck> ListGeoAsync(CancellationToken ct)
    {
        var tokens = await geo.CategoriesAsync(ct);
        return new IpcAck(true, string.Join('\n', tokens));
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
        if (await store.GetBalancerAsync(name, ct) is null && !configRepo.Exists(name))
        {
            return new IpcAck(false, $"unknown profile: {name}");
        }

        if (string.Equals(name, control.Target, StringComparison.Ordinal))
        {
            return new IpcAck(true, $"already active: {name}");
        }

        control.SetTarget(name);
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
        // (categories / updated time) lands in the next status snapshot.
        DownloadAndRematerialize([source]);
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
        var sources = await store.ListGeoSourcesAsync(ct);
        DownloadAndRematerialize(sources);
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

        DownloadAndRematerialize([source]);
        return new IpcAck(true, $"обновление {source.Name} запущено");
    }

    /// <summary>
    /// Re-downloads the given sources and re-materializes the routing lists on a background task, then
    /// pushes a fresh snapshot. Kept off the IPC command path so a slow download never blocks the pipe
    /// or overruns the client's command timeout. A lightweight ticker broadcasts in-flight progress so
    /// the UI can spin the refresh icon and show a live percentage.
    /// </summary>
    private void DownloadAndRematerialize(IReadOnlyList<GeoSource> sources)
    {
        // Claim only the sources not already in flight (TryAdd is atomic), so a repeated update of the
        // same source doesn't start a duplicate download / re-materialize.
        var pending = sources.Where(source => _updating.TryAdd(source.Name, 0)).ToList();
        if (pending.Count == 0)
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            var pump = new CancellationTokenSource();
            var ticker = ProgressPumpAsync(pump.Token);
            try
            {
                foreach (var source in pending)
                {
                    try
                    {
                        await geoFileUpdater.UpdateAsync(source, new SourceProgress(_updating, source.Name));
                        // Downloaded: switch to indeterminate while the re-materialize runs below.
                        _updating[source.Name] = -1;
                    }
                    catch (Exception ex)
                    {
                        logger.LogWarning(ex, "geo source download failed: {Name}", source.Name);
                        _updating.TryRemove(source.Name, out _);
                    }
                }

                // Stop the percentage broadcaster before re-materializing: there is no percent to show
                // while applying, and the spinner keeps running client-side from the broadcast "applying"
                // state — so the ticker's reads need not contend with the re-materialize writes.
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
        });
    }

    /// <summary>
    /// Broadcasts the snapshot on a steady cadence while a download is in flight, so the spinner and
    /// percentage advance smoothly. A single pump (rather than a broadcast per progress callback) keeps
    /// snapshot rebuilds bounded; the change-dedup in <see cref="BroadcastIfChangedAsync"/> drops ticks
    /// that did not move the visible percent. Never throws — a store hiccup must not down the update.
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

        // The kill-switch is armed at connect time, so a change while a tunnel is up applies on the next
        // reconnect — flag it so the UI shows the same reconnect prompt as a routing change.
        if (control.Running && (key is "killswitch" or "allow-lan"))
        {
            control.SetRestartRequired();
        }

        logger.LogInformation("set setting {Key} = {Value}", key, args[1]);
        return new IpcAck(true, $"set {key} = {args[1]} (applies on reconnect)");
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
        IReadOnlyList<string> boundMembers = boundState is null
            ? []
            : (await store.GetBalancerAsync(boundState.Group, ct))?.Members ?? [boundState.Group];
        var boundStatus = boundState?.Status ?? ConnectionStatus.Disconnected;

        var configs = new List<ConfigEntry>();
        foreach (var name in configRepo.List())
        {
            var geoSettings = await store.GetTunnelGeoAsync(name, ct);
            var status = boundState is not null && boundMembers.Contains(name, StringComparer.Ordinal)
                ? MemberDisplayStatus(boundState.Status, string.Equals(name, boundState.ActiveMember, StringComparison.Ordinal))
                : ConnectionStatus.Idle;
            var rules = geoSettings is not null ? geoSettings.Rules.Select(GeoConfigurator.Format).ToList() : [];
            configs.Add(new ConfigEntry(name, ReadEndpoint(name), geoSettings?.GeoSplit ?? false, status, rules));
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
                balancer.Mode,
                state?.Status ?? ConnectionStatus.Disconnected,
                state?.ActiveMember,
                balancer.Members,
                balancer.RecheckSeconds,
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
            sources.Add(new SourceEntry(source.Name, source.Kind, source.Url, updated, meta?.CategoryCount ?? 0, updating, updating ? percent : 0));
        }

        return new StatusSnapshot(Version(), BoundTarget, configs, balancers, routingLists, control.Running, boundStatus, control.RestartRequired, control.BetterMember, settings.KillSwitchEnabled, settings.AllowLan, control.Target, sources);
    }

    /// <summary>
    /// Maps a balancer group's status to the connection status shown on a member config card.
    /// Non-active members read Connecting while the group brings a member up, otherwise Idle.
    /// </summary>
    private static string MemberDisplayStatus(string groupStatus, bool isActive)
    {
        return groupStatus switch
        {
            "connected" or "degraded" => isActive ? ConnectionStatus.Connected : ConnectionStatus.Idle,
            "connecting" or "failover" => ConnectionStatus.Connecting,
            "disconnecting" => isActive ? ConnectionStatus.Disconnecting : ConnectionStatus.Idle,
            _ => ConnectionStatus.Idle,
        };
    }

    private static string ReadEndpoint(string name)
    {
        try
        {
            foreach (var line in File.ReadLines(TunnelPaths.ConfigFile(name)))
            {
                var trimmed = line.Trim();
                if (trimmed.StartsWith("Endpoint", StringComparison.OrdinalIgnoreCase))
                {
                    return trimmed[(trimmed.IndexOf('=') + 1)..].Trim();
                }
            }
        }
        catch (IOException)
        {
        }

        return string.Empty;
    }

    private static string Version()
    {
        return typeof(AgentStatusBroker).Assembly.GetName().Version?.ToString() ?? "0";
    }
}
