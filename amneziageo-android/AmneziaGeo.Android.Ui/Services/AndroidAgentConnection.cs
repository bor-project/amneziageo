using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using Android.App;
using Android.Content;
using Android.Net;
using Android.OS;
using AmneziaGeo.Android.Engine;
using AmneziaGeo.Dal;
using AmneziaGeo.Decl;
using AmneziaGeo.Geo;
using AmneziaGeo.Ipc;
using AmneziaGeo.Localization;
using AmneziaGeo.Ui.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace AmneziaGeo.Android.Ui.Services;

/// <summary>
/// In-process agent for the Android head: persists configs/profiles, projects status snapshots, and drives
/// the tunnel through <see cref="GeoVpnService"/>.
/// </summary>
internal sealed class AndroidAgentConnection : IAgentConnection
{
    private readonly Dictionary<string, string> _configs = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _profiles = new(StringComparer.Ordinal);
    // Per-profile routing assignment: value = "<listId>|<1|0>" (1 = use routing).
    private readonly Dictionary<string, string> _routing = new(StringComparer.Ordinal);
    private readonly string _storePath;
    private const int DefaultMtu = 1420;

    private readonly SqliteStateStore _store;
    private readonly GeoConfigurator _geo;
    private readonly GeoFileUpdater _geoUpdater;
    private readonly AndroidAgentLog _log;
    private readonly GeoHttp _geoHttp;
    private readonly HttpClient _httpClient = new();
    private IReadOnlyList<RoutingListEntry> _routingSummaries = [];
    private IReadOnlyDictionary<string, ConfigTransport> _transports = new Dictionary<string, ConfigTransport>(StringComparer.Ordinal);
    private IReadOnlyList<GeoSource> _geoSources = [];
    private IReadOnlyList<GeoFileMetadata> _geoFileMeta = [];
    private readonly HashSet<string> _updatingSources = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string?> _sourceErrors = new(StringComparer.Ordinal);
    private Task? _initTask;
    private Task? _geoFilesTask;
    private VpnBridge.Listener? _events;

    private string? _selectedTarget;
    private string? _boundTarget;
    private string _boundStatus = ConnectionStatus.Disconnected;
    private bool _active;
    private bool _connectFailed;
    private bool _started;
    private bool _disposed;
    private string _logLevel = "error";
    private bool _routeLog;
    private int _routeTtl = 300;

    public event Action? Connected;

    public event Action? Disconnected;

    public event Action<StatusSnapshot>? SnapshotReceived;

    /// <summary>
    /// The in-process agent instance.
    /// </summary>
    public static AndroidAgentConnection? Current { get; private set; }

    /// <summary>
    /// Latest snapshot pushed to the listeners.
    /// </summary>
    public StatusSnapshot? Latest { get; private set; }

    /// <summary>
    /// ctor
    /// </summary>
    public AndroidAgentConnection()
    {
        var dir = Application.Context.FilesDir?.AbsolutePath ?? ".";
        _storePath = System.IO.Path.Combine(dir, "agent.json");
        var geoFiles = new AndroidGeoFileStore(System.IO.Path.Combine(dir, "geo"));
        _store = new SqliteStateStore(System.IO.Path.Combine(dir, "state.db"));
        _geoHttp = new GeoHttp(_httpClient, NullLogger<GeoHttp>.Instance);
        _geoUpdater = new GeoFileUpdater(_store, _geoHttp, geoFiles);
        _geo = new GeoConfigurator(_store, geoFiles);
        _log = new AndroidAgentLog(System.IO.Path.Combine(dir, "log.db"));
        Current = this;
    }

    public void Start()
    {
        if (_started || _disposed)
        {
            return;
        }

        _started = true;
        Load();
        _log.SetCaptureLevel(_logLevel);
        _log.SetRouteLog(_routeLog);
        _events = new VpnBridge.Listener { Handler = OnVpnEvent };
        VpnBridge.Listen(Application.Context, _events, VpnBridge.ActionEvent);
        MainActivity.Resumed += SyncTunnelState;
        Connected?.Invoke();
        PushSnapshot();
        SyncTunnelState();
        _ = EnsureInitAsync().ContinueWith(_ => PushSnapshot(), TaskScheduler.Default);
    }

    public Task<IpcAck> SendCommandAsync(IpcCommand command) => DispatchAsync(command);

    public Task<IpcAck> SendCommandRawAsync(IpcCommand command) => DispatchAsync(command);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        MainActivity.Resumed -= SyncTunnelState;
        if (_events is not null)
        {
            Application.Context.UnregisterReceiver(_events);
            _events = null;
        }

        _geoHttp.Dispose();
        _httpClient.Dispose();
        _log.Dispose();
        if (_started)
        {
            Disconnected?.Invoke();
        }
    }

    private async Task<IpcAck> DispatchAsync(IpcCommand command)
    {
        var args = command.Args;
        switch (command.Op)
        {
            case IpcContract.OpImportConfig:
            case IpcContract.OpEditConfig:
                if (args.Count < 2)
                {
                    return Fail();
                }

                // Import creates; replacing the text of an existing configuration is edit-config.
                if (command.Op == IpcContract.OpImportConfig && _configs.ContainsKey(args[0]))
                {
                    return new IpcAck(false, IpcMessage.Key("Agent_ConfigNameTaken", args[0]));
                }

                _configs[args[0]] = args[1];
                Save();
                PushSnapshot();
                return Ok();

            case IpcContract.OpAddProfile:
                if (args.Count < 1)
                {
                    return Fail();
                }

                _profiles[args[0]] = args.Count > 1 ? args[1] : string.Empty;
                Save();
                PushSnapshot();
                return Ok();

            case IpcContract.OpRemoveConfig:
                if (args.Count > 0)
                {
                    _configs.Remove(args[0]);
                    Save();
                    PushSnapshot();
                }

                return Ok();

            case IpcContract.OpRemoveProfile:
                if (args.Count > 0)
                {
                    _profiles.Remove(args[0]);
                    _routing.Remove(args[0]);
                    Save();
                    PushSnapshot();
                }

                return Ok();

            case IpcContract.OpGetConfig:
                return args.Count > 0 && _configs.TryGetValue(args[0], out var text)
                    ? new IpcAck(true, text)
                    : Fail();

            case IpcContract.OpSelectProfile:
                _selectedTarget = args.Count > 0 ? args[0] : null;
                PushSnapshot();
                return Ok();

            case IpcContract.OpSetConnection:
                return await SetConnectionAsync(args.Count > 0 ? args[0] : string.Empty);

            case IpcContract.OpRenameProfile:
                return RenameEntry(_profiles, args, rebindConfig: false);

            case IpcContract.OpRenameConfig:
                return RenameEntry(_configs, args, rebindConfig: true);

            case IpcContract.OpAssignRouting:
                return AssignRouting(args);

            case IpcContract.OpAddSource:
                return await AddSourceAsync(args);

            case IpcContract.OpRemoveSource:
                return await RemoveSourceAsync(args);

            case IpcContract.OpEditSource:
                return await EditSourceAsync(args);

            case IpcContract.OpUpdateSource:
                return await UpdateSourceAsync(args);

            case IpcContract.OpUpdateSources:
                return await UpdateAllSourcesAsync();

            case IpcContract.OpListLocalSubnets:
                return new IpcAck(true, string.Join('\n', GeoVpnService.LocalSubnets()));

            case IpcContract.OpListGeo:
                return await ListGeoAsync();

            case IpcContract.OpGetGeoEntries:
                return await GetGeoEntriesAsync(args);

            case IpcContract.OpSaveRoutingList:
                return await SaveRoutingListAsync(args);

            case IpcContract.OpGetRoutingList:
                return await GetRoutingListAsync(args);

            case IpcContract.OpRemoveRoutingList:
                return await RemoveRoutingListAsync(args);

            case IpcContract.OpGetRoutingSettings:
                return await GetRoutingSettingsAsync(args);

            case IpcContract.OpSetRoutingSettings:
                return await SetRoutingSettingsAsync(args);

            case IpcContract.OpSetWebSocket:
                return await SetWebSocketAsync(args);

            case IpcContract.OpReadLog:
                return await ReadLogAsync(args);

            case IpcContract.OpClearLog:
                return await ClearLogAsync(args);

            case IpcContract.OpExportLog:
                return await ExportLogAsync(args);

            case IpcContract.OpSetSetting:
                return SetSetting(args);

            case IpcContract.OpLogClient:
                return LogClient(args);

            case IpcContract.OpGetRuntimeConfig:
                return await GetRuntimeConfigAsync();

            case IpcContract.OpGetCacheEntries:
                return await GetCacheEntriesAsync();

            case IpcContract.OpExportBundle:
                return await ExportBundleAsync(args);

            case IpcContract.OpImportBundle:
                return await ImportBundleAsync(args);

            default:
                _log.Warn("agent", $"command '{command.Op}' is not wired in the Android agent");
                return new IpcAck(false, IpcMessage.Key("Android_OpNotWired", command.Op));
        }
    }

    private async Task<IpcAck> SetConnectionAsync(string desired)
    {
        if (desired == "disconnect")
        {
            _log.Info("agent", "disconnect requested");
            StartService(GeoVpnService.ActionDisconnect, null, null, null, null, null, foreground: false);
            return Ok();
        }

        var configName = _selectedTarget is not null && _profiles.TryGetValue(_selectedTarget, out var bound) && bound.Length > 0
            ? bound
            : _selectedTarget;
        if (configName is null || !_configs.TryGetValue(configName, out var configText))
        {
            _connectFailed = true;
            _log.Error("agent", $"connect refused: no config for target '{_selectedTarget}'");
            PushSnapshot();
            return new IpcAck(false, "config missing");
        }

        var granted = await EnsureVpnPermissionAsync();
        if (!granted)
        {
            _log.Warn("agent", "connect refused: vpn permission denied");
            return new IpcAck(false, "vpn permission denied");
        }

        _connectFailed = false;
        var (appMode, appPkgs) = await ResolveAppSplitFromRoutingAsync(_selectedTarget);
        VpnBridge.WritePlan(await BuildPlanAsync(_selectedTarget).ConfigureAwait(false));
        _log.Info("agent", $"connect requested: target '{_selectedTarget}', app-split {appMode}");
        StartService(GeoVpnService.ActionConnect, configText, _selectedTarget,
            appMode == "off" ? null : appMode, appMode == "off" ? null : appPkgs,
            _transports.GetValueOrDefault(configName), foreground: true);
        return Ok();
    }

    private static async Task<bool> EnsureVpnPermissionAsync()
    {
        var prepare = VpnService.Prepare(Application.Context);
        if (prepare is null)
        {
            return true;
        }

        var activity = MainActivity.Current;
        return activity is not null && await activity.RequestVpnPermissionAsync(prepare);
    }

    private static void StartService(string action, string? config, string? name, string? appMode, string[]? appPkgs, ConfigTransport? transport, bool foreground)
    {
        var context = Application.Context;
        var intent = new Intent(context, typeof(GeoVpnService));
        intent.SetAction(action);
        if (config is not null)
        {
            intent.PutExtra(GeoVpnService.ExtraConfig, config);
        }

        if (name is not null)
        {
            intent.PutExtra(GeoVpnService.ExtraName, name);
        }

        if (appMode is not null && appPkgs is { Length: > 0 })
        {
            intent.PutExtra(GeoVpnService.ExtraAppMode, appMode);
            intent.PutExtra(GeoVpnService.ExtraAppList, appPkgs);
        }

        if (transport is not null)
        {
            intent.PutExtra(GeoVpnService.ExtraMtu, transport.Mtu);
            intent.PutExtra(GeoVpnService.ExtraIpv6, transport.UseIpv6);
        }

        if (foreground && Build.VERSION.SdkInt >= BuildVersionCodes.O)
        {
            context.StartForegroundService(intent);
        }
        else
        {
            context.StartService(intent);
        }
    }

    // Reads what is running: the tunnel lives in another process and can be gone without a word, so the window
    // asks again whenever it comes up.
    private void SyncTunnelState()
    {
        if (_disposed)
        {
            return;
        }

        if (VpnBridge.IsRunning(Application.Context))
        {
            VpnBridge.RequestState(Application.Context);
        }
        else if (_active)
        {
            OnVpnStateChanged(VpnStage.Disconnected, null);
        }
    }

    // Takes what the tunnel process reports: a stage change or a line for the routing log.
    private void OnVpnEvent(Intent intent)
    {
        if (_disposed)
        {
            return;
        }

        var trace = intent.GetStringExtra(VpnBridge.ExtraTrace);
        if (trace is not null)
        {
            _log.Route(trace);
            return;
        }

        var stage = intent.GetIntExtra(VpnBridge.ExtraStage, -1);
        if (stage >= 0)
        {
            OnVpnStateChanged((VpnStage)stage, intent.GetStringExtra(VpnBridge.ExtraDetail));
        }
    }

    private void OnVpnStateChanged(VpnStage stage, string? detail)
    {
        // The session name comes back from the tunnel, so a head that started after it still names what runs.
        var session = string.IsNullOrEmpty(detail) ? _selectedTarget : detail;
        switch (stage)
        {
            case VpnStage.Connecting:
                _active = true;
                _boundStatus = ConnectionStatus.Connecting;
                _boundTarget = session;
                break;
            case VpnStage.Connected:
                _active = true;
                _boundStatus = ConnectionStatus.Connected;
                _boundTarget = session;
                _connectFailed = false;
                break;
            case VpnStage.Disconnected:
                _active = false;
                _boundStatus = ConnectionStatus.Disconnected;
                _boundTarget = null;
                break;
            case VpnStage.Failed:
                _active = false;
                _boundStatus = ConnectionStatus.Disconnected;
                _boundTarget = null;
                _connectFailed = true;
                break;
        }

        LogVpnStage(stage, detail);
        PushSnapshot();
    }

    // Records a tunnel state transition in the agent log; a failure is logged at error level with its cause.
    private void LogVpnStage(VpnStage stage, string? detail)
    {
        var suffix = string.IsNullOrEmpty(detail) ? string.Empty : ": " + detail;
        if (stage == VpnStage.Failed)
        {
            _log.Error("tunnel", "failed" + suffix);
        }
        else
        {
            _log.Info("tunnel", stage.ToString().ToLowerInvariant() + suffix);
        }
    }

    private void PushSnapshot()
    {
        var configs = _configs.Select(kv => Entry(kv.Key, kv.Value)).ToList();
        var profiles = _profiles
            .Select(kv =>
            {
                var routing = _routing.TryGetValue(kv.Key, out var raw) ? ParseRouting(raw) : (ListId: (long?)null, UseRouting: false);
                return new ProfileEntry(kv.Key, StatusFor(kv.Key), kv.Value, routing.ListId, routing.UseRouting);
            })
            .ToList();

        Latest = new StatusSnapshot(
            AgentVersion: "Android preview",
            BoundTarget: _boundTarget,
            Configs: configs,
            Profiles: profiles,
            RoutingLists: _routingSummaries,
            Active: _active,
            BoundStatus: _boundStatus,
            SelectedTarget: _selectedTarget,
            Sources: BuildSources(),
            ConnectFailed: _connectFailed,
            EngineVersion: string.Empty,
            LogLevel: _logLevel,
            RouteLog: _routeLog,
            RouteTtlSeconds: _routeTtl);

        SnapshotReceived?.Invoke(Latest);
    }

    private ConfigEntry Entry(string name, string config)
    {
        var transport = _transports.GetValueOrDefault(name);
        return new ConfigEntry(name, WgConfigEditor.GetEndpoint(config) ?? string.Empty, false, StatusFor(name), [],
            WebSocket: false,
            WebSocketHost: transport?.WebSocketHost ?? string.Empty,
            WebSocketPort: transport?.WebSocketPort ?? 443,
            Mtu: transport?.Mtu ?? 0,
            UseIpv6: transport?.UseIpv6 ?? false);
    }

    private string StatusFor(string target)
    {
        return _active && string.Equals(_boundTarget, target, StringComparison.Ordinal)
            ? ConnectionStatus.Connected
            : ConnectionStatus.Disconnected;
    }

    // Initializes the SQLite store and seeds default geo sources once.
    private Task EnsureInitAsync() => _initTask ??= InitAsync();

    private async Task InitAsync()
    {
        await _log.InitializeAsync().ConfigureAwait(false);
        _log.Info("agent", "android agent started");
        await _store.InitializeAsync().ConfigureAwait(false);
        await GeoDefaults.SeedIfEmptyAsync(_store, null, CancellationToken.None).ConfigureAwait(false);
        await RematerializeIfStaleAsync().ConfigureAwait(false);
        await RefreshTransportsAsync().ConfigureAwait(false);
        await RefreshRoutingSummariesAsync().ConfigureAwait(false);
        await RefreshGeoSourcesAsync().ConfigureAwait(false);
    }

    // Rebuilds the stored lists when the app started covering a rule token differently than the run that wrote them.
    private async Task RematerializeIfStaleAsync()
    {
        try
        {
            if (await _geo.RematerializeIfStaleAsync().ConfigureAwait(false))
            {
                _log.Info("geo", "rule expansion changed, the stored routing lists were rebuilt");
            }
        }
        catch (Exception ex)
        {
            _log.Warn("geo", "the routing lists could not be rebuilt: " + ex);
        }
    }

    private async Task RefreshGeoSourcesAsync()
    {
        _geoSources = await _store.ListGeoSourcesAsync().ConfigureAwait(false);
        _geoFileMeta = await _store.ListGeoFilesAsync().ConfigureAwait(false);
    }

    // The geo bases shown on the sources screen: both geoip and geosite; geosite domains route on Android via resolution to IPs.
    private IReadOnlyList<SourceEntry> BuildSources()
    {
        var metaByName = _geoFileMeta.ToDictionary(m => m.Name, StringComparer.Ordinal);
        var list = new List<SourceEntry>();
        foreach (var source in _geoSources)
        {
            var meta = metaByName.GetValueOrDefault(source.Name);
            var updated = meta is null
                ? null
                : meta.UpdatedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
            var updating = _updatingSources.Contains(source.Name);
            var error = !updating && _sourceErrors.TryGetValue(source.Name, out var e) ? e : null;
            list.Add(new SourceEntry(source.Name, source.Kind, source.Url, updated, meta?.CategoryCount ?? 0, updating, 0, false, error));
        }

        return list;
    }

    private async Task<IpcAck> AddSourceAsync(IReadOnlyList<string> args)
    {
        if (args.Count < 2)
        {
            return Fail();
        }

        var kind = args[0];
        var url = args[1].Trim();
        if (url.Length == 0)
        {
            return Fail();
        }

        await EnsureInitAsync().ConfigureAwait(false);
        var position = _geoSources.Count == 0 ? 1 : _geoSources.Max(s => s.Position) + 1;
        var name = NextSourceName(kind, position);
        var source = new GeoSource(name, kind, url, position);
        await _store.SaveGeoSourceAsync(source).ConfigureAwait(false);
        await UpdateOneSourceAsync(source).ConfigureAwait(false);
        await AfterSourcesChangedAsync().ConfigureAwait(false);
        return _sourceErrors.TryGetValue(name, out var err) && err is not null ? new IpcAck(false, err) : Ok();
    }

    // A source name not already taken; positions can repeat after removals, so bump until free.
    private string NextSourceName(string kind, int position)
    {
        var n = position;
        var candidate = $"{kind}-{n}";
        while (_geoSources.Any(s => string.Equals(s.Name, candidate, StringComparison.Ordinal)))
        {
            n++;
            candidate = $"{kind}-{n}";
        }

        return candidate;
    }

    private async Task<IpcAck> RemoveSourceAsync(IReadOnlyList<string> args)
    {
        if (args.Count < 1)
        {
            return Fail();
        }

        await EnsureInitAsync().ConfigureAwait(false);
        await _store.RemoveGeoSourceAsync(args[0]).ConfigureAwait(false);
        _sourceErrors.Remove(args[0]);
        await AfterSourcesChangedAsync().ConfigureAwait(false);
        return Ok();
    }

    private async Task<IpcAck> EditSourceAsync(IReadOnlyList<string> args)
    {
        if (args.Count < 3)
        {
            return Fail();
        }

        var name = args[0];
        var kind = args[1];
        var url = args[2].Trim();
        await EnsureInitAsync().ConfigureAwait(false);
        var current = _geoSources.FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.Ordinal));
        if (current is null)
        {
            return Fail();
        }

        var edited = current with { Kind = kind, Url = url };
        await _store.SaveGeoSourceAsync(edited).ConfigureAwait(false);
        await UpdateOneSourceAsync(edited).ConfigureAwait(false);
        await AfterSourcesChangedAsync().ConfigureAwait(false);
        return _sourceErrors.TryGetValue(name, out var err) && err is not null ? new IpcAck(false, err) : Ok();
    }

    private async Task<IpcAck> UpdateSourceAsync(IReadOnlyList<string> args)
    {
        if (args.Count < 1)
        {
            return Fail();
        }

        await EnsureInitAsync().ConfigureAwait(false);
        var source = _geoSources.FirstOrDefault(s => string.Equals(s.Name, args[0], StringComparison.Ordinal));
        if (source is null)
        {
            return Fail();
        }

        _updatingSources.Add(source.Name);
        PushSnapshot();
        await UpdateOneSourceAsync(source).ConfigureAwait(false);
        await AfterSourcesChangedAsync().ConfigureAwait(false);
        return Ok();
    }

    private async Task<IpcAck> UpdateAllSourcesAsync()
    {
        await EnsureInitAsync().ConfigureAwait(false);
        var targets = _geoSources.ToList();
        foreach (var source in targets)
        {
            _updatingSources.Add(source.Name);
        }

        PushSnapshot();
        foreach (var source in targets)
        {
            await UpdateOneSourceAsync(source).ConfigureAwait(false);
        }

        await AfterSourcesChangedAsync().ConfigureAwait(false);
        return Ok();
    }

    // Downloads one geo base, recording any failure against its name and clearing the in-flight flag.
    private async Task UpdateOneSourceAsync(GeoSource source)
    {
        try
        {
            await _geoUpdater.UpdateAsync(source).ConfigureAwait(false);
            _sourceErrors.Remove(source.Name);
        }
        catch (Exception ex)
        {
            _sourceErrors[source.Name] = ex.Message;
        }
        finally
        {
            _updatingSources.Remove(source.Name);
        }
    }

    // Re-materializes routing lists against the changed bases, refreshes caches, and pushes a fresh snapshot.
    private async Task AfterSourcesChangedAsync()
    {
        try
        {
            await _geo.RematerializeAllRoutingListsAsync().ConfigureAwait(false);
        }
        catch (Exception)
        {
        }

        await RefreshRoutingSummariesAsync().ConfigureAwait(false);
        await RefreshGeoSourcesAsync().ConfigureAwait(false);
        PushSnapshot();
    }

    private async Task RefreshRoutingSummariesAsync()
    {
        var summaries = await _store.ListRoutingListSummariesAsync().ConfigureAwait(false);
        _routingSummaries = summaries
            .Select(s => new RoutingListEntry(s.Id, s.Name, s.RuleCount, s.RouteCount, s.DomainCount))
            .ToList();
    }

    // Downloads the geoip databases on first use; retries after a failed attempt.
    private Task EnsureGeoFilesAsync()
    {
        var task = _geoFilesTask;
        if (task is { IsCompletedSuccessfully: true })
        {
            return task;
        }

        if (task is null || task.IsFaulted || task.IsCanceled)
        {
            _geoFilesTask = task = DownloadGeoFilesAsync();
        }

        return task;
    }

    private async Task DownloadGeoFilesAsync()
    {
        await EnsureInitAsync().ConfigureAwait(false);
        var sources = await _store.ListGeoSourcesAsync().ConfigureAwait(false);
        var any = false;
        foreach (var source in sources)
        {
            try
            {
                await _geoUpdater.UpdateAsync(source).ConfigureAwait(false);
                any = true;
            }
            catch (Exception)
            {
            }
        }

        if (!any)
        {
            throw new InvalidOperationException("geo download failed");
        }
    }

    private async Task<IpcAck> ListGeoAsync()
    {
        try
        {
            await EnsureGeoFilesAsync().ConfigureAwait(false);
        }
        catch (Exception)
        {
        }

        var tokens = await _geo.CategoriesAsync().ConfigureAwait(false);
        return new IpcAck(true, string.Join('\n', tokens));
    }

    private async Task<IpcAck> GetGeoEntriesAsync(IReadOnlyList<string> args)
    {
        if (args.Count < 1)
        {
            return Fail();
        }

        try
        {
            await EnsureGeoFilesAsync().ConfigureAwait(false);
        }
        catch (Exception)
        {
        }

        var entries = await _geo.EntriesAsync(args[0]).ConfigureAwait(false);
        var cap = args.Count > 1 && int.TryParse(args[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var c)
            ? Math.Clamp(c, 1, 5000)
            : 300;
        return new IpcAck(true, JsonSerializer.Serialize(new { total = entries.Count, entries = entries.Take(cap).ToArray() }));
    }

    private async Task<IpcAck> SaveRoutingListAsync(IReadOnlyList<string> args)
    {
        if (args.Count < 2)
        {
            return Fail();
        }

        await EnsureInitAsync().ConfigureAwait(false);
        try
        {
            await EnsureGeoFilesAsync().ConfigureAwait(false);
        }
        catch (Exception)
        {
        }

        var id = long.TryParse(args[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : 0;
        var name = args[1].Trim();
        if (name.Length == 0)
        {
            return Fail();
        }

        // The name column is unique: a clash would surface as a raw SQLite error from the insert.
        var lists = await _store.ListRoutingListsAsync().ConfigureAwait(false);
        if (lists.Any(l => l.Id != id && string.Equals(l.Name, name, StringComparison.Ordinal)))
        {
            return new IpcAck(false, IpcMessage.Key("Agent_RoutingListNameTaken", name));
        }

        var savedId = await _geo.ApplyToRoutingListAsync(id, name, [.. args.Skip(2)]).ConfigureAwait(false);
        await RefreshRoutingSummariesAsync().ConfigureAwait(false);
        PushSnapshot();
        return new IpcAck(true, savedId.ToString(CultureInfo.InvariantCulture));
    }

    private async Task<IpcAck> GetRoutingListAsync(IReadOnlyList<string> args)
    {
        if (args.Count < 1 || !long.TryParse(args[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var id))
        {
            return Fail();
        }

        await EnsureInitAsync().ConfigureAwait(false);
        var list = await _store.GetRoutingListAsync(id).ConfigureAwait(false);
        if (list is null)
        {
            return Fail();
        }

        return new IpcAck(true, string.Join('\n', list.Rules.Select(GeoConfigurator.FormatWithRole)));
    }

    private async Task<IpcAck> RemoveRoutingListAsync(IReadOnlyList<string> args)
    {
        if (args.Count < 1 || !long.TryParse(args[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var id))
        {
            return Fail();
        }

        await EnsureInitAsync().ConfigureAwait(false);
        await _store.RemoveRoutingListAsync(id).ConfigureAwait(false);
        var detached = _routing.Where(kv => ParseRouting(kv.Value).ListId == id).Select(kv => kv.Key).ToList();
        foreach (var profile in detached)
        {
            _routing.Remove(profile);
        }

        if (detached.Count > 0)
        {
            Save();
        }

        await RefreshRoutingSummariesAsync().ConfigureAwait(false);
        PushSnapshot();
        return Ok();
    }

    private async Task<IpcAck> GetRoutingSettingsAsync(IReadOnlyList<string> args)
    {
        if (args.Count < 1 || !long.TryParse(args[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var id))
        {
            return Fail();
        }

        await EnsureInitAsync().ConfigureAwait(false);
        var settings = await _store.GetRoutingSettingsAsync(id).ConfigureAwait(false);
        var json = JsonSerializer.Serialize(new
        {
            exclusions = settings?.Exclusions ?? string.Empty,
            allUdp = settings?.AllUdp ?? false,
            mode = settings?.Mode ?? "split",
            useGlobalProxy = settings?.UseGlobalProxy ?? false,
        });
        return new IpcAck(true, json);
    }

    private async Task<IpcAck> SetRoutingSettingsAsync(IReadOnlyList<string> args)
    {
        if (args.Count < 1 || !long.TryParse(args[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var id))
        {
            return Fail();
        }

        await EnsureInitAsync().ConfigureAwait(false);
        var exclusions = args.Count > 1 ? args[1] : string.Empty;
        var allUdp = args.Count > 2 && IsOn(args[2]);
        var useGlobalProxy = args.Count > 4 && IsOn(args[4]);
        if (exclusions.Length == 0 && !allUdp && !useGlobalProxy)
        {
            await _store.RemoveRoutingSettingsAsync(id).ConfigureAwait(false);
        }
        else
        {
            var mode = useGlobalProxy ? "full" : "split";
            await _store.SetRoutingSettingsAsync(new RoutingSettings(id, exclusions, allUdp, mode, useGlobalProxy)).ConfigureAwait(false);
        }

        PushSnapshot();
        return Ok();
    }

    // Export selection from OpExportBundle's arg0 json; all arrays optional. RoutingRules maps a routing list
    // name to the rule tokens to keep; an absent list keeps all its rules.
    private sealed record BundleSelection(
        string[]? Profiles,
        string[]? Configs,
        string[]? RoutingLists,
        Dictionary<string, string[]>? RoutingRules);

    // Packs the picked configs, routing lists and profiles into a portable bundle.
    private async Task<IpcAck> ExportBundleAsync(IReadOnlyList<string> args)
    {
        if (args.Count < 1 || string.IsNullOrWhiteSpace(args[0]))
        {
            return Fail();
        }

        var selection = ParseSelection(args[0]);
        if (selection is null)
        {
            return new IpcAck(false, IpcMessage.Key("Agent_ExportSelectionParseFailed"));
        }

        await EnsureInitAsync().ConfigureAwait(false);

        var configNames = new HashSet<string>(selection.Configs ?? [], StringComparer.Ordinal);
        var routingNames = new HashSet<string>(selection.RoutingLists ?? [], StringComparer.Ordinal);
        var profileNames = new HashSet<string>(selection.Profiles ?? [], StringComparer.Ordinal);

        // Профиль тянет за собой свою конфигурацию и свой список маршрутизации.
        foreach (var profile in profileNames)
        {
            if (_profiles.TryGetValue(profile, out var boundConfig) && boundConfig.Length > 0)
            {
                configNames.Add(boundConfig);
            }

            if (_routing.TryGetValue(profile, out var raw)
                && ParseRouting(raw).ListId is { } boundId
                && await _store.GetRoutingListAsync(boundId).ConfigureAwait(false) is { } boundList)
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
            if (!_configs.TryGetValue(name, out var text))
            {
                continue;
            }

            var transport = await _store.GetConfigTransportAsync(name).ConfigureAwait(false);
            configBlocks.Add(new PortableBundle.ConfigBlock(
                name,
                text,
                transport is null
                    ? null
                    : new PortableBundle.TransportBlock(
                        transport.UseWebSocket,
                        transport.WebSocketHost,
                        transport.WebSocketPort,
                        transport.Mtu,
                        transport.UseIpv6),
                null));
        }

        var routingBlocks = new List<PortableBundle.RoutingBlock>();
        if (routingNames.Count > 0)
        {
            var all = await _store.ListRoutingListsAsync().ConfigureAwait(false);
            foreach (var name in routingNames)
            {
                var list = all.FirstOrDefault(l => string.Equals(l.Name, name, StringComparison.Ordinal));
                if (list is null)
                {
                    continue;
                }

                // Role-tagged tokens: bare ones would re-import every rule as Proxy.
                var rules = list.Rules.Select(GeoConfigurator.FormatWithRole).ToList();
                if (selection.RoutingRules is not null && selection.RoutingRules.TryGetValue(name, out var kept))
                {
                    var keep = new HashSet<string>(kept, StringComparer.Ordinal);
                    rules = rules.Where(keep.Contains).ToList();
                }

                var settings = await _store.GetRoutingSettingsAsync(list.Id).ConfigureAwait(false);
                routingBlocks.Add(new PortableBundle.RoutingBlock(
                    name,
                    rules,
                    settings is null ? null : new PortableBundle.RoutingSettingsBlock(settings.Exclusions, settings.AllUdp)));
            }
        }

        var profileBlocks = new List<PortableBundle.ProfileBlock>();
        foreach (var name in profileNames)
        {
            // Профиль без конфигурации не экспортируется.
            if (!_profiles.TryGetValue(name, out var config) || config.Length == 0)
            {
                continue;
            }

            var routingName = default(string);
            var useRouting = false;
            if (_routing.TryGetValue(name, out var raw))
            {
                var parsed = ParseRouting(raw);
                useRouting = parsed.UseRouting;
                if (parsed.ListId is { } id && await _store.GetRoutingListAsync(id).ConfigureAwait(false) is { } list)
                {
                    routingName = list.Name;
                }
            }

            profileBlocks.Add(new PortableBundle.ProfileBlock(name, config, routingName, useRouting));
        }

        var bundle = new PortableBundle.Bundle(
            PortableBundle.FormatTag,
            PortableBundle.CurrentVersion,
            configBlocks,
            routingBlocks,
            profileBlocks);
        _log.Info("agent", $"exported bundle: {configBlocks.Count} configs, {routingBlocks.Count} routing lists, {profileBlocks.Count} profiles");
        return new IpcAck(true, PortableBundle.Serialize(bundle));
    }

    // Recreates the bundle's configs, routing lists and profiles; policy picks what to do on a name clash.
    private async Task<IpcAck> ImportBundleAsync(IReadOnlyList<string> args)
    {
        if (args.Count < 1 || string.IsNullOrWhiteSpace(args[0]))
        {
            return Fail();
        }

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

        await EnsureInitAsync().ConfigureAwait(false);
        try
        {
            await EnsureGeoFilesAsync().ConfigureAwait(false);
        }
        catch (Exception)
        {
        }

        var existingLists = (await _store.ListRoutingListsAsync().ConfigureAwait(false))
            .GroupBy(l => l.Name, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        var configNames = new HashSet<string>(_configs.Keys, StringComparer.Ordinal);
        var profileNames = new HashSet<string>(_profiles.Keys, StringComparer.Ordinal);
        var listNames = new HashSet<string>(existingLists.Keys, StringComparer.Ordinal);

        var configMap = new Dictionary<string, string>(StringComparer.Ordinal);
        var routingMap = new Dictionary<string, long>(StringComparer.Ordinal);
        var renames = new List<string>();

        foreach (var block in bundle.Configs)
        {
            if (_configs.ContainsKey(block.Name) && policy != "new")
            {
                if (policy != "skip")
                {
                    _configs[block.Name] = block.ConfigText;
                    await ApplyTransportAsync(block.Name, block.Transport).ConfigureAwait(false);
                }

                configMap[block.Name] = block.Name;
                continue;
            }

            var finalName = FreeName(block.Name, configNames, "Конфигурация");
            configNames.Add(finalName);
            if (!string.Equals(finalName, block.Name, StringComparison.Ordinal))
            {
                renames.Add($"«{block.Name}» → «{finalName}»");
            }

            _configs[finalName] = block.ConfigText;
            await ApplyTransportAsync(finalName, block.Transport).ConfigureAwait(false);
            configMap[block.Name] = finalName;
        }

        foreach (var block in bundle.RoutingLists)
        {
            if (existingLists.TryGetValue(block.Name, out var existing) && policy != "new")
            {
                if (policy != "skip")
                {
                    var merged = policy == "merge"
                        ? existing.Rules.Select(GeoConfigurator.FormatWithRole).Concat(block.Rules).Distinct(StringComparer.Ordinal).ToList()
                        : block.Rules.ToList();
                    await _geo.ApplyToRoutingListAsync(existing.Id, existing.Name, merged).ConfigureAwait(false);
                    await ApplyRoutingSettingsAsync(existing.Id, block.Settings).ConfigureAwait(false);
                }

                routingMap[block.Name] = existing.Id;
                continue;
            }

            var finalName = FreeName(block.Name, listNames, "Список");
            listNames.Add(finalName);
            if (!string.Equals(finalName, block.Name, StringComparison.Ordinal))
            {
                renames.Add($"«{block.Name}» → «{finalName}»");
            }

            var newId = await _geo.ApplyToRoutingListAsync(0, finalName, block.Rules).ConfigureAwait(false);
            await ApplyRoutingSettingsAsync(newId, block.Settings).ConfigureAwait(false);
            routingMap[block.Name] = newId;
        }

        var importedProfiles = 0;
        foreach (var block in bundle.Profiles)
        {
            var config = block.Config is not null && configMap.TryGetValue(block.Config, out var mapped)
                ? mapped
                : string.Empty;

            // Профиль без конфигурации пропускается.
            if (config.Length == 0)
            {
                continue;
            }

            var routingId = block.RoutingList is not null && routingMap.TryGetValue(block.RoutingList, out var rid)
                ? rid
                : default(long?);
            importedProfiles++;

            if (_profiles.ContainsKey(block.Name) && policy != "new")
            {
                if (policy != "skip")
                {
                    _profiles[block.Name] = config;
                    BindRouting(block.Name, routingId, block.UseRouting);
                }

                continue;
            }

            var finalName = FreeName(block.Name, profileNames, "Профиль");
            profileNames.Add(finalName);
            if (!string.Equals(finalName, block.Name, StringComparison.Ordinal))
            {
                renames.Add($"«{block.Name}» → «{finalName}»");
            }

            _profiles[finalName] = config;
            BindRouting(finalName, routingId, block.UseRouting);
        }

        Save();
        await RefreshRoutingSummariesAsync().ConfigureAwait(false);
        PushSnapshot();
        _log.Info("agent", $"imported bundle: {bundle.Configs.Count} configs, {bundle.RoutingLists.Count} routing lists, {importedProfiles} profiles");

        if (renames.Count == 0)
        {
            return new IpcAck(true, IpcMessage.Key(
                "Agent_BundleImported",
                bundle.Configs.Count,
                bundle.RoutingLists.Count,
                importedProfiles));
        }

        if (renames.Count <= 5)
        {
            return new IpcAck(true, IpcMessage.Key(
                "Agent_BundleImportedRenamed",
                bundle.Configs.Count,
                bundle.RoutingLists.Count,
                importedProfiles,
                string.Join(", ", renames)));
        }

        return new IpcAck(true, IpcMessage.Key(
            "Agent_BundleImportedRenamedMany",
            bundle.Configs.Count,
            bundle.RoutingLists.Count,
            importedProfiles));
    }

    // Подбирает свободное имя импортируемому блоку; пустое заменяет запасным.
    private static string FreeName(string desired, HashSet<string> taken, string fallback)
        => UniqueName.ResolveParen(string.IsNullOrWhiteSpace(desired) ? fallback : desired, taken);

    private static BundleSelection? ParseSelection(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<BundleSelection>(
                json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch (JsonException)
        {
            return null;
        }
    }

    // Stores the bundle's transport for a config; the websocket carrier stays off on Android.
    private async Task ApplyTransportAsync(string config, PortableBundle.TransportBlock? transport)
    {
        if (transport is null)
        {
            return;
        }

        await _store.SetConfigTransportAsync(
            new ConfigTransport(config, false, transport.Host, transport.Port, transport.Mtu, transport.UseIpv6)).ConfigureAwait(false);
    }

    private async Task ApplyRoutingSettingsAsync(long listId, PortableBundle.RoutingSettingsBlock? settings)
    {
        if (settings is null)
        {
            return;
        }

        await _store.SetRoutingSettingsAsync(
            new RoutingSettings(listId, settings.Exclusions, settings.AllUdp, "split")).ConfigureAwait(false);
    }

    // Points a profile at a routing list, or clears the binding.
    private void BindRouting(string profile, long? listId, bool useRouting)
    {
        if (listId is null)
        {
            _routing.Remove(profile);
            return;
        }

        _routing[profile] = $"{listId.Value.ToString(CultureInfo.InvariantCulture)}|{(useRouting ? "1" : "0")}";
    }

    // Stores a config's tunnel MTU and IPv6 opt-in; both reach the tunnel builder on the next connect. The
    // websocket carrier has no Android engine behind it and is refused rather than saved as a dead setting.
    private async Task<IpcAck> SetWebSocketAsync(IReadOnlyList<string> args)
    {
        if (args.Count < 3 || !_configs.ContainsKey(args[0]))
        {
            return Fail();
        }

        if (IsOn(args[1]))
        {
            return new IpcAck(false, Loc.Instance.Get("Android_WebSocketUnsupported"));
        }

        if (ParseRange(args[2], 1, 65535) is not { } port)
        {
            return new IpcAck(false, Loc.Instance.Get("Transport_InvalidPort"));
        }

        var mtuText = args.Count > 4 ? args[4].Trim() : string.Empty;
        if ((mtuText.Length == 0 ? DefaultMtu : ParseRange(mtuText, 576, 1500)) is not { } mtu)
        {
            return new IpcAck(false, Loc.Instance.Get("Transport_InvalidMtu"));
        }

        await EnsureInitAsync().ConfigureAwait(false);
        var previous = await _store.GetConfigTransportAsync(args[0]).ConfigureAwait(false);
        var useIpv6 = args.Count > 5 ? IsOn(args[5]) : previous?.UseIpv6 ?? false;
        var host = args.Count > 3 ? args[3].Trim() : string.Empty;
        await _store.SetConfigTransportAsync(new ConfigTransport(args[0], false, host, port, mtu, useIpv6)).ConfigureAwait(false);
        await RefreshTransportsAsync().ConfigureAwait(false);
        PushSnapshot();
        return Ok();
    }

    private static int? ParseRange(string value, int min, int max) =>
        int.TryParse(value.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
        && parsed >= min && parsed <= max
            ? parsed
            : null;

    private async Task RefreshTransportsAsync()
    {
        var transports = new Dictionary<string, ConfigTransport>(StringComparer.Ordinal);
        foreach (var name in _configs.Keys.ToList())
        {
            if (await _store.GetConfigTransportAsync(name).ConfigureAwait(false) is { } transport)
            {
                transports[name] = transport;
            }
        }

        _transports = transports;
    }

    // Assigns or clears a profile's routing list. Args: profile, list id (or "none"), "on"/"off".
    private IpcAck AssignRouting(IReadOnlyList<string> args)
    {
        if (args.Count < 1)
        {
            return Fail();
        }

        var profile = args[0];
        var listArg = args.Count > 1 ? args[1] : "none";
        var on = args.Count > 2 && IsOn(args[2]);
        if (string.Equals(listArg, "none", StringComparison.OrdinalIgnoreCase)
            || !long.TryParse(listArg, NumberStyles.Integer, CultureInfo.InvariantCulture, out var listId))
        {
            _routing.Remove(profile);
        }
        else
        {
            _routing[profile] = $"{listId.ToString(CultureInfo.InvariantCulture)}|{(on ? "1" : "0")}";
        }

        Save();
        PushSnapshot();
        if (_active && string.Equals(_boundTarget, profile, StringComparison.Ordinal))
        {
            _ = SetConnectionAsync("connect");
        }

        return Ok();
    }

    // The rules the session routes by. Addresses stay ranges and names stay names: a name resolved to a fresh
    // address keeps its verdict, which a set of host routes fixed at connect never could.
    private async Task<GeoRoutingPlan> BuildPlanAsync(string? profile)
    {
        if (profile is null || !_routing.TryGetValue(profile, out var raw))
        {
            return GeoRoutingPlan.Full;
        }

        var (listId, useRouting) = ParseRouting(raw);
        if (!useRouting || listId is null)
        {
            return GeoRoutingPlan.Full;
        }

        await EnsureInitAsync().ConfigureAwait(false);
        var list = await _store.GetRoutingListAsync(listId.Value).ConfigureAwait(false);
        if (list is null)
        {
            return GeoRoutingPlan.Full;
        }

        var settings = await _store.GetRoutingSettingsAsync(listId.Value).ConfigureAwait(false);
        var directRoutes = new List<string>(list.DirectRoutes);
        var directDomains = new List<GeoDomain>(list.DirectDomains);
        SplitExclusions(settings?.Exclusions, directRoutes, directDomains);

        var plan = new GeoRoutingPlan(
            list.Routes,
            directRoutes,
            list.BlockRoutes,
            list.Domains,
            directDomains,
            list.BlockDomains,
            settings is { UseGlobalProxy: true },
            settings is { AllUdp: true },
            _routeTtl);

        // A list that decides nothing would leave every destination off the tunnel; the whole tunnel is the safer read.
        if (!plan.HasRules)
        {
            _log.Warn("geo", $"routing '{profile}': the list decides nothing, running the full tunnel instead");
            return GeoRoutingPlan.Full;
        }

        _log.Info("geo", $"routing '{profile}': {plan.ProxyRoutes.Count}/{plan.DirectRoutes.Count}/{plan.BlockRoutes.Count} ranges, "
            + $"{plan.ProxyDomains.Count}/{plan.DirectDomains.Count}/{plan.BlockDomains.Count} names, "
            + (plan.FullTunnel ? "full tunnel" : "split")
            + (plan.AllUdp ? ", all udp tunneled" : string.Empty));
        return plan;
    }

    // Splits the free-text exclusions into what the router classifies: an address becomes a direct range, anything
    // else a direct name.
    private static void SplitExclusions(string? text, List<string> routes, List<GeoDomain> domains)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        var separators = new[] { ',', ';', '\n', '\r', ' ', '\t' };
        foreach (var token in text.Split(separators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var slash = token.IndexOf('/');
            var head = slash < 0 ? token : token[..slash];
            if (System.Net.IPAddress.TryParse(head, out _))
            {
                routes.Add(token);
            }
            else
            {
                domains.Add(new GeoDomain(GeoDomainKind.Domain, token));
            }
        }
    }

    private static (long? ListId, bool UseRouting) ParseRouting(string raw)
    {
        var bar = raw.IndexOf('|');
        var idPart = bar < 0 ? raw : raw[..bar];
        var useRouting = bar >= 0 && bar + 1 < raw.Length && raw[(bar + 1)..] == "1";
        return long.TryParse(idPart, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id) ? (id, useRouting) : (null, false);
    }

    private static bool IsOn(string value) =>
        string.Equals(value, "on", StringComparison.OrdinalIgnoreCase)
        || string.Equals(value, "1", StringComparison.Ordinal)
        || string.Equals(value, "true", StringComparison.OrdinalIgnoreCase)
        || string.Equals(value, "yes", StringComparison.OrdinalIgnoreCase);

    private void Load()
    {
        try
        {
            if (!System.IO.File.Exists(_storePath))
            {
                return;
            }

            using var document = JsonDocument.Parse(System.IO.File.ReadAllText(_storePath));
            LoadMap(document.RootElement, "Configs", _configs);
            LoadMap(document.RootElement, "Profiles", _profiles);
            LoadMap(document.RootElement, "Routing", _routing);
            if (document.RootElement.TryGetProperty("LogLevel", out var level) && level.ValueKind == JsonValueKind.String)
            {
                _logLevel = KnownLogLevel(level.GetString() ?? "info");
            }

            if (document.RootElement.TryGetProperty("RouteLog", out var route))
            {
                _routeLog = route.ValueKind == JsonValueKind.True;
            }

            if (document.RootElement.TryGetProperty("RouteTtl", out var ttl)
                && ttl.ValueKind == JsonValueKind.Number
                && SettingKeys.TryParseRouteTtl(ttl.GetRawText(), out var seconds))
            {
                _routeTtl = seconds;
            }
        }
        catch (Exception)
        {
        }
    }

    private static void LoadMap(JsonElement root, string name, Dictionary<string, string> into)
    {
        if (!root.TryGetProperty(name, out var map) || map.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        foreach (var entry in map.EnumerateObject())
        {
            if (entry.Value.ValueKind == JsonValueKind.String)
            {
                into[entry.Name] = entry.Value.GetString() ?? string.Empty;
            }
        }
    }

    private void Save()
    {
        try
        {
            var builder = new System.Text.StringBuilder();
            builder.Append("{\"Configs\":");
            AppendMap(builder, _configs);
            builder.Append(",\"Profiles\":");
            AppendMap(builder, _profiles);
            builder.Append(",\"Routing\":");
            AppendMap(builder, _routing);
            builder.Append(",\"LogLevel\":").Append(JsonSerializer.Serialize(_logLevel));
            builder.Append(",\"RouteLog\":").Append(_routeLog ? "true" : "false");
            builder.Append(",\"RouteTtl\":").Append(_routeTtl);
            builder.Append('}');
            System.IO.File.WriteAllText(_storePath, builder.ToString());
        }
        catch (Exception)
        {
        }
    }

    private static void AppendMap(System.Text.StringBuilder builder, Dictionary<string, string> map)
    {
        builder.Append('{');
        var first = true;
        foreach (var entry in map)
        {
            if (!first)
            {
                builder.Append(',');
            }

            first = false;
            builder.Append(JsonSerializer.Serialize(entry.Key)).Append(':').Append(JsonSerializer.Serialize(entry.Value));
        }

        builder.Append('}');
    }

    // Renames a profile / config key in place, carrying its value, retargeting the selection and binding, and (for
    // a config) repointing the profiles bound to it. Refuses a name already taken in the same map.
    private IpcAck RenameEntry(Dictionary<string, string> map, IReadOnlyList<string> args, bool rebindConfig)
    {
        if (args.Count < 2)
        {
            return Fail();
        }

        var oldName = args[0];
        var newName = args[1];
        if (string.Equals(oldName, newName, StringComparison.Ordinal))
        {
            return Ok();
        }

        if (!map.TryGetValue(oldName, out var value))
        {
            return Fail();
        }

        if (map.ContainsKey(newName))
        {
            return new IpcAck(false, Loc.Instance.Get("Agent_NameTaken", newName));
        }

        map.Remove(oldName);
        map[newName] = value;

        if (rebindConfig)
        {
            foreach (var profile in _profiles.Keys.ToList())
            {
                if (string.Equals(_profiles[profile], oldName, StringComparison.Ordinal))
                {
                    _profiles[profile] = newName;
                }
            }
        }
        else
        {
            RebindProfileState(oldName, newName);
            // Selection and the live latch name a profile: a config of the same name must not move them.
            RetargetSelection(oldName, newName);
        }

        Save();
        PushSnapshot();
        return Ok();
    }

    // Carries a profile's routing assignment across a rename.
    private void RebindProfileState(string oldName, string newName)
    {
        if (_routing.Remove(oldName, out var routing))
        {
            _routing[newName] = routing;
        }
    }

    private void RetargetSelection(string oldName, string newName)
    {
        if (string.Equals(_selectedTarget, oldName, StringComparison.Ordinal))
        {
            _selectedTarget = newName;
        }

        if (string.Equals(_boundTarget, oldName, StringComparison.Ordinal))
        {
            _boundTarget = newName;
        }
    }

    // The include app set for a profile from its assigned routing list's app:pkg rules; ("off", []) when none.
    private async Task<(string Mode, string[] Packages)> ResolveAppSplitFromRoutingAsync(string? profile)
    {
        if (profile is null || !_routing.TryGetValue(profile, out var raw))
        {
            return ("off", []);
        }

        var (listId, useRouting) = ParseRouting(raw);
        if (!useRouting || listId is null)
        {
            return ("off", []);
        }

        await EnsureInitAsync().ConfigureAwait(false);
        var settings = await _store.GetRoutingSettingsAsync(listId.Value).ConfigureAwait(false);
        if (settings is { UseGlobalProxy: true })
        {
            return ("off", []);
        }

        var list = await _store.GetRoutingListAsync(listId.Value).ConfigureAwait(false);
        if (list?.Apps is not { Count: > 0 })
        {
            return ("off", []);
        }

        const string prefix = "pkg=";
        var packages = list.Apps
            .Where(a => a.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .Select(a => a[prefix.Length..].Trim())
            .Where(p => p.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        return packages.Length > 0 ? ("include", packages) : ("off", []);
    }

    // Reads a window of one log table for the in-app viewer, newest first.
    private async Task<IpcAck> ReadLogAsync(IReadOnlyList<string> args)
    {
        if (args.Count < 1 || !IsKnownLogTable(args[0]))
        {
            return Fail();
        }

        await EnsureInitAsync().ConfigureAwait(false);
        var table = args[0];
        var limit = args.Count > 1 && int.TryParse(args[1], out var l) ? Math.Clamp(l, 1, 2000) : 400;
        var beforeId = args.Count > 2 && long.TryParse(args[2], out var b) && b > 0 ? (long?)b : null;
        var minLevelId = table == SqliteLogStore.AgentTable && args.Count > 3 ? AndroidAgentLog.MinId(args[3]) : null;
        var search = args.Count > 4 && args[4].Length > 0 ? args[4] : null;

        var page = await _log.QueryAsync(table, beforeId, limit, minLevelId, search).ConfigureAwait(false);
        var lines = page.Rows.Select(AndroidAgentLog.Render).ToList();
        var firstId = page.Rows.Count > 0 ? page.Rows[^1].Id : 0L;
        var matchCount = search is null ? 0 : await _log.CountAsync(table, minLevelId, search).ConfigureAwait(false);

        return new IpcAck(true, JsonSerializer.Serialize(new
        {
            lines,
            firstId,
            hasOlder = page.HasOlder,
            matchCount,
        }));
    }

    // Clears one log table.
    private async Task<IpcAck> ClearLogAsync(IReadOnlyList<string> args)
    {
        if (args.Count < 1 || !IsKnownLogTable(args[0]))
        {
            return Fail();
        }

        await EnsureInitAsync().ConfigureAwait(false);
        await _log.ClearAsync(args[0]).ConfigureAwait(false);
        return Ok();
    }

    // Renders a whole log table to text for the UI to save.
    private async Task<IpcAck> ExportLogAsync(IReadOnlyList<string> args)
    {
        if (args.Count < 1 || !IsKnownLogTable(args[0]))
        {
            return Fail();
        }

        await EnsureInitAsync().ConfigureAwait(false);
        var text = await _log.RenderAllAsync(args[0]).ConfigureAwait(false);
        return new IpcAck(true, text);
    }

    // Sets a named agent setting; handles the log capture level and the routing-log switch.
    private IpcAck SetSetting(IReadOnlyList<string> args)
    {
        if (args.Count < 2)
        {
            return Fail();
        }

        switch (args[0])
        {
            case "log-level":
                _logLevel = KnownLogLevel(args[1]);
                _log.SetCaptureLevel(_logLevel);
                Save();
                PushSnapshot();
                return Ok();
            case "route-log":
                _routeLog = IsOn(args[1]);
                _log.SetRouteLog(_routeLog);
                Save();
                PushSnapshot();
                return Ok();
            case SettingKeys.RouteTtl:
                if (!SettingKeys.TryParseRouteTtl(args[1], out var ttl))
                {
                    return Fail();
                }

                _routeTtl = ttl;
                Save();
                PushSnapshot();
                return Ok();
            default:
                return Ok();
        }
    }

    // Records a UI diagnostic line in the agent log.
    private IpcAck LogClient(IReadOnlyList<string> args)
    {
        if (args.Count > 0 && args[0].Length > 0)
        {
            _log.Warn("ui", args[0]);
        }

        return Ok();
    }

    // Renders the configuration the tunnel runs on, or would run on at the next connect.
    private async Task<IpcAck> GetRuntimeConfigAsync()
    {
        await EnsureInitAsync().ConfigureAwait(false);
        var target = _selectedTarget;
        var configName = target is not null && _profiles.TryGetValue(target, out var bound) && bound.Length > 0 ? bound : target;
        var report = new System.Text.StringBuilder();
        report.Append("target       : ").Append(target ?? "(none)").Append('\n');
        report.Append("config       : ").Append(configName ?? "(none)").Append('\n');
        report.Append("status       : ").Append(_boundStatus).Append('\n');
        report.Append("active       : ").Append(_active ? "yes" : "no").Append('\n');
        if (configName is not null && _configs.TryGetValue(configName, out var configText))
        {
            report.Append("endpoint     : ").Append(WgConfigEditor.GetEndpoint(configText) ?? "(none)").Append('\n');
        }

        var (appMode, appPkgs) = await ResolveAppSplitFromRoutingAsync(target);
        report.Append("app split    : ").Append(appMode);
        if (appMode != "off" && appPkgs is { Length: > 0 })
        {
            report.Append(" (").Append(appPkgs.Length).Append(" apps)");
        }

        report.Append('\n');
        AppendRoutingReport(report, target);
        report.Append("log level    : ").Append(_logLevel).Append('\n');
        report.Append("route log    : ").Append(_routeLog ? "on" : "off").Append('\n');
        return new IpcAck(true, report.ToString());
    }

    // Appends the target's routing-list assignment and its rule counts to the runtime report.
    private void AppendRoutingReport(System.Text.StringBuilder report, string? target)
    {
        if (target is null || !_routing.TryGetValue(target, out var raw))
        {
            report.Append("routing list : (none)\n");
            return;
        }

        var (listId, useRouting) = ParseRouting(raw);
        if (listId is null)
        {
            report.Append("routing list : (none)\n");
            return;
        }

        var summary = _routingSummaries.FirstOrDefault(r => r.Id == listId.Value);
        var name = summary?.Name ?? listId.Value.ToString(CultureInfo.InvariantCulture);
        report.Append("routing list : ").Append(name).Append(useRouting ? " (on)" : " (off)").Append('\n');
        report.Append("  geoip routes   : ").Append(summary?.RouteCount ?? 0).Append('\n');
        report.Append("  geosite domains: ").Append(summary?.DomainCount ?? 0).Append('\n');
    }

    // Returns the routing list's own rules; on Android a verdict lives in the tun's route table, not in a cache.
    private async Task<IpcAck> GetCacheEntriesAsync()
    {
        await EnsureInitAsync().ConfigureAwait(false);
        var rows = new List<object>();
        var target = _selectedTarget;
        if (target is not null && _routing.TryGetValue(target, out var raw))
        {
            var (listId, _) = ParseRouting(raw);
            if (listId is not null)
            {
                var list = await _store.GetRoutingListAsync(listId.Value).ConfigureAwait(false);
                if (list is not null)
                {
                    foreach (var route in list.Routes)
                    {
                        rows.Add(new { kind = "proxy", key = route, value = "geoip" });
                    }

                    if (list.Domains is not null)
                    {
                        foreach (var domain in list.Domains)
                        {
                            rows.Add(new { kind = "domain", key = domain.Value, value = domain.Kind.ToString().ToLowerInvariant() });
                        }
                    }
                }
            }
        }

        return Rows(rows);
    }

    private static IpcAck Rows(List<object> rows)
    {
        const int cap = 1000;
        var total = rows.Count;
        var capped = total > cap;
        var entries = capped ? rows.Take(cap).ToList() : rows;
        return new IpcAck(true, JsonSerializer.Serialize(new { total, capped, entries }));
    }

    private static bool IsKnownLogTable(string name) => name is SqliteLogStore.AgentTable or SqliteLogStore.RoutesTable;

    private static string KnownLogLevel(string token)
    {
        return token switch
        {
            "none" or "trace" or "debug" or "info" or "warning" or "error" => token,
            _ => "info",
        };
    }

    private static IpcAck Ok() => new(true, string.Empty);

    private static IpcAck Fail() => new(false, string.Empty);
}
