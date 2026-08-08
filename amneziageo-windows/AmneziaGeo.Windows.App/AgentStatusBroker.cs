using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using AmneziaGeo.Dal;
using AmneziaGeo.Decl;
using AmneziaGeo.Geo;
using AmneziaGeo.Ipc;
using Microsoft.Extensions.Logging;

namespace AmneziaGeo.Windows.App;

/// <summary>
/// Status snapshots broker for UI clients.
/// </summary>
internal sealed class AgentStatusBroker(GeoFileUpdater geoFileUpdater, GeoUpdateChecker geoUpdateChecker, AgentControl control, SettingsStore settingsStore, UpdateChecker updateChecker, UpdateState updateState, RouteManager routes, LogLevelController logLevel, DiagnosticsCollector diagnostics, SqliteLogStore logStore, ScopedStoreFactory storeFactory, IGeoFileStore geoFiles, ServiceManager serviceManager, UserStoreRegistry registry, ActiveTunnelScope activeScope, RuntimeInspector inspector, ILogger<AgentStatusBroker> logger)
{
    private readonly List<PipeConnection> _clients = [];
    private readonly Lock _gate = new();
    private readonly Dictionary<string, string> _lastJsonByRoot = new(StringComparer.OrdinalIgnoreCase);

    // Per-connection user scope: command handlers read the store/configRepo/geo of the connecting user.
    private readonly AsyncLocal<BrokerScope?> _connectionScope = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, BrokerScope> _scopes = new(StringComparer.OrdinalIgnoreCase);
    private BrokerScope? _defaultScope;

    private BrokerScope CurrentScope => _connectionScope.Value ?? (_defaultScope ??= ScopeFor(AppDataRoot.Base()));

    // Resolve the acting user's data surfaces from the current connection scope.
    private IStateStore store => CurrentScope.Store;
    private ConfigRepository configRepo => CurrentScope.ConfigRepo;
    private GeoConfigurator geo => CurrentScope.Geo;

    private BrokerScope ScopeFor(string userRoot, string? sid = null)
    {
        var key = Path.TrimEndingDirectorySeparator(Path.GetFullPath(userRoot));
        var scope = _scopes.GetOrAdd(key, root =>
        {
            var scopeStore = storeFactory.For(root);
            return new BrokerScope(root, scopeStore, new ConfigRepository(scopeStore, serviceManager), new GeoConfigurator(scopeStore, geoFiles));
        });
        if (sid is not null)
        {
            scope.Sid = sid;
        }

        return scope;
    }

    // The connecting client's user scope, or the default scope when the identity cannot be resolved.
    private BrokerScope ResolveScope(NamedPipeServerStream stream)
    {
        var resolved = UserContext.ResolveClient(stream);
        if (resolved is null)
        {
            logger.LogWarning("could not tell which user the app window belongs to, so the configurations under {Root} are served; a different user may see the wrong library", AppDataRoot.Base());
        }

        return ScopeFor(resolved?.Root ?? AppDataRoot.Base(), resolved?.Sid);
    }

    // Per-source progress: 0-100 while downloading, -1 while re-materializing. Presence means "updating".
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, int> _updating = new(StringComparer.Ordinal);

    // Per-source "update available" flag; surfaced on SourceEntry.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool> _updateAvailable = new(StringComparer.Ordinal);

    // Per-source last failure message; surfaced on SourceEntry.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> _lastError = new(StringComparer.Ordinal);

    // Serializes the update download-phase / cancel transitions, which arrive from concurrent pipe handlers.
    private readonly object _updateStateGate = new();

    // At most one geo-refresh session at a time; concurrent triggers queue (sources unioned, force OR-ed).
    private readonly object _geoSessionGate = new();
    private bool _geoRunning;
    private bool _geoQueued;
    private bool _geoQueuedForce;
    private readonly HashSet<string> _geoQueuedNames = new(StringComparer.Ordinal);

    // Bumped when a geo refresh session actually changed the local bases; surfaced so the tray can announce it.
    private int _geoUpdatedTick;

    /// <summary>
    /// Config reflected on the connection card.
    /// </summary>
    public string? BoundTarget => control.Running ? (control.RunningTarget ?? control.Target) : control.Target;

    /// <summary>
    /// Handles a connected client: sends the current snapshot, then reads until the client disconnects.
    /// </summary>
    public async Task HandleClientAsync(NamedPipeServerStream stream, CancellationToken ct)
    {
        var connection = new PipeConnection(stream);
        var scope = ResolveScope(stream);
        connection.Scope = scope;
        _connectionScope.Value = scope;
        lock (_gate)
        {
            _clients.Add(connection);
        }

        logger.LogInformation("status client connected for {Root}", scope.UserRoot);
        try
        {
            var json = await BuildJsonAsync(scope, ct);
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
            logger.LogError(ex, "the connection to the app window broke unexpectedly; it is closed and the window reconnects by itself, the tunnel is unaffected");
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
        List<PipeConnection> clients;
        lock (_gate)
        {
            if (_clients.Count == 0)
            {
                return;
            }

            clients = [.. _clients];
        }

        // One snapshot per distinct user: each client sees its own library, over the shared tunnel state.
        foreach (var group in clients.Where(c => c.Scope is not null).GroupBy(c => c.Scope!.UserRoot, StringComparer.OrdinalIgnoreCase))
        {
            var scope = group.First().Scope!;
            var json = await BuildJsonAsync(scope, ct);
            lock (_gate)
            {
                if (_lastJsonByRoot.TryGetValue(scope.UserRoot, out var last) && last == json)
                {
                    continue;
                }

                _lastJsonByRoot[scope.UserRoot] = json;
            }

            foreach (var target in group)
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
    }

    private async Task HandleLineAsync(PipeConnection connection, string line, CancellationToken ct)
    {
        var envelope = JsonSerializer.Deserialize<IpcEnvelope>(line, IpcJson.Options);
        if (envelope is not { Type: IpcContract.CommandType, Command: not null })
        {
            return;
        }

        if (envelope.Command.Op == IpcContract.OpAttachUi)
        {
            // UI presence is informational only: a live tunnel outlives the loss of every window (a tray crash,
            // Task Manager end-task, or an upgrade gap) and comes down only on an explicit user disconnect / exit
            // or an agent-service stop. A VPN must not fail open when its front-end dies.
            logger.LogInformation("UI session attached");
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
                IpcContract.OpSetGeo => await SetGeoAsync(command.Args, ct),
                IpcContract.OpSetWebSocket => await SetWebSocketAsync(command.Args, ct),
                IpcContract.OpSetConfigDns => await SetConfigDnsAsync(command.Args, ct),
                IpcContract.OpSetConfigExclusions => await SetConfigExclusionsAsync(command.Args, ct),
                IpcContract.OpListLocalSubnets => ListLocalSubnets(),
                IpcContract.OpListGeo => await ListGeoAsync(ct),
                IpcContract.OpGetGeoEntries => await GetGeoEntriesAsync(command.Args, ct),
                IpcContract.OpListProcesses => ListProcesses(),
                IpcContract.OpSaveRoutingList => await SaveRoutingListAsync(command.Args, ct),
                IpcContract.OpRemoveRoutingList => await RemoveRoutingListAsync(command.Args, ct),
                IpcContract.OpGetRoutingList => await GetRoutingListAsync(command.Args, ct),
                IpcContract.OpSetRoutingSettings => await SetRoutingSettingsAsync(command.Args, ct),
                IpcContract.OpGetRoutingSettings => await GetRoutingSettingsAsync(command.Args, ct),
                IpcContract.OpAssignRouting => await AssignRoutingAsync(command.Args, ct),
                IpcContract.OpSetConnection => await SetConnectionAsync(command.Args, ct),
                IpcContract.OpSetSetting => await SetSettingAsync(command.Args, ct),
                IpcContract.OpSelectConfig => await SelectConfigAsync(command.Args, ct),
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
                IpcContract.OpRenameConfig => await RenameConfigAsync(command.Args, ct),
                IpcContract.OpCopyConfig => await CopyConfigAsync(command.Args, ct),
                IpcContract.OpExportBundle => await ExportBundleAsync(command.Args, ct),
                IpcContract.OpImportBundle => await ImportBundleAsync(command.Args, ct),
                IpcContract.OpCheckUpdate => await CheckUpdateAsync(command.Args, ct),
                IpcContract.OpReportUpdateDownload => await ReportUpdateDownloadAsync(command.Args, ct),
                IpcContract.OpCancelUpdateDownload => await CancelUpdateDownloadAsync(ct),
                IpcContract.OpDownloadGeo => await DownloadGeoAsync(ct),
                IpcContract.OpCollectDiagnostics => await CollectDiagnosticsAsync(ct),
                IpcContract.OpReadLog => await ReadLogAsync(command.Args, ct),
                IpcContract.OpClearLog => await ClearLogAsync(command.Args, ct),
                IpcContract.OpExportLog => await ExportLogAsync(command.Args, ct),
                IpcContract.OpGetRuntimeConfig => await GetRuntimeConfigAsync(ct),
                IpcContract.OpGetCacheEntries => await GetCacheEntriesAsync(ct),
                IpcContract.OpLogClient => LogClient(command.Args),
                _ => new IpcAck(false, $"unknown command: {command.Op}"),
            };
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "the request '{Op}' from the app could not be carried out; nothing was changed", command.Op);
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

        logger.LogWarning("reported by the app window: {Detail}", args[0]);
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

        // Import creates; replacing the text of an existing configuration is edit-config.
        if (await configRepo.ExistsAsync(args[0], ct))
        {
            return new IpcAck(false, IpcMessage.Key("Agent_ConfigNameTaken", args[0]));
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

        await configRepo.EditFromTextAsync(args[0], args[1], ct);

        // Config text applies on a fresh tunnel; flag a reconnect when the running target is affected.
        if (control.Running && IsRunningMember(args[0]))
        {
            control.SetRestartRequired();
        }

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

        // Refuse while the config is the running target.
        if (control.Running && string.Equals(name, BoundTarget, StringComparison.Ordinal))
        {
            return new IpcAck(false, $"config {name} is running; disconnect first");
        }

        await configRepo.RemoveAsync(name, ct);
        await store.RemoveTunnelStateAsync(name, ct);
        await ClearBindingIfTargetAsync(name, ct);

        logger.LogInformation("removed config {Name}", name);
        return new IpcAck(true, $"removed config {name}");
    }

    // Clear target binding when the removed config was selected.
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

        if (await configRepo.ExistsAsync(destination, ct))
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

        if (await configRepo.ExistsAsync(newName, ct))
        {
            return new IpcAck(false, IpcMessage.Key("Agent_NameTaken", newName));
        }

        // Rename is allowed while running: the live adapter keeps carrying traffic under its old service name and
        // the UI reflects the new name at once; a later re-dial re-resolves the current config (ConfigRunner).
        await configRepo.RenameAsync(oldName, newName, ct);

        // Follow the rename in the live binding so the supervisor keeps resolving the config: a stale
        // running target would look like a broken binding on the next re-dial and drop the tunnel.
        control.RetargetName(oldName, newName);

        logger.LogInformation("renamed config {Old} -> {New}", oldName, newName);
        return new IpcAck(true, IpcMessage.Key("Agent_RenamedTo", newName));
    }

    // Export selection from OpExportBundle's arg0 JSON; all arrays optional. RoutingRules maps a routing
    // list name to the rule tokens to KEEP; an absent list keeps all its rules.
    private sealed record SelectionRequest(
        string[]? Configs,
        string[]? RoutingLists,
        Dictionary<string, string[]>? RoutingRules);

    // Export a selective bundle: caller picks configs and routing lists by name.
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

        var configNames = new HashSet<string>(selection.Configs ?? [], StringComparer.Ordinal);
        var routingNames = new HashSet<string>(selection.RoutingLists ?? [], StringComparer.Ordinal);

        if (configNames.Count == 0 && routingNames.Count == 0)
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
                transport = new PortableBundle.TransportBlock(tr.UseWebSocket, tr.WebSocketHost, tr.WebSocketPort, tr.Mtu, tr.UseIpv6);
            }

            PortableBundle.GeoBlock? geoBlock = null;
            var ownGeo = await store.GetTunnelGeoAsync(name, ct);
            if (ownGeo is not null && (ownGeo.GeoSplit || ownGeo.Rules.Count > 0))
            {
                geoBlock = new PortableBundle.GeoBlock(ownGeo.GeoSplit, ownGeo.Rules.Select(GeoConfigurator.Format).ToList());
            }

            var dns = await store.GetConfigDnsAsync(name, ct);
            var exclusions = await store.GetConfigExclusionsAsync(name, ct);
            configBlocks.Add(new PortableBundle.ConfigBlock(name, configText, transport, geoBlock, dns?.Servers, exclusions?.Exclusions));
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
                    settingsBlock = new PortableBundle.RoutingSettingsBlock(settings.Exclusions, settings.AllUdp, settings.Mode, settings.UseGlobalProxy);
                }

                routingBlocks.Add(new PortableBundle.RoutingBlock(name, rules, settingsBlock));
            }
        }

        var bundle = new PortableBundle.Bundle(
            PortableBundle.FormatTag,
            PortableBundle.CurrentVersion,
            configBlocks,
            routingBlocks);

        logger.LogInformation(
            "exported bundle: {Configs} configs, {Routing} routing lists",
            configBlocks.Count,
            routingBlocks.Count);
        return new IpcAck(true, PortableBundle.Serialize(bundle));
    }

    // Import a selective bundle: recreates configs and routing lists under fresh names. All-or-nothing; no rollback of rows already written.
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

        // Configs and routing lists each own a separate name space. Snapshots taken before import
        // drive collision detection.
        var existingConfigs = new HashSet<string>(await configRepo.ListAsync(ct), StringComparer.Ordinal);
        var existingLists = (await store.ListRoutingListsAsync(ct))
            .ToDictionary(l => l.Name, l => l, StringComparer.Ordinal);

        // Growing name spaces so the add-as-new path never reuses a name taken earlier in THIS import.
        var configNames = new HashSet<string>(existingConfigs, StringComparer.Ordinal);
        var listNames = new HashSet<string>(existingLists.Keys, StringComparer.Ordinal);

        var configNameMap = new Dictionary<string, string>(StringComparer.Ordinal);
        var routingMap = new Dictionary<string, (string Name, long Id)>(StringComparer.Ordinal);
        var renames = new List<string>();
        var importedConfigs = 0;
        var importedLists = 0;

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
                    await store.SetConfigTransportAsync(new ConfigTransport(incoming, trE.UseWebSocket, trE.Host, trE.Port, trE.Mtu, trE.UseIpv6), ct);
                }

                await ApplyConfigDataAsync(incoming, block, ct);

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
                importedConfigs++;
                continue;
            }

            var finalName = FreeName(incoming, configNames);
            configNames.Add(finalName);
            if (!string.Equals(finalName, block.Name, StringComparison.Ordinal))
            {
                renames.Add($"«{block.Name}» → «{finalName}»");
            }

            // Malformed config text throws here and aborts the import.
            await configRepo.AddFromTextAsync(finalName, block.ConfigText, ct);

            if (block.Transport is { } tr)
            {
                await store.SetConfigTransportAsync(new ConfigTransport(finalName, tr.UseWebSocket, tr.Host, tr.Port, tr.Mtu, tr.UseIpv6), ct);
            }

            await ApplyConfigDataAsync(finalName, block, ct);

            if (block.Geo is { } g)
            {
                // Re-materialize rule tokens against local geo data.
                await geo.ApplyAsync(finalName, g.Split, g.Rules, ct);
            }

            configNameMap[block.Name] = finalName;
            importedConfigs++;
        }

        foreach (var block in bundle.RoutingLists)
        {
            // Same-name list already here and a non-default policy: act on the existing row (id kept, so
            // the selection keeps pointing at it).
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
                    await store.SetRoutingSettingsAsync(new RoutingSettings(existingList.Id, sE.Exclusions, sE.AllUdp, sE.Mode, sE.UseGlobalProxy), ct);
                }

                routingMap[block.Name] = (existingList.Name, existingList.Id);
                importedLists++;
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
                await store.SetRoutingSettingsAsync(new RoutingSettings(newId, s.Exclusions, s.AllUdp, s.Mode, s.UseGlobalProxy), ct);
            }

            routingMap[block.Name] = (finalName, newId);
            importedLists++;
        }

        if (bundle.Profiles is { Count: > 0 } legacy)
        {
            logger.LogInformation("the bundle carries {Count} profile(s) from an older build; their pairings are dropped, the configs and routing lists above came through", legacy.Count);
        }

        logger.LogInformation(
            "imported bundle: {Configs} configs, {Routing} routing lists",
            importedConfigs,
            importedLists);

        var shown = renames.Distinct(StringComparer.Ordinal).ToList();
        if (shown.Count == 0)
        {
            return new IpcAck(true, IpcMessage.Key(
                "Agent_BundleImported",
                importedConfigs,
                importedLists));
        }

        if (shown.Count <= 5)
        {
            return new IpcAck(true, IpcMessage.Key(
                "Agent_BundleImportedRenamed",
                importedConfigs,
                importedLists,
                string.Join(", ", shown)));
        }

        return new IpcAck(true, IpcMessage.Key(
            "Agent_BundleImportedRenamedMany",
            importedConfigs,
            importedLists));
    }

    // DNS and bypass entries a bundle carries for a config.
    private async Task ApplyConfigDataAsync(string name, PortableBundle.ConfigBlock block, CancellationToken ct)
    {
        if (block.Dns is { } dns)
        {
            if (dns.Trim().Length == 0)
            {
                await store.RemoveConfigDnsAsync(name, ct);
            }
            else
            {
                await store.SetConfigDnsAsync(new ConfigDns(name, dns), ct);
            }
        }

        if (block.Exclusions is { } exclusions)
        {
            if (exclusions.Trim().Length == 0)
            {
                await store.RemoveConfigExclusionsAsync(name, ct);
            }
            else
            {
                await store.SetConfigExclusionsAsync(new ConfigExclusions(name, exclusions), ct);
            }
        }
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

    // Config names must be valid file names; replace invalid chars.
    private static string SanitizeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = name.Select(c => Array.IndexOf(invalid, c) >= 0 ? '_' : c).ToArray();
        var clean = new string(chars).Trim();
        return clean.Length == 0 ? "config" : clean;
    }

    // Set the config as target when none is set; idempotent.
    private async Task EnsureDefaultTargetAsync(string name, CancellationToken ct)
    {
        if (!string.IsNullOrEmpty(control.Target))
        {
            return;
        }

        control.SetTarget(name);
        await store.SetSettingAsync(AgentControl.SelectedTargetKey, name, ct);
        logger.LogInformation("auto-selected configuration '{Config}' as connection target (none was set)", name);
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
        var (rules, routes, domains, skipped) = await geo.ApplyAsync(args[0], on, args.Skip(2).ToList(), ct);
        AnnounceRules();
        logger.LogInformation("{Name}: {Rules} rule(s) saved, giving {Routes} address range(s) and {Domains} domain(s); only the named traffic goes through the tunnel: {On} — takes effect on reconnect", args[0], rules, routes, domains, on);
        var summary = $"saved: {rules} rules, {routes} routes, {domains} domains";
        return new IpcAck(true, skipped > 0 ? $"{summary}, {skipped} tokens ignored (applies on reconnect)" : $"{summary} (applies on reconnect)");
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

        var previous = await store.GetConfigTransportAsync(args[0], ct);

        // Optional 6th arg: route IPv6 for this config; absent keeps the stored value (CLI set-websocket sends none).
        var useIpv6 = args.Count > 5
            ? args[5].Trim().ToLowerInvariant() is "on" or "1" or "true" or "yes"
            : previous?.UseIpv6 ?? false;

        var updated = new ConfigTransport(args[0], on, host, port, mtu, useIpv6);
        await store.SetConfigTransportAsync(updated, ct);

        // Transport applies on a fresh tunnel; flag a reconnect when the running target is affected and something
        // actually changed - IPv6 also swaps the adapter address, so a real edit here always needs a fresh tunnel.
        if (previous != updated && control.Running && IsRunningMember(args[0]))
        {
            control.SetRestartRequired();
        }

        logger.LogInformation("{Name}: carried inside a websocket: {On} (server {Host}, port {Port}), packet size {Mtu}, IPv6 allowed: {V6} — takes effect on reconnect",
            args[0], on, host.Length == 0 ? "from the configuration" : host, port, mtu, useIpv6);
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
        if (control.Running && IsRunningMember(args[0]))
        {
            control.SetRestartRequired();
        }

        logger.LogInformation("{Name}: names outside the tunnel will be resolved by '{Servers}' (empty means your provider's own servers) — takes effect on reconnect", args[0], servers);
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
        if (control.Running && IsRunningMember(args[0]))
        {
            control.SetRestartRequired();
        }

        logger.LogInformation("{Name}: the list of addresses and names kept out of the tunnel was saved ({Len} characters) — takes effect on reconnect", args[0], exclusions.Length);
        return new IpcAck(true, IpcMessage.Key("Agent_ExclusionsSaved"));
    }

    // Default LAN bypass CIDRs (RFC1918 + connected subnets), newline-separated.
    private IpcAck ListLocalSubnets()
    {
        return new IpcAck(true, string.Join('\n', routes.DefaultExclusionEntries()));
    }

    private bool IsRunningMember(string config)
    {
        return string.Equals(BoundTarget, config, StringComparison.Ordinal);
    }

    private async Task<IpcAck> ListGeoAsync(CancellationToken ct)
    {
        var tokens = await geo.CategoriesAsync(ct);
        return new IpcAck(true, string.Join('\n', tokens));
    }

    // Every entry a geo rule expands to, as a JSON array.
    private async Task<IpcAck> GetGeoEntriesAsync(IReadOnlyList<string> args, CancellationToken ct)
    {
        if (args.Count < 1 || string.IsNullOrWhiteSpace(args[0]))
        {
            return new IpcAck(false, "get-geo-entries requires a rule token");
        }

        var entries = await geo.EntriesAsync(args[0], ct);
        // A limit of 0 asks for the whole category; without one the answer stays a short page.
        var cap = args.Count > 1 && int.TryParse(args[1], System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var parsed)
            ? (parsed <= 0 ? entries.Count : parsed)
            : 300;
        var json = System.Text.Json.JsonSerializer.Serialize(new { total = entries.Count, entries = entries.Take(cap).ToArray() });
        return new IpcAck(true, json);
    }

    private async Task<IpcAck> GetRuntimeConfigAsync(CancellationToken ct)
    {
        var (config, applied) = await InspectTargetAsync(ct);
        if (string.IsNullOrEmpty(config))
        {
            return new IpcAck(false, IpcMessage.Key("Agent_NoConfigSelected"));
        }

        return new IpcAck(true, await inspector.RenderAsync(store, config, applied, ct));
    }

    private async Task<IpcAck> GetCacheEntriesAsync(CancellationToken ct)
    {
        var (config, _) = await InspectTargetAsync(ct);
        if (string.IsNullOrEmpty(config))
        {
            return new IpcAck(false, IpcMessage.Key("Agent_NoConfigSelected"));
        }

        // The tunnel runs in its own service process; without a session here the snapshot comes from there.
        if (!inspector.HasLiveSession && RuntimeSnapshotPipe.Send(config, RuntimeSnapshotPipe.OpSnapshot, logger) is { Length: > 0 } served)
        {
            return new IpcAck(true, served);
        }

        return new IpcAck(true, System.Text.Json.JsonSerializer.Serialize(inspector.Collect()));
    }

    // The tunnel the config screen reports on: the running one while this user owns it, otherwise the config
    // the next connect would raise.
    private async Task<(string Config, bool Applied)> InspectTargetAsync(CancellationToken ct)
    {
        var scope = CurrentScope;
        var applied = control.Running && activeScope.IsOwnedBy(scope.UserRoot, scope.Sid);
        var name = applied
            ? control.RunningTarget ?? control.Target ?? string.Empty
            : await store.GetSettingAsync(AgentControl.SelectedTargetKey, ct) ?? string.Empty;
        return (name, applied);
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

    // Rules that only take effect on a fresh tunnel: any app rule (the ETW matcher is built at bring-up) and the
    // Block bucket (its WFP drops are armed once and never rebuilt). Proxy geo is reconciled live by the domain
    // tracker. Direct is reconciled live by the routing cache, but only while the bucket is large enough to be
    // resolved per destination - a small one is materialized at bring-up, so a change to it needs a fresh tunnel.
    private static bool RequiresReconnect(string rule, bool directIsEager)
    {
        if (IsAppRule(rule))
        {
            return true;
        }

        var bar = rule.IndexOf('|');
        var role = bar > 0 ? rule[..bar].ToLowerInvariant() : "proxy";
        // Every bucket is resolved per destination and re-decided live, so only app rules still need a fresh tunnel.
        return role switch
        {
            "direct" or "exclude" => directIsEager,
            _ => false,
        };
    }

    // Nothing is materialized at bring-up any more; kept so the reconnect rule reads the same on both sides.
    private static bool DirectIsEager(RoutingList? list) => false;

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

        // The name column is unique: a clash would surface as a raw SQLite error from the insert.
        var lists = await store.ListRoutingListsAsync(ct);
        if (lists.Any(l => l.Id != id && string.Equals(l.Name, name, StringComparison.Ordinal)))
        {
            return new IpcAck(false, IpcMessage.Key("Agent_RoutingListNameTaken", name));
        }

        // Proxy geo (domains/geoip) applies live; app rules and the Block bucket need a fresh tunnel, and so does
        // Direct while its bucket is small enough to be materialized at bring-up. The eager flag is read on both
        // sides: a bucket that crosses the threshold either way leaves stale routes behind or lacks fresh ones.
        var previous = id > 0 ? await store.GetRoutingListAsync(id, ct) : null;
        var previousEager = DirectIsEager(previous);
        var previousReconnect = previous is null
            ? new HashSet<string>(StringComparer.Ordinal)
            : previous.Rules.Select(GeoConfigurator.FormatWithRole)
                .Where(r => RequiresReconnect(r, previousEager))
                .ToHashSet(StringComparer.Ordinal);

        var resultId = await geo.ApplyToRoutingListAsync(id, name, args.Skip(2).ToList(), ct);

        // Flag a reconnect only when the running tunnel routes through this list and a connect-time rule changed.
        if (control.Running && BoundTarget is not null)
        {
            var listId = await store.GetSelectedRoutingListAsync(ct);
            var eager = DirectIsEager(await store.GetRoutingListAsync(resultId, ct));
            var newReconnect = args.Skip(2).Where(r => RequiresReconnect(r, eager)).ToHashSet(StringComparer.Ordinal);
            if (listId == resultId && !newReconnect.SetEquals(previousReconnect))
            {
                control.SetRestartRequired();
            }
        }

        AnnounceRules();
        logger.LogInformation("saved routing list {Id} '{Name}' ({Rules} rules)", resultId, name, args.Count - 2);
        return new IpcAck(true, resultId.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    // The tunnel runs in its own process and polls for nothing: a rule change is announced to it here, and it
    // drops every verdict taken under the old rules.
    private void AnnounceRules() => Announce(RuntimeSnapshotPipe.OpRules);

    private void Announce(string op)
    {
        if (!control.Running || control.RunningTarget is not { Length: > 0 } tunnel)
        {
            return;
        }

        _ = Task.Run(() => RuntimeSnapshotPipe.Send(tunnel, op, logger));
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

        // Args after id: exclusions, all-UDP, mode, use-global-proxy. All optional; all-default clears the row.
        // IPv6 is per-config now (set-websocket), no longer carried here.
        var exclusions = args.Count > 1 ? args[1].Trim() : string.Empty;
        var udpArg = args.Count > 2 ? args[2].Trim().ToLowerInvariant() : "off";
        var allUdp = udpArg is "on" or "1" or "true" or "yes";
        var globalArg = args.Count > 4 ? args[4].Trim().ToLowerInvariant() : "off";
        var useGlobalProxy = globalArg is "on" or "1" or "true" or "yes";

        // Mode mirrors the global-proxy flag: full routes everything minus Direct, split tunnels only Proxy.
        var mode = useGlobalProxy ? "full" : "split";

        // Every field here is read once at bring-up, so a real change needs a fresh tunnel - but a save that
        // changes nothing must not light the banner.
        var previous = await store.GetRoutingSettingsAsync(id, ct);
        var changed = (previous?.AllUdp ?? false) != allUdp
            || (previous?.UseGlobalProxy ?? false) != useGlobalProxy
            || !string.Equals(previous?.Exclusions ?? string.Empty, exclusions, StringComparison.Ordinal);

        if (exclusions.Length == 0 && !allUdp && !useGlobalProxy)
        {
            await store.RemoveRoutingSettingsAsync(id, ct);
        }
        else
        {
            await store.SetRoutingSettingsAsync(new RoutingSettings(id, exclusions, allUdp, mode, useGlobalProxy), ct);
        }

        // Settings apply on a fresh tunnel; flag a reconnect when the running tunnel routes through this list.
        if (changed && control.Running && BoundTarget is not null)
        {
            if (await store.GetSelectedRoutingListAsync(ct) == id)
            {
                control.SetRestartRequired();
            }
        }

        logger.LogInformation("routing list {Id} saved: exclusions {Len} characters, all UDP through the tunnel: {Udp}, mode {Mode}, everything through the tunnel except the direct list: {Global}", id, exclusions.Length, allUdp, mode, useGlobalProxy);
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
            useGlobalProxy = settings?.UseGlobalProxy ?? false,
        });
        return new IpcAck(true, json);
    }

    private async Task<IpcAck> AssignRoutingAsync(IReadOnlyList<string> args, CancellationToken ct)
    {
        if (args.Count < 1)
        {
            return new IpcAck(false, "assign-routing requires a list id (or 'none')");
        }

        long? listId = null;
        if (!args[0].Equals("none", StringComparison.OrdinalIgnoreCase))
        {
            if (!long.TryParse(args[0], out var id) || id <= 0)
            {
                return new IpcAck(false, "invalid routing list id");
            }

            if (await store.GetRoutingListAsync(id, ct) is null)
            {
                return new IpcAck(false, $"unknown routing list: {id}");
            }

            listId = id;
        }

        var currentList = await store.GetSelectedRoutingListAsync(ct);
        await store.SetSelectedRoutingListAsync(listId, ct);
        if (currentList != listId && control.Running)
        {
            // Routing applies on a fresh tunnel; flag a restart instead of re-applying live.
            control.SetRestartRequired();
        }

        logger.LogInformation("routing list {List} now applies to every configuration; with none picked, all traffic goes through the tunnel", listId);
        return new IpcAck(true, $"routing: {listId?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "none"} (applies on reconnect)");
    }

    private async Task<IpcAck> SetConnectionAsync(IReadOnlyList<string> args, CancellationToken ct)
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

        var scope = CurrentScope;

        // The single machine-wide tunnel belongs to one user; another user's request needs an explicit takeover.
        if (control.Running && !activeScope.IsOwnedBy(scope.UserRoot, scope.Sid))
        {
            var takeover = connect && args.Any(a => a.Equals("takeover", StringComparison.OrdinalIgnoreCase));
            if (!takeover)
            {
                return new IpcAck(false, IpcMessage.Key("Agent_TunnelOwnedByOther"));
            }
        }

        if (connect)
        {
            activeScope.SetOwner(scope.UserRoot, scope.Sid);
            var target = await store.GetSettingAsync(AgentControl.SelectedTargetKey, ct);
            if (!string.IsNullOrEmpty(target))
            {
                control.SetTarget(target);
            }

            await store.SetSettingAsync("last-owner-root", scope.UserRoot, ct);
            await store.SetSettingAsync("last-owner-target", target ?? string.Empty, ct);
            control.SetRunning(true);
            logger.LogInformation("connect requested by {Root}", scope.UserRoot);
            return new IpcAck(true, "connecting");
        }

        control.SetRunning(false);
        logger.LogInformation("disconnect requested by {Root}", scope.UserRoot);
        return new IpcAck(true, "disconnecting");
    }

    private async Task<IpcAck> SelectConfigAsync(IReadOnlyList<string> args, CancellationToken ct)
    {
        if (args.Count < 1 || string.IsNullOrWhiteSpace(args[0]))
        {
            return new IpcAck(false, "set-config requires a config name");
        }

        var name = args[0];
        if (!await configRepo.ExistsAsync(name, ct))
        {
            return new IpcAck(false, $"unknown config: {name}");
        }

        var currentSelection = await store.GetSettingAsync(AgentControl.SelectedTargetKey, ct);
        if (string.Equals(name, currentSelection, StringComparison.Ordinal))
        {
            return new IpcAck(true, $"already active: {name}");
        }

        // Persist the per-user selection; reflect it on the shared control only when this user owns the tunnel or none runs.
        await store.SetSettingAsync(AgentControl.SelectedTargetKey, name, ct);
        if (!control.Running || activeScope.IsOwnedBy(CurrentScope.UserRoot, CurrentScope.Sid))
        {
            control.SetTarget(name);
        }

        logger.LogInformation("selected configuration '{Config}'", name);

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

    /// <summary>
    /// Downloads every geo source now (the periodic auto-update); a changed base advances the geo-updated tick. Returns the source count.
    /// </summary>
    public async Task<int> UpdateAllSourcesAsync(CancellationToken ct)
    {
        var sources = await store.ListGeoSourcesAsync(ct);
        if (sources.Count > 0)
        {
            EnqueueGeoRefresh(sources, forceResolve: false);
        }

        return sources.Count;
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
            logger.LogWarning(ex, "could not check the rule database {Name} for a newer version; the copy already downloaded stays in use", source.Name);
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
                logger.LogWarning(ex, "refreshing the rule databases failed part-way; the rules already in force keep working and the refresh is tried again");
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
                logger.LogWarning(ex, "the list of rule databases could not be re-read, so the queued refresh only re-checks what is already loaded");
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
                        logger.LogWarning(ex, "the rule database {Name} could not be downloaded; the copy already on disk stays in use, rules do not change", source.Name);
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
                        await RematerializeAllUsersAsync();
                    }
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "the routing lists could not be rebuilt from the refreshed databases; they keep the rules they had, so a new database does not take effect until this succeeds");
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

        // A real base change advances the geo-updated tick and is pushed so the tray can announce it.
        if (changed)
        {
            Interlocked.Increment(ref _geoUpdatedTick);
            await BroadcastIfChangedAsync(CancellationToken.None);
        }

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

    // Re-materialize routing lists for every known user against the current geo bases.
    private async Task RematerializeAllUsersAsync(CancellationToken ct = default)
    {
        foreach (var root in registry.OpenedRoots())
        {
            await new GeoConfigurator(storeFactory.For(root), geoFiles).RematerializeAllRoutingListsAsync(ct);
        }
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
        if (await store.GetSelectedRoutingListAsync(ct) is null && !control.Running)
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
                logger.LogWarning(ex, "download progress could not be sent to the window; the download itself continues, only the progress bar stalls");
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

        // The route lifetime applies live; the running tunnel adopts it for what it already holds.
        if (key == AppSettings.RouteTtlKey)
        {
            Announce(RuntimeSnapshotPipe.OpTtl);
            logger.LogInformation("route lifetime set to {Value} s", args[1]);
            return new IpcAck(true, $"route lifetime = {args[1]} s");
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
            logger.LogError(ex, "the diagnostics archive could not be built; nothing was written to disk");
            return new IpcAck(false, IpcMessage.Key("Agent_DiagnosticsFailed", ex.Message));
        }
    }

    // Reads a window of one log table for the in-app viewer (OpReadLog), newest first. The agent queries the
    // DB as SYSTEM so an unprivileged UI can view logs it cannot open directly.
    private async Task<IpcAck> ReadLogAsync(IReadOnlyList<string> args, CancellationToken ct)
    {
        if (args.Count < 1 || !IsKnownTable(args[0]))
        {
            return new IpcAck(false, "read-log requires a known table (ageo|routes)");
        }

        var table = args[0];
        var limit = args.Count > 1 && int.TryParse(args[1], out var l) ? Math.Clamp(l, 1, 2000) : 400;
        long? beforeId = args.Count > 2 && long.TryParse(args[2], out var b) && b > 0 ? b : null;
        var minLevelId = table == SqliteLogStore.AgentTable && args.Count > 3 ? LogLevels.MinId(args[3]) : null;
        var search = args.Count > 4 && args[4].Length > 0 ? args[4] : null;

        var page = await logStore.QueryAsync(table, beforeId, limit, minLevelId, search, ct);
        var lines = page.Rows.Select(LogFormat.Render).ToList();
        var firstId = page.Rows.Count > 0 ? page.Rows[^1].Id : 0L;
        var matchCount = search is null ? 0 : await logStore.CountAsync(table, minLevelId, search, ct);

        return new IpcAck(true, JsonSerializer.Serialize(new
        {
            lines,
            firstId,
            hasOlder = page.HasOlder,
            matchCount,
        }));
    }

    // Clears one log table (OpClearLog); other logs are left untouched.
    private async Task<IpcAck> ClearLogAsync(IReadOnlyList<string> args, CancellationToken ct)
    {
        if (args.Count < 1 || !IsKnownTable(args[0]))
        {
            return new IpcAck(false, "clear-log requires a known table (ageo|routes)");
        }

        await logStore.ClearAsync(args[0], ct);
        logger.LogInformation("log cleared: {Table}", args[0]);
        return new IpcAck(true, "log cleared");
    }

    // Renders a whole log table to text for the UI to save under the user's account (OpExportLog).
    private async Task<IpcAck> ExportLogAsync(IReadOnlyList<string> args, CancellationToken ct)
    {
        if (args.Count < 1 || !IsKnownTable(args[0]))
        {
            return new IpcAck(false, "export-log requires a known table");
        }

        var text = await logStore.RenderAsync(args[0], LogFormat.Render, ct);
        logger.LogInformation("log rendered for export: {Table} ({Chars} chars)", args[0], text.Length);
        return new IpcAck(true, text);
    }

    private static bool IsKnownTable(string name)
    {
        return name is SqliteLogStore.AgentTable or SqliteLogStore.RoutesTable;
    }

    private async Task<IpcAck> CheckUpdateAsync(IReadOnlyList<string> args, CancellationToken ct)
    {
        // A silent check (the UI's automatic on-open / hourly check, #22) refreshes the update banner without
        // driving the checking state, so it never triggers the tray's "Checking…" item or up-to-date notice.
        var silent = args.Count > 0 && string.Equals(args[0], "silent", StringComparison.Ordinal);
        var settings = await settingsStore.LoadAsync(ct);
        if (string.IsNullOrWhiteSpace(settings.UpdateUrl))
        {
            return new IpcAck(false, IpcMessage.Key("Agent_UpdateUrlNotSet"));
        }

        // A manual check drives a checking state on the snapshot, so the tray shows "Checking…" and, once done,
        // announces the up-to-date result (#15). Silent (#22) and background (UpdateCheckService) checks never
        // set it, so the notice fires only for a user-initiated check.
        if (!silent)
        {
            lock (_updateStateGate)
            {
                updateState.Checking = true;
                updateState.CheckFailed = false;
            }

            await BroadcastIfChangedAsync(ct);
        }

        // The checking flag is always cleared, even if the check is cancelled (agent shutdown), so the tray
        // never latches a "Checking…" menu item.
        var result = (Info: (UpdateInfo?)null, Faulted: true);
        try
        {
            result = await TryCheckUpdateAsync(settings.UpdateUrl, ct);
        }
        finally
        {
            var faulted = result.Faulted || result.Info is null;
            lock (_updateStateGate)
            {
                if (!silent)
                {
                    updateState.Checking = false;
                    updateState.CheckFailed = faulted;
                }

                if (!faulted)
                {
                    updateState.Latest = result.Info;
                }
            }
        }

        await BroadcastIfChangedAsync(ct);

        if (result.Faulted)
        {
            return new IpcAck(false, IpcMessage.Key("Agent_UpdateServerUnavailable"));
        }

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
                return (await updateChecker.CheckAsync(url, Version(), AppSettings.BuildTarget, settings.AllowPrerelease, token), false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "update check failed");
                return (null, true);
            }
        }
    }

    // Records the setup download phase and percent reported by the UI process that owns the byte-pump; it rides
    // the snapshot so the tray and every window share one update state. A phase transition or a percent change is
    // broadcast at once, since no periodic pump runs during an app self-update and the tray menu reads the shared
    // percent (#17). The "failed" phase latches the download-failed flag for the tray warning.
    private async Task<IpcAck> ReportUpdateDownloadAsync(IReadOnlyList<string> args, CancellationToken ct)
    {
        var phase = args.Count > 0 ? args[0] : "idle";
        var newPhase = phase switch
        {
            "downloading" => UpdateDownloadPhase.Downloading,
            "downloaded" => UpdateDownloadPhase.Downloaded,
            _ => UpdateDownloadPhase.Idle,
        };
        var newFailed = phase == "failed";
        var percent = args.Count > 1 && int.TryParse(args[1], out var parsed) ? parsed : 0;
        var setupPath = args.Count > 2 ? args[2] : string.Empty;
        var version = args.Count > 3 ? args[3] : string.Empty;

        bool phaseChanged;
        bool percentChanged;
        bool hadCancel;
        lock (_updateStateGate)
        {
            hadCancel = updateState.CancelRequested;
            phaseChanged = updateState.DownloadPhase != newPhase || updateState.DownloadFailed != newFailed;
            percentChanged = updateState.DownloadPercent != percent;
            updateState.DownloadPhase = newPhase;
            updateState.DownloadFailed = newFailed;
            updateState.DownloadPercent = percent;
            updateState.DownloadedSetupPath = setupPath;
            updateState.DownloadedVersion = version;
            // Clear a pending cancel only on a phase transition (a fresh start drops a stale one, a stop consumes
            // it); a per-percent tick must not clear it, so a cancel set mid-download survives until the byte-pump
            // sees it (#17).
            if (phaseChanged)
            {
                updateState.CancelRequested = false;
            }
        }

        if (phaseChanged || percentChanged || hadCancel)
        {
            await BroadcastIfChangedAsync(ct);
        }

        return new IpcAck(true, "ok");
    }

    // Flags a cancel on a running download so it rides the next snapshot; the UI that owns the byte-pump aborts
    // it. Ignored when no download is in flight, so a stale request cannot cancel a later download.
    private async Task<IpcAck> CancelUpdateDownloadAsync(CancellationToken ct)
    {
        var flagged = false;
        lock (_updateStateGate)
        {
            if (updateState.DownloadPhase == UpdateDownloadPhase.Downloading)
            {
                updateState.CancelRequested = true;
                flagged = true;
            }
        }

        if (flagged)
        {
            await BroadcastIfChangedAsync(ct);
        }

        return new IpcAck(true, "ok");
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
                    logger.LogWarning(ex, "the rule database {Name} could not be downloaded; the rules stay as they are", source.Name);
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
                await RematerializeAllUsersAsync(ct);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "the databases were downloaded but the routing lists could not be rebuilt from them; the previous rules stay in force");
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

    private async Task<string> BuildJsonAsync(BrokerScope scope, CancellationToken ct)
    {
        var snapshot = await BuildSnapshotAsync(scope, ct);
        return JsonSerializer.Serialize(new IpcEnvelope(IpcContract.SnapshotType, snapshot), IpcJson.Options);
    }

    private async Task<StatusSnapshot> BuildSnapshotAsync(BrokerScope scope, CancellationToken ct)
    {
        var store = scope.Store;
        var configRepo = scope.ConfigRepo;
        var states = await store.ListTunnelStatesAsync(ct);

        // The live connection view belongs to the tunnel owner; other users see their own idle library.
        var owned = activeScope.IsOwnedBy(scope.UserRoot, scope.Sid);
        var selectedTarget = await store.GetSettingAsync(AgentControl.SelectedTargetKey, ct) ?? string.Empty;
        var selectedRouting = await store.GetSelectedRoutingListAsync(ct);
        var boundTarget = owned ? BoundTarget : null;

        // Derive each config's status from the bound tunnel's state alone.
        var boundState = boundTarget is not null ? states.FirstOrDefault(s => s.Name == boundTarget) : null;
        var boundConfig = boundState?.Name;
        var boundStatus = boundState?.Status ?? ConnectionStatus.Disconnected;

        var configs = new List<ConfigEntry>();
        // Computed at most once per snapshot, and only for a config that has no saved exclusions.
        var defaultExclusions = default(string);
        foreach (var name in await configRepo.ListAsync(ct))
        {
            var configText = await configRepo.ReadTextAsync(name, ct);
            var geoSettings = await store.GetTunnelGeoAsync(name, ct);
            var transport = await store.GetConfigTransportAsync(name, ct);
            var configDns = await store.GetConfigDnsAsync(name, ct);
            var configEx = await store.GetConfigExclusionsAsync(name, ct);
            // No row -> show the runtime default LAN bypass; saving freezes it.
            var exclusions = configEx?.Exclusions ?? (defaultExclusions ??= string.Join('\n', routes.DefaultExclusionEntries()));
            var status = boundState is not null && string.Equals(name, boundConfig, StringComparison.Ordinal)
                ? DisplayStatus(boundState.Status)
                : ConnectionStatus.Idle;
            var rules = geoSettings is not null ? geoSettings.Rules.Select(GeoConfigurator.Format).ToList() : [];
            configs.Add(new ConfigEntry(name, ReadEndpoint(configText), geoSettings?.GeoSplit ?? false, status, rules, transport?.UseWebSocket ?? false, transport?.WebSocketHost ?? string.Empty, transport?.WebSocketPort ?? 443, configDns?.Servers ?? string.Empty, exclusions, transport?.Mtu ?? 0, transport?.UseIpv6 ?? false));
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
        // A download only counts as ready when its version still matches the offered one, so a newer check
        // drops a setup downloaded for the previous version.
        var downloadedForCurrent = updateState.DownloadPhase == UpdateDownloadPhase.Downloaded
            && update is not null
            && string.Equals(updateState.DownloadedVersion, update.Version, StringComparison.Ordinal);
        var connectFailed = owned && control.ConnectFailed;
        var disconnectFailed = owned && control.DisconnectFailed;
        return new StatusSnapshot(Version(), boundTarget, configs, routingLists, owned && control.Running, boundStatus, owned && control.RestartRequired, selectedTarget, selectedRouting, sources,
            settings.UpdateUrl,
            update?.Available ?? false,
            update?.Version ?? string.Empty,
            update?.SetupUrl ?? string.Empty,
            update?.Description ?? string.Empty,
            settings.GeoAutoCheck,
            settings.GeoCheckIntervalHours,
            settings.GeoCacheValidityHours,
            connectFailed,
            AppSettings.EngineVersion,
            settings.TunnelAllUdp,
            settings.LogLevel,
            settings.RouteLog,
            connectFailed ? control.ConnectFailReason.ToString() : string.Empty,
            connectFailed ? (control.ConnectFailDetail ?? string.Empty) : string.Empty,
            owned ? control.RetryAttempt : 0,
            settings.SurviveReboot,
            settings.PeriodicReconnect,
            settings.PeriodicReconnectIntervalSeconds,
            settings.RouteTtlSeconds,
            settings.ShowNotifications,
            settings.AllowPrerelease,
            update?.Sha256 ?? string.Empty,
            updateState.DownloadPhase == UpdateDownloadPhase.Downloading,
            downloadedForCurrent,
            updateState.DownloadPercent,
            downloadedForCurrent ? updateState.DownloadedSetupPath : string.Empty,
            disconnectFailed,
            disconnectFailed ? (control.DisconnectFailDetail ?? string.Empty) : string.Empty,
            updateState.DownloadFailed,
            updateState.CancelRequested,
            updateState.Checking,
            updateState.CheckFailed,
            Volatile.Read(ref _geoUpdatedTick),
            AppSettings.BuildTarget);
    }

    private static string DisplayStatus(string profileStatus)
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
