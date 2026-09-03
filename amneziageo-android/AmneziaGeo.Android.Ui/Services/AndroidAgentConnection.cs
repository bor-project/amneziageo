using System.Collections.Concurrent;
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
using AmneziaGeo.Routing;
using AmneziaGeo.Ui.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace AmneziaGeo.Android.Ui.Services;

/// <summary>
/// In-process agent for the Android head: persists configs, projects status snapshots, and drives
/// the tunnel through <see cref="GeoVpnService"/>.
/// </summary>
internal sealed class AndroidAgentConnection : IAgentConnection
{
    private readonly Dictionary<string, string> _configs = new(StringComparer.Ordinal);

    // The order the list is shown in; the store file keeps it as the order its config map is written in.
    private List<string> _order = [];
    private readonly string _storePath;

    // Age past which the tunnel's own snapshot of what it carries is no longer an answer about what runs now.
    private const int SessionWindowSeconds = 60;

    // How long a probe handed to the tunnel is waited for, and how often its result is looked for.
    private const int ProbeWaitMs = 40_000;
    private const int ProbePollMs = 250;

    // Leaves the start-up rush to the interface before the first geo check goes out.
    private const int GeoStartDelaySeconds = 5;
    private const int GeoTickSeconds = 60;
    private const int MinGeoIntervalHours = 1;
    private const int MaxGeoIntervalHours = 24 * 7;

    // Seconds between two looks at whose subscription interval has run out.
    private const int SubscriptionTickSeconds = 900;
    private const int DefaultSubscriptionIntervalHours = 12;
    private static readonly string AppVersion = ReadAppVersion();

    private readonly SqliteStateStore _store;
    private readonly GeoConfigurator _geo;
    private readonly GeoFileUpdater _geoUpdater;
    private readonly AndroidGeoFileStore _geoFiles;
    private readonly GeoUpdateChecker _geoChecker;
    private readonly AndroidAgentLog _log;
    private readonly GeoHttp _geoHttp;
    private readonly HttpClient _httpClient = new();
    private readonly AndroidUpdater _updater;
    // Null until the store has been read: the snapshot says "not loaded yet", not "no lists".
    private IReadOnlyList<RoutingListEntry>? _routingSummaries;
    private IReadOnlyDictionary<string, ConfigTransport> _transports = new Dictionary<string, ConfigTransport>(StringComparer.Ordinal);

    // Which subscription brought which configuration; empty until the store has been read.
    private IReadOnlyDictionary<string, SubscriptionMember> _members = new Dictionary<string, SubscriptionMember>(StringComparer.Ordinal);
    private IReadOnlyList<GeoSource> _geoSources = [];
    private IReadOnlyList<GeoFileMetadata> _geoFileMeta = [];
    private readonly HashSet<string> _updatingSources = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string?> _sourceErrors = new(StringComparer.Ordinal);

    // Per-source download volume while a base is being fetched.
    private readonly ConcurrentDictionary<string, GeoDownload> _sourceProgress = new(StringComparer.Ordinal);
    private Task? _initTask;
    private Task? _geoFilesTask;
    private VpnBridge.Listener? _events;

    private string? _selectedTarget;
    private long? _selectedRoutingList;
    private string? _boundTarget;
    private string _boundStatus = ConnectionStatus.Disconnected;
    private long _handshakeUnix;
    private LinkReading _link = LinkReading.Empty;
    private DateTimeOffset _linkLoggedAt;
    private bool _churnLogged;
    private bool _active;
    private bool _restartRequired;

    // Просил ли пользователь быть подключённым: держится через падения связи и гаснет только по отбою.
    private bool _dialWanted;

    // Сколько ждать остановки туннеля перед подъёмом заново.
    private const int RestartTickMs = 100;
    private const int RestartWaitTicks = 50;
    private bool _connectFailed;
    private string _connectFailReason = string.Empty;
    private string _connectFailDetail = string.Empty;
    private bool _started;
    private bool _disposed;
    private string _logLevel = "error";
    private bool _routeLog;
    private bool _directTcp = true;
    private bool _excludeRoutes = true;
    private int _routeTtl = 300;
    private bool _geoAutoCheck = true;
    private int _geoCheckIntervalHours = 24;
    private bool _subscriptionAutoRefresh = true;
    private int _subscriptionIntervalHours = DefaultSubscriptionIntervalHours;
    private DateTimeOffset _geoCheckedAt;
    private readonly CancellationTokenSource _geoChecks = new();
    private bool _alwaysOn;
    private bool _alwaysOnLockdown;
    private LocalProxyOptions _proxyOptions = new();
    private string _proxyOfferLine = string.Empty;

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
    /// Awaits the store load and republishes the snapshot it filled.
    /// </summary>
    public async Task ReadyAsync()
    {
        await EnsureInitAsync().ConfigureAwait(false);
        PushSnapshot();
    }

    /// <summary>
    /// ctor
    /// </summary>
    public AndroidAgentConnection()
    {
        var dir = Application.Context.FilesDir?.AbsolutePath ?? ".";
        _storePath = System.IO.Path.Combine(dir, "agent.json");
        var geoFiles = new AndroidGeoFileStore(System.IO.Path.Combine(dir, "geo"));
        _geoFiles = geoFiles;
        _store = new SqliteStateStore(System.IO.Path.Combine(dir, "state.db"));
        _geoHttp = new GeoHttp(_httpClient, NullLogger<GeoHttp>.Instance);
        _geoUpdater = new GeoFileUpdater(_store, _geoHttp, geoFiles);
        _geoChecker = new GeoUpdateChecker(_store, _geoHttp, geoFiles);
        _geo = new GeoConfigurator(_store, geoFiles);
        _log = new AndroidAgentLog(System.IO.Path.Combine(dir, "log.db"));
        _updater = new AndroidUpdater(_httpClient, _log, PushSnapshot, AppVersion);
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
        _ = CheckGeoUpdatesAsync(_geoChecks.Token);
        _ = RefreshSubscriptionsAsync(_geoChecks.Token);
    }

    public Task<IpcAck> SendCommandAsync(IpcCommand command) => DispatchAsync(command);

    // Reads the version the package manager reports for this build.
    private static string ReadAppVersion()
    {
        try
        {
            var context = Application.Context;
            var name = context.PackageName;
            var info = name is null ? null : context.PackageManager?.GetPackageInfo(name, 0);
            return info?.VersionName ?? string.Empty;
        }
        catch (Exception ex)
        {
            global::Android.Util.Log.Warn("AndroidAgent", "reading the package version failed: " + ex);
            return string.Empty;
        }
    }

    public Task<IpcAck> SendCommandRawAsync(IpcCommand command) => DispatchAsync(command);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _geoChecks.Cancel();
        MainActivity.Resumed -= SyncTunnelState;
        if (_events is not null)
        {
            Application.Context.UnregisterReceiver(_events);
            _events = null;
        }

        _geoChecks.Dispose();
        _updater.Dispose();
        _geoHttp.Dispose();
        _httpClient.Dispose();
        _log.Dispose();
        if (_started)
        {
            Disconnected?.Invoke();
        }
    }

    // Отбивает конфиг, который движок отверг бы при подъёме туннеля.
    private static IpcAck? RejectBadConfig(string text)
    {
        try
        {
            WgConfigValidator.Validate(text);
            return null;
        }
        catch (WgConfigFormatException ex)
        {
            return new IpcAck(false, ex.UnknownKey
                ? IpcMessage.Key("Agent_ConfigUnsupportedKey", ex.Offender)
                : IpcMessage.Key("Agent_ConfigRejected", ex.Message));
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

                var rejected = RejectBadConfig(args[1]);
                if (rejected is not null)
                {
                    return rejected;
                }

                // Import creates; replacing the text of an existing configuration is edit-config.
                if (command.Op == IpcContract.OpImportConfig && _configs.ContainsKey(args[0]))
                {
                    return new IpcAck(false, IpcMessage.Key("Agent_ConfigNameTaken", args[0]));
                }

                _configs[args[0]] = args[1];

                // A first import is ready to dial: it takes the selection while there is none.
                if (command.Op == IpcContract.OpImportConfig && _selectedTarget is null)
                {
                    _selectedTarget = args[0];
                }

                Save();
                PushSnapshot();
                return Ok();

            // One head, one process: presence needs no announcing here.
            case IpcContract.OpAttachUi:
                return Ok();

            case IpcContract.OpAddConfig:
                return await AddConfigAsync(args).ConfigureAwait(false);

            case IpcContract.OpCopyConfig:
                return await CopyConfigAsync(args).ConfigureAwait(false);

            case IpcContract.OpAddSubscription:
                return await AddSubscriptionAsync(args).ConfigureAwait(false);

            case IpcContract.OpListSubscriptions:
                return await ListSubscriptionsAsync().ConfigureAwait(false);

            case IpcContract.OpRefreshSubscription:
                return await RefreshSubscriptionAsync(args).ConfigureAwait(false);

            case IpcContract.OpRemoveSubscription:
                return await RemoveSubscriptionAsync(args).ConfigureAwait(false);

            case IpcContract.OpConfigSubscription:
                return await ConfigSubscriptionAsync(args).ConfigureAwait(false);

            case IpcContract.OpRemoveConfig:
                return await RemoveConfigAsync(args).ConfigureAwait(false);

            case IpcContract.OpGetConfig:
                return args.Count > 0 && _configs.TryGetValue(args[0], out var text)
                    ? new IpcAck(true, text)
                    : Fail();

            case IpcContract.OpSelectConfig:
                return await SelectConfigAsync(args).ConfigureAwait(false);

            case IpcContract.OpSetConnection:
                return await SetConnectionAsync(args.Count > 0 ? args[0] : string.Empty);

            case IpcContract.OpRenameConfig:
                return await RenameConfigAsync(args).ConfigureAwait(false);

            case IpcContract.OpReorderConfigs:
                if (args.Count == 0)
                {
                    return Fail();
                }

                _order = [.. args];
                Save();
                PushSnapshot();
                return Ok();

            case IpcContract.OpAssignRouting:
                return await AssignRoutingAsync(args).ConfigureAwait(false);

            case IpcContract.OpAddSource:
                return await AddSourceAsync(args);

            case IpcContract.OpRemoveSource:
                return await RemoveSourceAsync(args);

            case IpcContract.OpEditSource:
                return await EditSourceAsync(args);

            case IpcContract.OpUpdateSource:
                return await UpdateSourceAsync(args);

            case IpcContract.OpUpdateSources:
            case IpcContract.OpDownloadGeo:
                return await UpdateAllSourcesAsync();

            case IpcContract.OpCheckSource:
                return await CheckSourceAsync(args);

            case IpcContract.OpCheckSources:
                return await CheckSourcesAsync();

            case IpcContract.OpRefreshSources:
                await EnsureInitAsync().ConfigureAwait(false);
                await RefreshGeoSourcesAsync().ConfigureAwait(false);
                PushSnapshot();
                return Ok();

            case IpcContract.OpListLocalSubnets:
                return new IpcAck(true, string.Join('\n', GeoVpnService.LocalSubnets()));

            // Гео-базы разбираются в пуле: разворачивание правил держит вызывающий поток, а он тут UI-шный.
            case IpcContract.OpListGeo:
                return await Task.Run(ListGeoAsync).ConfigureAwait(false);

            case IpcContract.OpGetGeoEntries:
                return await Task.Run(() => GetGeoEntriesAsync(args)).ConfigureAwait(false);

            case IpcContract.OpSaveRoutingList:
                return await Task.Run(() => SaveRoutingListAsync(args)).ConfigureAwait(false);

            case IpcContract.OpGetRoutingList:
                return await Task.Run(() => GetRoutingListAsync(args)).ConfigureAwait(false);

            case IpcContract.OpCountRoutes:
                return await Task.Run(() => CountRoutesAsync(args)).ConfigureAwait(false);

            case IpcContract.OpRemoveRoutingList:
                return await Task.Run(() => RemoveRoutingListAsync(args)).ConfigureAwait(false);

            case IpcContract.OpReorderRoutingLists:
                return await Task.Run(() => ReorderRoutingListsAsync(args)).ConfigureAwait(false);

            case IpcContract.OpGetRoutingSettings:
                return await Task.Run(() => GetRoutingSettingsAsync(args)).ConfigureAwait(false);

            case IpcContract.OpSetRoutingSettings:
                return await Task.Run(() => SetRoutingSettingsAsync(args)).ConfigureAwait(false);

            case IpcContract.OpSetWebSocket:
                return await SetWebSocketAsync(args);

            case IpcContract.OpSetGeo:
                return await SetGeoAsync(args);

            // The tunnel here resolves names through its own trap and routes by list, so neither of these
            // reaches the session: the setting would be stored and never applied.
            case IpcContract.OpSetConfigDns:
            case IpcContract.OpSetConfigExclusions:
                return new IpcAck(false, IpcMessage.Key("Android_PerConfigResolverUnsupported"));

            case IpcContract.OpListProcesses:
                return ListProcesses();

            case IpcContract.OpCollectDiagnostics:
                return await CollectDiagnosticsAsync();

            case IpcContract.OpCheckUpdate:
                return await _updater.CheckAsync(args.Count > 0 && args[0] == "silent", CancellationToken.None).ConfigureAwait(false);

            case IpcContract.OpDownloadUpdate:
            {
                var started = _updater.StartDownload(CancellationToken.None);
                PushSnapshot();
                return started;
            }

            case IpcContract.OpCancelUpdateDownload:
                return _updater.Cancel();

            case IpcContract.OpApplyUpdate:
                return await _updater.InstallAsync(CancellationToken.None).ConfigureAwait(false);

            // The agent owns the download here, so a client report changes nothing.
            case IpcContract.OpReportUpdateDownload:
                return Ok();

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

            case IpcContract.OpGetSessions:
                return GetSessions();

            case IpcContract.OpKnownHosts:
                return await KnownHostsAsync(CancellationToken.None).ConfigureAwait(false);

            case IpcContract.OpCheckChannel:
                return await CheckChannelAsync(args, CancellationToken.None).ConfigureAwait(false);

            case IpcContract.OpCheckServers:
                return await CheckServersAsync(CancellationToken.None).ConfigureAwait(false);

            case IpcContract.OpCheckTarget:
                return await CheckTargetAsync(args, CancellationToken.None).ConfigureAwait(false);

            case IpcContract.OpProbeTarget:
                return await ProbeTargetAsync(args, CancellationToken.None).ConfigureAwait(false);

            case IpcContract.OpExportBundle:
                return await ExportBundleAsync(args);

            case IpcContract.OpImportBundle:
                return await ImportBundleAsync(args);

            case IpcContract.OpOpenVpnSettings:
                return OpenVpnSettings();

            default:
                _log.Warn("agent", $"command '{command.Op}' is not wired in the Android agent");
                return new IpcAck(false, IpcMessage.Key("Android_OpNotWired", command.Op));
        }
    }

    private async Task<IpcAck> SetConnectionAsync(string desired)
    {
        if (desired == "disconnect")
        {
            _dialWanted = false;
            Save();
            _log.Info("agent", "disconnect requested");

            // Stops through a broadcast: a head in the background is barred from starting the service.
            if (VpnBridge.IsRunning(Application.Context))
            {
                VpnBridge.RequestStop(Application.Context);
            }
            else
            {
                OnVpnStateChanged(VpnStage.Disconnected, null);
            }

            return Ok();
        }

        // Two different refusals: nothing to connect to, and a selection whose config is gone.
        if (_selectedTarget is not { Length: > 0 })
        {
            SetConnectFailure(nameof(ConnectFailureReason.NoTargetSelected), string.Empty);
            _log.Error("agent", "connect refused: no configuration selected");
            PushSnapshot();
            return new IpcAck(false, "nothing selected");
        }

        var configName = _selectedTarget;
        if (!_configs.TryGetValue(configName, out var configText))
        {
            SetConnectFailure(nameof(ConnectFailureReason.ConfigMissing), configName);
            _log.Error("agent", $"connect refused: configuration '{configName}' is gone");
            PushSnapshot();
            return new IpcAck(false, $"configuration '{configName}' not found");
        }

        var granted = await EnsureVpnPermissionAsync();
        if (!granted)
        {
            SetConnectFailure(nameof(ConnectFailureReason.PermissionDenied), string.Empty);
            _log.Warn("agent", "connect refused: vpn permission denied");
            PushSnapshot();
            return new IpcAck(false, "vpn permission denied");
        }

        ClearConnectFailure();
        _restartRequired = false;
        _dialWanted = true;
        Save();
        // Reports the connecting stage from the request: the tunnel process speaks only once it is up, and until
        // then a snapshot would pull the card back to disconnected.
        _active = true;
        _boundStatus = ConnectionStatus.Connecting;
        _boundTarget = _selectedTarget;
        PushSnapshot();
        var useRouter = RouterEnabled();

        // Правила разворачиваются в пуле: агент живёт в процессе UI, и план большого списка держит поток.
        var planStarted = System.Environment.TickCount64;
        var (appMode, appPkgs) = await Task.Run(async () =>
        {
            var split = await ResolveAppSplitFromRoutingAsync(useRouter).ConfigureAwait(false);
            VpnBridge.WritePlan(await BuildPlanAsync(useRouter).ConfigureAwait(false));
            return split;
        }).ConfigureAwait(false);

        _log.Info("agent", $"connect requested: config '{_selectedTarget}', app rules {AppRulesLine(appMode, appPkgs.Length)}, "
            + $"plan ready in {System.Environment.TickCount64 - planStarted} ms");
        StartService(GeoVpnService.ActionConnect, configText, _selectedTarget,
            appMode == "off" ? null : appMode, appMode == "off" ? null : appPkgs,
            _transports.GetValueOrDefault(configName), foreground: true, EngineLogLevel(_logLevel), _directTcp,
            _excludeRoutes);
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

    // Движок пишет каждое своё решение через JNI: на диагностическом уровне это заметная доля времени пакета.
    private static int EngineLogLevel(string level)
    {
        return level switch
        {
            "none" => 0,
            "trace" or "debug" => 2,
            _ => 1,
        };
    }

    private static void StartService(string action, string? config, string? name, string? appMode, string[]? appPkgs, ConfigTransport? transport, bool foreground, int engineLog = 1, bool directTcp = true, bool excludeRoutes = false)
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

        intent.PutExtra(GeoVpnService.ExtraEngineLog, engineLog);
        intent.PutExtra(GeoVpnService.ExtraDirectTcp, directTcp);
        intent.PutExtra(GeoVpnService.ExtraExcludeRoutes, excludeRoutes);

        if (transport is not null)
        {
            intent.PutExtra(GeoVpnService.ExtraMtu, transport.Mtu);
            intent.PutExtra(GeoVpnService.ExtraMtuMode, (int)transport.MtuMode);
            intent.PutExtra(GeoVpnService.ExtraIpv6, transport.UseIpv6);
        }

        if (transport is { UseWebSocket: true })
        {
            intent.PutExtra(GeoVpnService.ExtraWsHost, transport.WebSocketHost);
            intent.PutExtra(GeoVpnService.ExtraWsPort, transport.WebSocketPort);
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

        var handshake = intent.GetLongExtra(VpnBridge.ExtraHandshake, -1);
        if (handshake >= 0)
        {
            _handshakeUnix = handshake;
            _link = new LinkReading(
                intent.GetLongExtra(VpnBridge.ExtraRxBits, 0),
                intent.GetLongExtra(VpnBridge.ExtraTxBits, 0),
                intent.GetIntExtra(VpnBridge.ExtraChurn, 0),
                intent.GetIntExtra(VpnBridge.ExtraLoss, LinkHealth.LossUnknown),
                intent.GetIntExtra(VpnBridge.ExtraRtt, -1));
            LogLink(_link);
            PushSnapshot();
            return;
        }

        var stage = intent.GetIntExtra(VpnBridge.ExtraStage, -1);
        if (stage >= 0)
        {
            _alwaysOn = intent.GetBooleanExtra(VpnBridge.ExtraAlwaysOn, false);
            _alwaysOnLockdown = intent.GetBooleanExtra(VpnBridge.ExtraLockdown, false);
            OnVpnStateChanged((VpnStage)stage, intent.GetStringExtra(VpnBridge.ExtraDetail),
                intent.GetStringExtra(VpnBridge.ExtraReason));
        }
    }

    private void OnVpnStateChanged(VpnStage stage, string? detail, string? reason = null)
    {
        // The session name comes back from the tunnel, so a head that started after it still names what runs.
        var session = string.IsNullOrEmpty(detail) ? _selectedTarget : detail;
        switch (stage)
        {
            case VpnStage.Connecting:
                _active = true;
                _restartRequired = false;
                _boundStatus = ConnectionStatus.Connecting;
                _boundTarget = session;
                break;
            case VpnStage.Connected:
                _active = true;
                _boundStatus = ConnectionStatus.Connected;
                _boundTarget = session;
                ClearConnectFailure();
                break;
            case VpnStage.Disconnected:
                _active = false;
                _restartRequired = false;
                _boundStatus = ConnectionStatus.Disconnected;
                _boundTarget = null;
                _handshakeUnix = 0;
                ResetLink();
                ResetAlwaysOn();
                break;
            case VpnStage.Failed:
                _active = false;
                _restartRequired = false;
                _boundStatus = ConnectionStatus.Disconnected;
                _boundTarget = null;
                _handshakeUnix = 0;
                ResetLink();
                ResetAlwaysOn();
                SetConnectFailure(reason ?? nameof(ConnectFailureReason.Unknown), detail ?? string.Empty);
                break;
        }

        LogVpnStage(stage, detail);
        PushSnapshot();
    }

    // Opens the system screen carrying the always-on switch; no application may set always-on for itself.
    private IpcAck OpenVpnSettings()
    {
        try
        {
            var intent = new Intent(global::Android.Provider.Settings.ActionVpnSettings);
            intent.AddFlags(ActivityFlags.NewTask);
            Application.Context.StartActivity(intent);
            return Ok();
        }
        catch (Exception ex)
        {
            _log.Warn("agent", "the system vpn settings did not open: " + ex);
            return new IpcAck(false, "vpn settings unavailable");
        }
    }

    // Only a running tunnel is asked about always-on, so a stopped one leaves no answer behind.
    private void ResetAlwaysOn()
    {
        _alwaysOn = false;
        _alwaysOnLockdown = false;
    }

    // Drops the link view a stopped tunnel left behind.
    private void ResetLink()
    {
        _link = LinkReading.Empty;
        _churnLogged = false;
    }

    // Writes the link to the journal: a line a minute while it runs, and a warning when it starts or stops
    // re-establishing the session.
    private void LogLink(LinkReading reading)
    {
        var churning = LinkHealth.Churning(reading.HandshakesPerMinute);
        if (churning != _churnLogged)
        {
            _churnLogged = churning;
            if (churning)
            {
                // Loud enough to survive the default capture floor: this is the record a transient outage leaves.
                _log.Error("link", $"the session is re-established {reading.HandshakesPerMinute} times a minute, so the link carries almost nothing");
            }
            else
            {
                _log.Info("link", "the session stopped re-establishing");
            }
        }

        var now = DateTimeOffset.UtcNow;
        if (now - _linkLoggedAt < TimeSpan.FromSeconds(60))
        {
            return;
        }

        _linkLoggedAt = now;
        _log.Info("link", $"receives {reading.RxBitsPerSecond / 1000} kbit/s, sends {reading.TxBitsPerSecond / 1000} kbit/s, handshakes {reading.HandshakesPerMinute}/min, loses {LossText(reading.LossPercent)}");
    }

    // The measured share, or a word for a tunnel that has found nothing inside it to answer an echo.
    private static string LossText(int percent)
    {
        return LinkHealth.LossKnown(percent) ? $"{percent}%" : "nothing that answers";
    }

    // Marks the last connect as failed and names its cause for the notice.
    private void SetConnectFailure(string reason, string detail)
    {
        _connectFailed = true;
        _connectFailReason = reason;
        _connectFailDetail = detail;
    }

    private void ClearConnectFailure()
    {
        _connectFailed = false;
        _connectFailReason = string.Empty;
        _connectFailDetail = string.Empty;
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

    // A live tun keeps the routes establish() was given, so an edited list only reaches the tunnel on a reconnect.
    private void MarkRoutingChanged(long listId)
    {
        if (_active && _selectedRoutingList == listId)
        {
            _restartRequired = true;
            _log.Info("agent", "the edited routing list applies on the next connect");
        }
    }

    private void PushSnapshot()
    {
        var configs = OrderedNames().Select(name => Entry(name, _configs[name])).ToList();
        var proxy = VpnBridge.ReadProxyState();
        var proxyAddresses = ProxyAddresses(_proxyOptions.Enabled);
        LogProxyOffer(proxy.Running, proxyAddresses);

        Latest = new StatusSnapshot(
            AgentVersion: AppVersion,
            BoundTarget: _boundTarget,
            Configs: configs,
            RoutingLists: _routingSummaries,
            Active: _active,
            BoundStatus: _boundStatus,
            RestartRequired: _restartRequired,
            SelectedTarget: _selectedTarget ?? string.Empty,
            SelectedRoutingList: _selectedRoutingList,
            Sources: BuildSources(),
            ConnectFailed: _connectFailed,
            ConnectFailReason: _connectFailReason,
            ConnectFailDetail: _connectFailDetail,
            EngineVersion: string.Empty,
            LogLevel: _logLevel,
            RouteLog: _routeLog,
            RouteTtlSeconds: _routeTtl,
            GeoAutoCheck: _geoAutoCheck,
            GeoCheckIntervalHours: _geoCheckIntervalHours,
            SubscriptionAutoRefresh: _subscriptionAutoRefresh,
            SubscriptionRefreshIntervalHours: _subscriptionIntervalHours,
            UpdateUrl: _updater.Url,
            UpdateAvailable: _updater.Available,
            UpdateVersion: _updater.Version,
            UpdateSetupUrl: _updater.SetupUrl,
            UpdateDescription: _updater.Description,
            AllowPrerelease: _updater.AllowPrerelease,
            UpdateSetupSha256: _updater.Sha256,
            UpdateSetupPath: _updater.SetupPath,
            UpdateDownloading: _updater.Downloading,
            UpdateDownloaded: _updater.Downloaded,
            UpdateDownloadPercent: _updater.Percent,
            UpdateDownloadFailed: _updater.Failed,
            UpdateCancelRequested: _updater.CancelRequested,
            UpdateChecking: _updater.Checking,
            UpdateCheckFailed: _updater.CheckFailed,
            UpdateInstalling: _updater.Installing,
            AlwaysOn: _alwaysOn,
            AlwaysOnLockdown: _alwaysOnLockdown,
            ProxyEnabled: _proxyOptions.Enabled,
            ProxySocksPort: _proxyOptions.SocksPort,
            ProxyHttpPort: _proxyOptions.HttpPort,
            ProxyAnonymous: _proxyOptions.AllowAnonymous,
            ProxyCredentials: _proxyOptions.Credentials,
            ProxyRunning: proxy.Running,
            ProxyError: proxy.Error,
            ProxyAddresses: proxyAddresses);

        SnapshotReceived?.Invoke(Latest);
    }

    // Addresses the proxy answers on. Named from the moment it is switched on: the screen that offers it is where
    // the user reads what to point a client at, and here it only listens while the tunnel stands.
    private static IReadOnlyList<string> ProxyAddresses(bool enabled)
    {
        return enabled ? GeoVpnService.ReachableAddresses() : [];
    }

    // Writes where the proxy is offered, and the links behind the answer when it is offered nowhere.
    private void LogProxyOffer(bool running, IReadOnlyList<string> addresses)
    {
        var line = (running ? "up" : "down") + " " + (addresses.Count > 0 ? string.Join(", ", addresses) : "nowhere");
        if (string.Equals(line, _proxyOfferLine, StringComparison.Ordinal))
        {
            return;
        }

        _proxyOfferLine = line;
        global::Android.Util.Log.Info("AmneziaGeo", "proxy offered " + line);
        if (!running || addresses.Count > 0)
        {
            return;
        }

        try
        {
            foreach (var adapter in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
            {
                var found = adapter.GetIPProperties().UnicastAddresses.Select(a => a.Address.ToString());
                global::Android.Util.Log.Info("AmneziaGeo",
                    $"link {adapter.Name} {adapter.NetworkInterfaceType} {adapter.OperationalStatus} {string.Join(" ", found)}");
            }
        }
        catch (Exception ex)
        {
            global::Android.Util.Log.Warn("AmneziaGeo", "listing links failed: " + ex);
        }
    }

    // The names in the order the user set, with anything it does not name after them.
    private List<string> OrderedNames()
    {
        var names = _order.Where(_configs.ContainsKey).ToList();
        names.AddRange(_configs.Keys.Where(name => !names.Contains(name)));
        return names;
    }

    private ConfigEntry Entry(string name, string config)
    {
        var transport = _transports.GetValueOrDefault(name);
        var bound = _active && string.Equals(_boundTarget, name, StringComparison.Ordinal);
        var handshake = bound && _handshakeUnix > 0
            ? HandshakeAge.Step(Math.Max(0, DateTimeOffset.UtcNow.ToUnixTimeSeconds() - _handshakeUnix))
            : -1;
        var reading = bound ? _link : LinkReading.Empty;
        var member = _members.GetValueOrDefault(name);
        return new ConfigEntry(name, WgConfigEditor.GetEndpoint(config) ?? string.Empty, false, StatusFor(name), [],
            WebSocket: transport?.UseWebSocket ?? false,
            WebSocketHost: transport?.WebSocketHost ?? string.Empty,
            WebSocketPort: transport?.WebSocketPort ?? 443,
            Mtu: transport?.Mtu ?? 0,
            UseIpv6: transport?.UseIpv6 ?? false,
            UseRouter: transport?.UseRouter ?? true,
            AllowInbound: transport?.AllowInbound ?? false,
            InboundNetwork: transport?.InboundNetwork ?? false,
            Address: string.Join(", ", WgConfigEditor.GetAddresses(config)),
            HandshakeAgeSeconds: handshake,
            RxBitsPerSecond: reading.RxBitsPerSecond,
            TxBitsPerSecond: reading.TxBitsPerSecond,
            HandshakesPerMinute: reading.HandshakesPerMinute,
            LossPercent: reading.LossPercent,
            RttMs: reading.RttMs,
            Subscription: member?.Subscription ?? string.Empty,
            SubscriptionGone: member is { Present: false },
            ConfigMtu: WgConfigEditor.GetMtu(config),
            MtuMode: transport?.MtuMode ?? MtuMode.Auto,
            ResolvedMtu: MtuPlan.ResolveForLearnedLink(transport, config));
    }

    private string StatusFor(string target)
    {
        return _active && string.Equals(_boundTarget, target, StringComparison.Ordinal)
            ? _boundStatus
            : ConnectionStatus.Disconnected;
    }

    // Initializes the SQLite store and seeds default geo sources once.
    private Task EnsureInitAsync() => _initTask ??= InitAsync();

    private async Task InitAsync()
    {
        await _log.InitializeAsync().ConfigureAwait(false);
        _log.Info("agent", "android agent started");
        await _store.InitializeAsync().ConfigureAwait(false);
        await GeoDefaults.SeedAsync(_store, _geoFiles, null, CancellationToken.None).ConfigureAwait(false);
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
            var volume = _sourceProgress.GetValueOrDefault(source.Name);
            list.Add(new SourceEntry(source.Name, source.Kind, source.Url, updated, meta?.CategoryCount ?? 0, updating,
                updating ? volume.Percent : 0, meta?.UpdateAvailable ?? false, error,
                updating ? volume.Read : 0, updating ? volume.Total : 0));
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
            await _geoUpdater.UpdateAsync(source, new SourceProgress(_sourceProgress, source.Name)).ConfigureAwait(false);
            _sourceErrors.Remove(source.Name);
        }
        catch (Exception ex)
        {
            _sourceErrors[source.Name] = ex.Message;
        }
        finally
        {
            _updatingSources.Remove(source.Name);
            _sourceProgress.TryRemove(source.Name, out _);
        }
    }

    // Пушит снимок, пока идут загрузки: иначе объём в списке источников стоит на месте.
    private async Task ProgressPumpAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromMilliseconds(700), ct).ConfigureAwait(false);
            }
            catch (System.OperationCanceledException)
            {
                return;
            }

            PushSnapshot();
        }
    }

    // Складывает ход загрузки одного источника.
    private sealed class SourceProgress(ConcurrentDictionary<string, GeoDownload> map, string name) : IProgress<GeoDownload>
    {
        public void Report(GeoDownload value) => map[name] = value;
    }

    // Есть ли база на диске.
    private bool HasGeoFile(string name)
    {
        using var stream = _geoFiles.OpenRead(name);
        return stream is not null;
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

    // Pulls the newest rule databases while the interface is up: the app carries no agent in the background,
    // so the schedule runs here, and only over a link that costs the user nothing.
    private async Task CheckGeoUpdatesAsync(CancellationToken ct)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(GeoStartDelaySeconds), ct).ConfigureAwait(false);
            while (!ct.IsCancellationRequested)
            {
                if (_geoAutoCheck
                    && DateTimeOffset.UtcNow - _geoCheckedAt >= TimeSpan.FromHours(_geoCheckIntervalHours)
                    && OnFreeLink())
                {
                    await RunGeoCheckAsync().ConfigureAwait(false);
                }

                await Task.Delay(TimeSpan.FromSeconds(GeoTickSeconds), ct).ConfigureAwait(false);
            }
        }
        catch (System.OperationCanceledException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
        catch (Exception ex)
        {
            _log.Error("geo", "the scheduled check of the rule databases stopped", ex);
        }
    }

    // Asks every source whether its file changed and downloads the ones that did.
    private async Task RunGeoCheckAsync()
    {
        await EnsureInitAsync().ConfigureAwait(false);
        var stale = new List<GeoSource>();
        foreach (var source in _geoSources.ToList())
        {
            if (await CheckOneSourceAsync(source).ConfigureAwait(false) == GeoUpdateChecker.Status.Available)
            {
                stale.Add(source);
            }
        }

        _geoCheckedAt = DateTimeOffset.UtcNow;
        Save();
        if (stale.Count == 0)
        {
            await RefreshGeoSourcesAsync().ConfigureAwait(false);
            PushSnapshot();
            return;
        }

        _log.Info("geo", $"a newer file stands for {stale.Count} rule database(s), downloading");
        foreach (var source in stale)
        {
            _updatingSources.Add(source.Name);
        }

        PushSnapshot();
        foreach (var source in stale)
        {
            await UpdateOneSourceAsync(source).ConfigureAwait(false);
        }

        await AfterSourcesChangedAsync().ConfigureAwait(false);
    }

    // Whether the device sits on a link that carries no traffic bill: wifi, or the cable a set-top box runs on.
    private bool OnFreeLink()
    {
        try
        {
            if (Application.Context.GetSystemService(Context.ConnectivityService) is not ConnectivityManager manager
                || manager.ActiveNetwork is not { } network
                || manager.GetNetworkCapabilities(network) is not { } capabilities)
            {
                return false;
            }

            // A tunnel of ours carries the transports of the link under it, so this reads the same either way.
            return capabilities.HasTransport(TransportType.Wifi) || capabilities.HasTransport(TransportType.Ethernet);
        }
        catch (Exception ex)
        {
            _log.Warn("geo", "the kind of the network link could not be read: " + ex);
            return false;
        }
    }

    private async Task RefreshRoutingSummariesAsync()
    {
        var summaries = await _store.ListRoutingListSummariesAsync().ConfigureAwait(false);
        _routingSummaries = summaries
            .Select(s => new RoutingListEntry(s.Id, s.Name, s.RuleCount, s.RouteCount, s.DomainCount,
                s.ProxyRuleCount, s.DirectRuleCount, s.BlockRuleCount, s.AllUdp, s.UseGlobalProxy))
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
        // Только отсутствующие базы: лежащую на диске обновляет экран источников, а не открытие списка.
        var missing = (await _store.ListGeoSourcesAsync().ConfigureAwait(false))
            .Where(source => !HasGeoFile(source.Name))
            .ToList();
        if (missing.Count == 0)
        {
            return;
        }

        foreach (var source in missing)
        {
            _updatingSources.Add(source.Name);
        }

        PushSnapshot();
        var pump = new CancellationTokenSource();
        var ticker = ProgressPumpAsync(pump.Token);
        try
        {
            foreach (var source in missing)
            {
                await UpdateOneSourceAsync(source).ConfigureAwait(false);
            }
        }
        finally
        {
            pump.Cancel();
            await ticker.ConfigureAwait(false);
            pump.Dispose();
        }

        await AfterSourcesChangedAsync().ConfigureAwait(false);
        if (missing.All(source => _sourceErrors.GetValueOrDefault(source.Name) is not null))
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
        // A limit of 0 asks for the whole category; without one the answer stays a short page.
        var cap = args.Count > 1 && int.TryParse(args[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var c)
            ? (c <= 0 ? entries.Count : c)
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

        // The first list applies at once.
        if (id == 0 && lists.Count == 0)
        {
            _selectedRoutingList = savedId;
            Save();
        }

        MarkRoutingChanged(savedId);
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

    // Counts the routes a draft rule set puts into the tun, so the editor can refuse a list this device cannot
    // carry. A device with the relay reports no ceiling.
    private async Task<IpcAck> CountRoutesAsync(IReadOnlyList<string> args)
    {
        if (args.Count < 1)
        {
            return Fail();
        }

        await EnsureInitAsync().ConfigureAwait(false);
        try
        {
            await EnsureGeoFilesAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.Warn("geo", $"route count runs on the geo files at hand: {ex.Message}");
        }

        var full = string.Equals(args[0], "full", StringComparison.OrdinalIgnoreCase);
        var draft = await _geo.MaterializeDraftAsync([.. args.Skip(1)]).ConfigureAwait(false);
        // A name carries no address until connect, where it resolves and cuts or adds about two routes.
        var names = draft.Domains.Count + draft.DirectDomains.Count + draft.BlockDomains.Count;
        var routes = SystemRoutes.Tunneled(full, draft.Routes, draft.DirectRoutes, draft.BlockRoutes).Count + (names * 2);
        // A session behind the relay has no ceiling: what the route table will not hold, the relay decides. Without
        // one the tun keeps the direct ranges that do not fit and leaves out the widest, so a list over the budget
        // still runs - shorter of reach, in either mode: every range left outside costs the routes around it.
        var relayed = RouteBudget.Relayable && RouterEnabled();
        var trims = !relayed && routes > RouteBudget.Max;
        var kept = trims
            ? SystemRoutes.Carve(draft.DirectRoutes, [], draft.BlockRoutes, RouteBudget.Max)
            : [];
        if (trims)
        {
            routes = SystemRoutes.Tunneled(full, draft.Routes, kept, draft.BlockRoutes).Count + (names * 2);
        }

        var limit = relayed ? 0 : RouteBudget.Max;
        return new IpcAck(true, $"{{\"routes\":{routes.ToString(CultureInfo.InvariantCulture)}"
            + $",\"limit\":{limit.ToString(CultureInfo.InvariantCulture)}"
            + $",\"trims\":{(trims ? 1 : 0).ToString(CultureInfo.InvariantCulture)}"
            + $",\"kept\":{kept.Count.ToString(CultureInfo.InvariantCulture)}"
            + $",\"total\":{draft.DirectRoutes.Count.ToString(CultureInfo.InvariantCulture)}}}");
    }

    // Stores the order the routing-list catalogue is shown in; a name the store does not know leaves it alone.
    private async Task<IpcAck> ReorderRoutingListsAsync(IReadOnlyList<string> args)
    {
        if (args.Count == 0)
        {
            return Fail();
        }

        await EnsureInitAsync().ConfigureAwait(false);
        var stored = (await _store.ListRoutingListSummariesAsync().ConfigureAwait(false))
            .Select(summary => summary.Name)
            .ToHashSet(StringComparer.Ordinal);
        if (args.Any(name => !stored.Contains(name)))
        {
            return Fail();
        }

        await _store.SetRoutingListOrderAsync(args).ConfigureAwait(false);
        await RefreshRoutingSummariesAsync().ConfigureAwait(false);
        PushSnapshot();
        return Ok();
    }

    private async Task<IpcAck> RemoveRoutingListAsync(IReadOnlyList<string> args)
    {
        if (args.Count < 1 || !long.TryParse(args[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var id))
        {
            return Fail();
        }

        await EnsureInitAsync().ConfigureAwait(false);
        await _store.RemoveRoutingListAsync(id).ConfigureAwait(false);
        MarkRoutingChanged(id);

        // Turns routing off when the removed list was the selected one.
        if (_selectedRoutingList == id)
        {
            _selectedRoutingList = null;
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

        MarkRoutingChanged(id);

        // Сводки списков держатся в памяти: без перечитывания снимок отдаёт прежние режимы.
        await RefreshRoutingSummariesAsync().ConfigureAwait(false);
        PushSnapshot();
        return Ok();
    }

    // Export selection from OpExportBundle's arg0 json; all arrays optional. RoutingRules maps a routing list
    // name to the rule tokens to keep; an absent list keeps all its rules.
    private sealed record BundleSelection(
        string[]? Configs,
        string[]? RoutingLists,
        Dictionary<string, string[]>? RoutingRules);

    // Packs the picked configs and routing lists into a portable bundle.
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

        if (configNames.Count == 0 && routingNames.Count == 0)
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
                        transport.UseIpv6,
                        transport.MtuMode,
                        transport.UseRouter),
                null));
        }

        var routingBlocks = new List<PortableBundle.RoutingBlock>();
        var activeList = default(string);
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

                if (_selectedRoutingList == list.Id)
                {
                    activeList = name;
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

        var activeConfig = _selectedTarget is { Length: > 0 } selected
            && configBlocks.Exists(block => string.Equals(block.Name, selected, StringComparison.Ordinal))
                ? selected
                : null;

        var bundle = new PortableBundle.Bundle(
            PortableBundle.FormatTag,
            PortableBundle.CurrentVersion,
            configBlocks,
            routingBlocks,
            ActiveConfig: activeConfig,
            ActiveRoutingList: activeList);
        _log.Info("agent", $"exported bundle: {configBlocks.Count} configs, {routingBlocks.Count} routing lists");
        return new IpcAck(true, PortableBundle.Serialize(bundle));
    }

    // Recreates the bundle's configs and routing lists; policy picks what to do on a name clash.
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
        var listNames = new HashSet<string>(existingLists.Keys, StringComparer.Ordinal);
        var renames = new List<string>();
        var importedConfigs = new Dictionary<string, string>(StringComparer.Ordinal);
        var importedLists = new Dictionary<string, long>(StringComparer.Ordinal);

        foreach (var block in bundle.Configs)
        {
            if (_configs.ContainsKey(block.Name) && policy != "new")
            {
                if (policy != "skip")
                {
                    _configs[block.Name] = block.ConfigText;
                    await ApplyTransportAsync(block.Name, block.Transport).ConfigureAwait(false);
                }

                importedConfigs[block.Name] = block.Name;
                continue;
            }

            var finalName = FreeName(block.Name, configNames, "Конфигурация");
            configNames.Add(finalName);
            if (!string.Equals(finalName, block.Name, StringComparison.Ordinal))
            {
                renames.Add($"«{block.Name}» → «{finalName}»");
            }

            _configs[finalName] = block.ConfigText;
            importedConfigs[block.Name] = finalName;
            await ApplyTransportAsync(finalName, block.Transport).ConfigureAwait(false);
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

                importedLists[block.Name] = existing.Id;
                continue;
            }

            var finalName = FreeName(block.Name, listNames, "Список");
            listNames.Add(finalName);
            if (!string.Equals(finalName, block.Name, StringComparison.Ordinal))
            {
                renames.Add($"«{block.Name}» → «{finalName}»");
            }

            var newId = await _geo.ApplyToRoutingListAsync(0, finalName, block.Rules).ConfigureAwait(false);
            importedLists[block.Name] = newId;
            await ApplyRoutingSettingsAsync(newId, block.Settings).ConfigureAwait(false);
        }

        if (bundle.Profiles is { Count: > 0 } legacy)
        {
            _log.Info("agent", $"the bundle carries {legacy.Count} profile(s) from an older build; their pairings are dropped");
        }

        RestoreBundleSelection(bundle, importedConfigs, importedLists);
        Save();
        await RefreshRoutingSummariesAsync().ConfigureAwait(false);
        PushSnapshot();
        _log.Info("agent", $"imported bundle: {bundle.Configs.Count} configs, {bundle.RoutingLists.Count} routing lists");

        if (renames.Count == 0)
        {
            return new IpcAck(true, IpcMessage.Key(
                "Agent_BundleImported",
                bundle.Configs.Count,
                bundle.RoutingLists.Count));
        }

        if (renames.Count <= 5)
        {
            return new IpcAck(true, IpcMessage.Key(
                "Agent_BundleImportedRenamed",
                bundle.Configs.Count,
                bundle.RoutingLists.Count,
                string.Join(", ", renames)));
        }

        return new IpcAck(true, IpcMessage.Key(
            "Agent_BundleImportedRenamedMany",
            bundle.Configs.Count,
            bundle.RoutingLists.Count));
    }

    // Ставит выбор из бандла, пока своего нет: чужой выбор при восстановлении не трогается.
    private void RestoreBundleSelection(
        PortableBundle.Bundle bundle,
        IReadOnlyDictionary<string, string> configs,
        IReadOnlyDictionary<string, long> lists)
    {
        if (string.IsNullOrEmpty(_selectedTarget)
            && bundle.ActiveConfig is { Length: > 0 } config
            && configs.TryGetValue(config, out var name))
        {
            _selectedTarget = name;
            _log.Info("agent", $"the bundle's server is selected again: {name}");
        }

        if (_selectedRoutingList is null
            && bundle.ActiveRoutingList is { Length: > 0 } list
            && lists.TryGetValue(list, out var id))
        {
            _selectedRoutingList = id;
            _log.Info("agent", $"the bundle's routing list is applied again: {list}");
        }
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

    // Stores the bundle's transport for a config.
    private async Task ApplyTransportAsync(string config, PortableBundle.TransportBlock? transport)
    {
        if (transport is null)
        {
            return;
        }

        await _store.SetConfigTransportAsync(
            new ConfigTransport(config, transport.UseWebSocket, transport.Host, transport.Port, transport.Mtu, transport.UseIpv6, transport.MtuMode, transport.UseRouter)).ConfigureAwait(false);
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

    // Stores a config's websocket front, tunnel MTU and IPv6 opt-in; all reach the tunnel builder on the
    // next connect.
    private async Task<IpcAck> SetWebSocketAsync(IReadOnlyList<string> args)
    {
        if (args.Count < 3 || !_configs.ContainsKey(args[0]))
        {
            return Fail();
        }

        if (ParseRange(args[2], 1, 65535) is not { } port)
        {
            return new IpcAck(false, Loc.Instance.Get("Transport_InvalidPort"));
        }

        // Empty leaves the config in charge: a zero here means nothing was chosen.
        var mtuText = args.Count > 4 ? args[4].Trim() : string.Empty;
        if ((mtuText.Length == 0 ? 0 : ParseRange(mtuText, 576, 1500)) is not { } mtu)
        {
            return new IpcAck(false, Loc.Instance.Get("Transport_InvalidMtu"));
        }

        await EnsureInitAsync().ConfigureAwait(false);
        var previous = await _store.GetConfigTransportAsync(args[0]).ConfigureAwait(false);
        var useIpv6 = args.Count > 5 ? IsOn(args[5]) : previous?.UseIpv6 ?? false;
        var host = args.Count > 3 ? args[3].Trim() : string.Empty;

        // An older client sends no mode, and a size it sent stands for a choice of its own.
        var mode = args.Count > 6
            ? MtuModes.Parse(args[6], previous?.MtuMode ?? MtuMode.Auto)
            : mtu > 0 ? MtuMode.Custom : previous?.MtuMode ?? MtuMode.Auto;
        var useRouter = args.Count > 7 ? IsOn(args[7]) : previous?.UseRouter ?? true;
        var allowInbound = args.Count > 8 ? IsOn(args[8]) : previous?.AllowInbound ?? false;
        var inboundNetwork = args.Count > 9 ? IsOn(args[9]) : previous?.InboundNetwork ?? false;
        await _store.SetConfigTransportAsync(new ConfigTransport(args[0], IsOn(args[1]), host, port, mtu, useIpv6, mode, useRouter, allowInbound, inboundNetwork)).ConfigureAwait(false);
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
        await RefreshSubscriptionMembersAsync().ConfigureAwait(false);
    }

    private async Task RefreshSubscriptionMembersAsync()
    {
        var members = new Dictionary<string, SubscriptionMember>(StringComparer.Ordinal);
        foreach (var member in await _store.ListSubscriptionMembersAsync(null).ConfigureAwait(false))
        {
            members[member.ConfigName] = member;
        }

        _members = members;
    }

    // Picks the routing list every config uses. Args: list id, or "none" to turn routing off.
    private async Task<IpcAck> AssignRoutingAsync(IReadOnlyList<string> args)
    {
        var listArg = args.Count > 0 ? args[0] : "none";
        var picked = string.Equals(listArg, "none", StringComparison.OrdinalIgnoreCase)
            || !long.TryParse(listArg, NumberStyles.Integer, CultureInfo.InvariantCulture, out var listId)
                ? null
                : (long?)listId;

        var names = await Task.Run(async () => (
            From: await ListNameAsync(_selectedRoutingList).ConfigureAwait(false),
            To: await ListNameAsync(picked).ConfigureAwait(false))).ConfigureAwait(false);

        Journal(SwitchLog.RoutingList(names.From, names.To));
        _selectedRoutingList = picked;
        Save();
        PushSnapshot();
        if (_active)
        {
            _ = SetConnectionAsync("connect");
        }

        return Ok();
    }

    // The rules the session routes by. Addresses stay ranges and names stay names: a name resolved to a fresh
    // address keeps its verdict, which a set of host routes fixed at connect never could.
    private async Task<GeoRoutingPlan> BuildPlanAsync(bool useRouter)
    {
        if (_selectedRoutingList is not { } listId)
        {
            return GeoRoutingPlan.Full with { UseRouter = useRouter };
        }

        await EnsureInitAsync().ConfigureAwait(false);
        var list = await _store.GetRoutingListAsync(listId).ConfigureAwait(false);
        if (list is null)
        {
            return GeoRoutingPlan.Full;
        }

        var settings = await _store.GetRoutingSettingsAsync(listId).ConfigureAwait(false);
        // Inbound access rides the tunnel bucket: the tun then carries those ranges and the router sends the answers back.
        var proxyRoutes = new List<string>(list.Routes);
        foreach (var inbound in InboundRanges())
        {
            if (!proxyRoutes.Contains(inbound))
            {
                proxyRoutes.Add(inbound);
            }
        }

        var directRoutes = new List<string>(list.DirectRoutes);
        var directDomains = new List<GeoDomain>(list.DirectDomains);
        SplitExclusions(settings?.Exclusions, directRoutes, directDomains);

        // An application the list names rides the tunnel wherever no rule decided for the destination: the relay
        // names the owner of every connection, so the rules keep deciding for everyone and the applications only
        // add to them. Without a relay the owner is unreachable and the tunnel itself has to be restricted to them,
        // which is what the whole tunnel below stands for.
        var apps = AppPackages(list.Apps);
        var perApp = settings is not { UseGlobalProxy: true } && useRouter && apps.Length > 0;
        var attributed = Build.VERSION.SdkInt >= BuildVersionCodes.Q;
        var plan = new GeoRoutingPlan(
            proxyRoutes,
            directRoutes,
            list.BlockRoutes,
            list.Domains,
            directDomains,
            list.BlockDomains,
            settings is { UseGlobalProxy: true } || (perApp && !attributed),
            settings is { AllUdp: true },
            _routeTtl)
        {
            TunnelApps = perApp && attributed ? apps : [],
            UseRouter = useRouter,
        };

        // A list that decides nothing would leave every destination off the tunnel; the whole tunnel is the safer read.
        if (!plan.HasRules && !perApp)
        {
            _log.Warn("geo", $"routing '{list.Name}': the list decides nothing, running the full tunnel instead");
            return GeoRoutingPlan.Full;
        }

        _log.Info("geo", $"routing '{list.Name}': {plan.ProxyRoutes.Count}/{plan.DirectRoutes.Count}/{plan.BlockRoutes.Count} ranges, "
            + $"{plan.ProxyDomains.Count}/{plan.DirectDomains.Count}/{plan.BlockDomains.Count} names, "
            + ModeName(perApp ? apps.Length : 0, attributed, plan.FullTunnel)
            + (plan.AllUdp ? ", all udp tunneled" : string.Empty));
        return plan;
    }

    // Ranges the tunnel may reach this device from; empty unless the target answers what arrives from it.
    private IReadOnlyList<string> InboundRanges()
    {
        if (_selectedTarget is not { Length: > 0 } name
            || _transports.GetValueOrDefault(name) is not { AllowInbound: true } transport
            || !_configs.TryGetValue(name, out var text))
        {
            return [];
        }

        return TunnelInbound.Ranges(WgConfigEditor.GetAddresses(text), transport.InboundNetwork);
    }

    // How the session reads in the log: rules deciding each destination with the named applications added to them,
    // the tunnel restricted to those applications, everything tunneled, or the rules alone.
    private static string ModeName(int apps, bool attributed, bool fullTunnel)
    {
        if (apps > 0)
        {
            return attributed
                ? $"split, {apps} application(s) tunneled on top of the rules"
                : "per-app tunnel";
        }

        return fullTunnel ? "full tunnel" : "split";
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

    private static int SubscriptionInterval(string? value)
    {
        return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var hours)
            ? Math.Clamp(hours, SettingKeys.SubscriptionIntervalMinHours, SettingKeys.SubscriptionIntervalMaxHours)
            : DefaultSubscriptionIntervalHours;
    }

    private static int GeoInterval(string? value) =>
        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var hours)
            ? Math.Clamp(hours, MinGeoIntervalHours, MaxGeoIntervalHours)
            : 24;

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
            _order = [.. _configs.Keys];
            if (document.RootElement.TryGetProperty("SelectedRouting", out var selectedList)
                && selectedList.ValueKind == JsonValueKind.Number
                && selectedList.TryGetInt64(out var listId))
            {
                _selectedRoutingList = listId;
            }

            if (document.RootElement.TryGetProperty("LogLevel", out var level) && level.ValueKind == JsonValueKind.String)
            {
                _logLevel = KnownLogLevel(level.GetString() ?? "info");
            }

            if (document.RootElement.TryGetProperty("RouteLog", out var route))
            {
                _routeLog = route.ValueKind == JsonValueKind.True;
            }

            if (document.RootElement.TryGetProperty("DirectTcp", out var directTcp)
                && directTcp.ValueKind is JsonValueKind.True or JsonValueKind.False)
            {
                _directTcp = directTcp.ValueKind == JsonValueKind.True;
            }

            if (document.RootElement.TryGetProperty("ExcludeRoutes", out var excludeRoutes)
                && excludeRoutes.ValueKind is JsonValueKind.True or JsonValueKind.False)
            {
                _excludeRoutes = excludeRoutes.ValueKind == JsonValueKind.True;
            }

            if (document.RootElement.TryGetProperty("AllowPrerelease", out var prerelease)
                && prerelease.ValueKind is JsonValueKind.True or JsonValueKind.False)
            {
                _updater.AllowPrerelease = prerelease.ValueKind == JsonValueKind.True;
            }

            if (document.RootElement.TryGetProperty("RouteTtl", out var ttl)
                && ttl.ValueKind == JsonValueKind.Number
                && SettingKeys.TryParseRouteTtl(ttl.GetRawText(), out var seconds))
            {
                _routeTtl = seconds;
            }

            if (document.RootElement.TryGetProperty("GeoAutoCheck", out var geoAuto)
                && geoAuto.ValueKind is JsonValueKind.True or JsonValueKind.False)
            {
                _geoAutoCheck = geoAuto.ValueKind == JsonValueKind.True;
            }

            if (document.RootElement.TryGetProperty("GeoCheckInterval", out var geoInterval)
                && geoInterval.ValueKind == JsonValueKind.Number)
            {
                _geoCheckIntervalHours = GeoInterval(geoInterval.GetRawText());
            }

            if (document.RootElement.TryGetProperty("DialWanted", out var dial)
                && (dial.ValueKind == JsonValueKind.True || dial.ValueKind == JsonValueKind.False))
            {
                _dialWanted = dial.GetBoolean();
            }

            if (document.RootElement.TryGetProperty("SubscriptionAutoRefresh", out var subAuto)
                && subAuto.ValueKind is JsonValueKind.True or JsonValueKind.False)
            {
                _subscriptionAutoRefresh = subAuto.ValueKind == JsonValueKind.True;
            }

            if (document.RootElement.TryGetProperty("SubscriptionInterval", out var subInterval)
                && subInterval.ValueKind == JsonValueKind.Number)
            {
                _subscriptionIntervalHours = SubscriptionInterval(subInterval.GetRawText());
            }

            if (document.RootElement.TryGetProperty("GeoCheckedAt", out var geoChecked)
                && geoChecked.ValueKind == JsonValueKind.String
                && DateTimeOffset.TryParse(geoChecked.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var checkedAt))
            {
                _geoCheckedAt = checkedAt;
            }

            if (document.RootElement.TryGetProperty("Proxy", out var proxy) && proxy.ValueKind == JsonValueKind.Object)
            {
                _proxyOptions = ReadProxy(proxy);
                // The tunnel runs in its own process and listens by its own copy; a restored library brings it back.
                VpnBridge.WriteProxy(_proxyOptions);
            }

            var target = document.RootElement.TryGetProperty("Selected", out var selected) && selected.ValueKind == JsonValueKind.String
                ? selected.GetString()
                : null;
            target = CarryProfile(document.RootElement, target);

            // A selection outliving the config it names would refuse every connect with nothing to point at.
            if (target is { Length: > 0 } && _configs.ContainsKey(target))
            {
                _selectedTarget = target;
            }
        }
        catch (Exception)
        {
        }
    }

    // One-time move off named profiles: the selected profile (else any) hands its config to the selection and its
    // routing list to the global one; the rest of the pairings are lost. Save() drops both maps, so this runs once.
    private string? CarryProfile(JsonElement root, string? target)
    {
        if (!root.TryGetProperty("Profiles", out var profiles) || profiles.ValueKind != JsonValueKind.Object)
        {
            return target;
        }

        var carried = default(string);
        foreach (var entry in profiles.EnumerateObject())
        {
            if (carried is null || string.Equals(entry.Name, target, StringComparison.Ordinal))
            {
                carried = entry.Name;
            }
        }

        if (carried is null)
        {
            return target;
        }

        if (root.TryGetProperty("Routing", out var routing)
            && routing.ValueKind == JsonValueKind.Object
            && routing.TryGetProperty(carried, out var raw)
            && raw.ValueKind == JsonValueKind.String)
        {
            var (listId, useRouting) = ParseRouting(raw.GetString() ?? string.Empty);
            if (useRouting && listId is not null)
            {
                _selectedRoutingList = listId;
            }
        }

        var bound = profiles.GetProperty(carried);
        return bound.ValueKind == JsonValueKind.String && bound.GetString() is { Length: > 0 } config ? config : target;
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
            AppendMap(builder, OrderedNames(), _configs);
            builder.Append(",\"LogLevel\":").Append(JsonSerializer.Serialize(_logLevel));
            builder.Append(",\"RouteLog\":").Append(_routeLog ? "true" : "false");
            builder.Append(",\"DirectTcp\":").Append(_directTcp ? "true" : "false");
            builder.Append(",\"ExcludeRoutes\":").Append(_excludeRoutes ? "true" : "false");
            builder.Append(",\"RouteTtl\":").Append(_routeTtl);
            builder.Append(",\"AllowPrerelease\":").Append(_updater.AllowPrerelease ? "true" : "false");
            builder.Append(",\"GeoAutoCheck\":").Append(_geoAutoCheck ? "true" : "false");
            builder.Append(",\"GeoCheckInterval\":").Append(_geoCheckIntervalHours);
            builder.Append(",\"GeoCheckedAt\":").Append(JsonSerializer.Serialize(_geoCheckedAt.ToString("O", CultureInfo.InvariantCulture)));
            builder.Append(",\"SubscriptionAutoRefresh\":").Append(_subscriptionAutoRefresh ? "true" : "false");
            builder.Append(",\"SubscriptionInterval\":").Append(_subscriptionIntervalHours);
            builder.Append(",\"Proxy\":").Append(JsonSerializer.Serialize(_proxyOptions));
            builder.Append(",\"DialWanted\":").Append(_dialWanted ? "true" : "false");
            builder.Append(",\"Selected\":").Append(JsonSerializer.Serialize(_selectedTarget));
            builder.Append(",\"SelectedRouting\":").Append(_selectedRoutingList?.ToString(CultureInfo.InvariantCulture) ?? "null");
            builder.Append('}');
            System.IO.File.WriteAllText(_storePath, builder.ToString());
        }
        catch (Exception)
        {
        }
    }

    private static void AppendMap(System.Text.StringBuilder builder, List<string> names, Dictionary<string, string> map)
    {
        builder.Append('{');
        var first = true;
        foreach (var name in names)
        {
            if (!first)
            {
                builder.Append(',');
            }

            first = false;
            builder.Append(JsonSerializer.Serialize(name)).Append(':').Append(JsonSerializer.Serialize(map[name]));
        }

        builder.Append('}');
    }

    // Renames a config in place, carrying its text, its stored settings, the selection and the live latch with it.
    // Imports a config from a file the caller names; the text path is import-config's.
    private async Task<IpcAck> AddConfigAsync(IReadOnlyList<string> args)
    {
        if (args.Count < 2 || args[0].Length == 0 || args[1].Length == 0)
        {
            return Fail();
        }

        try
        {
            var text = await System.IO.File.ReadAllTextAsync(args[1]).ConfigureAwait(false);
            return await DispatchAsync(new IpcCommand(IpcContract.OpImportConfig, [args[0], text])).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.Error("agent", $"the configuration file '{args[1]}' could not be read", ex);
            return new IpcAck(false, ex.Message);
        }
    }

    // Duplicates a config into an independent copy: its text, its transport and its geo settings.
    private async Task<IpcAck> CopyConfigAsync(IReadOnlyList<string> args)
    {
        if (args.Count < 2 || args[0].Length == 0 || args[1].Trim().Length == 0)
        {
            return Fail();
        }

        var source = args[0];
        var destination = args[1].Trim();
        if (!_configs.TryGetValue(source, out var text))
        {
            return Fail();
        }

        if (_configs.ContainsKey(destination))
        {
            return new IpcAck(false, IpcMessage.Key("Agent_NameTaken", destination));
        }

        _configs[destination] = text;
        Save();
        await EnsureInitAsync().ConfigureAwait(false);
        if (await _store.GetConfigTransportAsync(source).ConfigureAwait(false) is { } transport)
        {
            await _store.SetConfigTransportAsync(transport with { Name = destination }).ConfigureAwait(false);
        }

        if (await _store.GetTunnelGeoAsync(source).ConfigureAwait(false) is { } geo)
        {
            await _store.SaveTunnelGeoAsync(geo with { Name = destination }).ConfigureAwait(false);
        }

        await RefreshTransportsAsync().ConfigureAwait(false);
        PushSnapshot();
        return new IpcAck(true, IpcMessage.Key("Agent_ConfigCopied", destination));
    }

    // Stores a config's own geo rules; the session takes them at the next connect.
    private async Task<IpcAck> SetGeoAsync(IReadOnlyList<string> args)
    {
        if (args.Count < 2 || !_configs.ContainsKey(args[0]))
        {
            return Fail();
        }

        await EnsureInitAsync().ConfigureAwait(false);
        try
        {
            await EnsureGeoFilesAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.Warn("geo", $"the rules are expanded against the geo files at hand: {ex}");
        }

        var (rules, routes, domains, skipped) = await _geo.ApplyAsync(args[0], IsOn(args[1]), [.. args.Skip(2)]).ConfigureAwait(false);
        PushSnapshot();
        var summary = $"saved: {rules} rules, {routes} routes, {domains} domains";
        return new IpcAck(true, skipped > 0
            ? $"{summary}, {skipped} tokens ignored (applies on reconnect)"
            : $"{summary} (applies on reconnect)");
    }

    // Asks every source whether its remote file changed, without downloading it.
    private async Task<IpcAck> CheckSourcesAsync()
    {
        await EnsureInitAsync().ConfigureAwait(false);
        if (_geoSources.Count == 0)
        {
            return new IpcAck(true, IpcMessage.Key("Agent_NoSourcesToCheck"));
        }

        var available = 0;
        foreach (var source in _geoSources.ToList())
        {
            if (await CheckOneSourceAsync(source).ConfigureAwait(false) == GeoUpdateChecker.Status.Available)
            {
                available++;
            }
        }

        await RefreshGeoSourcesAsync().ConfigureAwait(false);
        PushSnapshot();
        return new IpcAck(true, available == 0
            ? IpcMessage.Key("Agent_CheckedNoUpdates", _geoSources.Count)
            : IpcMessage.Key("Agent_CheckedUpdatesAvailable", _geoSources.Count, available));
    }

    private async Task<IpcAck> CheckSourceAsync(IReadOnlyList<string> args)
    {
        if (args.Count < 1 || args[0].Length == 0)
        {
            return Fail();
        }

        await EnsureInitAsync().ConfigureAwait(false);
        var source = _geoSources.FirstOrDefault(entry => string.Equals(entry.Name, args[0], StringComparison.Ordinal));
        if (source is null)
        {
            return Fail();
        }

        var status = await CheckOneSourceAsync(source).ConfigureAwait(false);
        await RefreshGeoSourcesAsync().ConfigureAwait(false);
        PushSnapshot();
        return new IpcAck(true, status switch
        {
            GeoUpdateChecker.Status.Available => IpcMessage.Key("Agent_SourceUpdateAvailable", source.Name),
            GeoUpdateChecker.Status.UpToDate => IpcMessage.Key("Agent_SourceUpToDate", source.Name),
            _ => IpcMessage.Key("Agent_SourceCheckFailed", source.Name),
        });
    }

    // Checks one source under its own ceiling; an unreachable host must not hold the command.
    private async Task<GeoUpdateChecker.Status> CheckOneSourceAsync(GeoSource source)
    {
        try
        {
            using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var status = await _geoChecker.CheckAsync(source, budget.Token).ConfigureAwait(false);
            if (status != GeoUpdateChecker.Status.Unknown)
            {
                await _store.SetGeoUpdateAvailableAsync(source.Name, status == GeoUpdateChecker.Status.Available).ConfigureAwait(false);
            }

            return status;
        }
        catch (System.OperationCanceledException)
        {
            return GeoUpdateChecker.Status.Unknown;
        }
        catch (Exception ex)
        {
            _log.Error("geo", $"'{source.Name}' could not be checked for a newer file; the copy at hand stays in use", ex);
            return GeoUpdateChecker.Status.Unknown;
        }
    }

    // The applications carrying a launcher icon: what a per-app rule can name on this system.
    private static IpcAck ListProcesses()
    {
        var context = Application.Context;
        var manager = context.PackageManager;
        if (manager is null)
        {
            return new IpcAck(true, string.Empty);
        }

        var own = context.PackageName;
        var intent = new Intent(Intent.ActionMain);
        intent.AddCategory(Intent.CategoryLauncher);
        var rows = manager.QueryIntentActivities(intent, global::Android.Content.PM.PackageInfoFlags.MetaData)
            .Where(entry => entry.ActivityInfo?.ApplicationInfo?.PackageName is { Length: > 0 } package
                && !string.Equals(package, own, StringComparison.Ordinal))
            .Select(entry =>
            {
                var info = entry.ActivityInfo!.ApplicationInfo!;
                return (Label: entry.LoadLabel(manager)?.ToString() ?? info.PackageName!, Package: info.PackageName!);
            })
            .GroupBy(app => app.Package, StringComparer.Ordinal)
            .Select(group => group.First())
            .OrderBy(app => app.Label, StringComparer.CurrentCultureIgnoreCase)
            .Select(app => string.Join('\t', "app", app.Label, app.Package, $"app:pkg={app.Package}"));
        return new IpcAck(true, string.Join('\n', rows));
    }

    // The ladder, as far as a phone can measure it: the socket cannot be excused from the tunnel outside the
    // service that owns it, so the leg under the tunnel is skipped rather than measured through the tunnel.
    private async Task<IpcAck> CheckChannelAsync(IReadOnlyList<string> args, CancellationToken ct)
    {
        var config = _selectedTarget;
        if (string.IsNullOrEmpty(config))
        {
            return new IpcAck(false, IpcMessage.Key("Agent_NoConfigSelected"));
        }

        // Configs live in this agent's own JSON; the store the desktop agents read them from is empty here.
        var text = _configs.GetValueOrDefault(config, string.Empty);
        var running = VpnBridge.IsRunning(Application.Context);
        var transport = await _store.GetConfigTransportAsync(config, ct).ConfigureAwait(false);
        var carrier = Carrier(text, transport);
        var options = new ChannelProbeOptions(
            config,
            running,
            LocalGateway.Find(),
            await ResolveAsync(carrier.Host, ct).ConfigureAwait(false),
            LinkLossProbe.PeerTargets(WgConfigEditor.GetAddresses(text)),
            LinkLossProbe.BeyondTargets(WgConfigEditor.GetDns(text)),
            true,
            false,
            running ? HandshakeAge.Step(Math.Max(0, DateTimeOffset.UtcNow.ToUnixTimeSeconds() - _handshakeUnix)) : -1,
            running ? _link.HandshakesPerMinute : -1,
            SourceHost: args.Count > 0 && args[0].Length > 0 ? args[0] : BusiestHost(),
            ConfiguredMtu: text.Length == 0 ? 0 : MtuPlan.ResolveForLink(transport, text),
            CarrierPort: carrier.Port);

        var report = await ChannelProbe.RunAsync(options, ct).ConfigureAwait(false);
        Record(report.Render(), report.Culprit.Length > 0, report.Advice);
        return new IpcAck(true, report.ToPayload());
    }

    // The destination the user is actually watching: the relay lives in the tunnel process and leaves what it
    // ranks by traffic where the head can read it.
    private static string? BusiestHost()
    {
        return VpnBridge.ReadSessions(SessionWindowSeconds).Busiest?.Host;
    }

    // What the relay holds, as it left it: the tunnel runs in another process, so this is read whole rather
    // than asked for, and a snapshot older than the window answers about a session that is already gone.
    // Every destination the agent can put a name to: what it resolved for this config before, which outlives a
    // disconnect, and what the tunnel carries right now.
    private async Task<IpcAck> KnownHostsAsync(CancellationToken ct)
    {
        await EnsureInitAsync().ConfigureAwait(false);
        var hosts = new List<string>(await _log.Store.ListProbeTargetsAsync(KnownHostList.HistoryRows, ct).ConfigureAwait(false));
        if (_selectedTarget is { Length: > 0 } config)
        {
            foreach (var resolution in await _store.ListDomainResolutionsAsync(config, ct).ConfigureAwait(false))
            {
                hosts.Add(resolution.Domain);
            }
        }

        foreach (var held in VpnBridge.ReadSessions(SessionWindowSeconds).Sessions)
        {
            hosts.Add(held.Name.Length > 0 ? held.Name : held.Host);
        }

        return new IpcAck(true, KnownHostList.Payload(hosts));
    }

    private static IpcAck GetSessions()
    {
        return new IpcAck(true, VpnBridge.ReadSessions(SessionWindowSeconds).ToPayload());
    }

    // Every saved server measured by the legs that cost only echoes. A socket cannot be excused from the tunnel
    // outside the service that owns it, so a sweep run while the tunnel is up says so in its verdict.
    private async Task<IpcAck> CheckServersAsync(CancellationToken ct)
    {
        var running = VpnBridge.IsRunning(Application.Context);
        var servers = new List<SweepServer>();
        foreach (var name in OrderedNames())
        {
            var text = _configs.GetValueOrDefault(name, string.Empty);
            var transport = await _store.GetConfigTransportAsync(name, ct).ConfigureAwait(false);
            var carrier = Carrier(text, transport);
            servers.Add(new SweepServer(
                name,
                await ResolveAsync(carrier.Host, ct).ConfigureAwait(false),
                carrier.Port,
                running && string.Equals(name, _selectedTarget, StringComparison.Ordinal)));
        }

        // The phone carries the whole tun, so a tunnel that runs is the default route for every probe here.
        var report = await ServerSweep
            .RunAsync(servers, new SweepOptions(LocalGateway.Find(), running, running), ct)
            .ConfigureAwait(false);

        Record(report.Render(), report.VerdictKey != CheckVerdicts.SweepBest);
        return new IpcAck(true, report.ToPayload());
    }

    // Why one destination goes where it goes, under the rules in force.
    private async Task<IpcAck> CheckTargetAsync(IReadOnlyList<string> args, CancellationToken ct)
    {
        if (args.Count < 1 || string.IsNullOrWhiteSpace(args[0]))
        {
            return new IpcAck(false, "check-target requires a domain, an address, an app token or a geo rule");
        }

        var (inspector, _) = await RulesAsync(ct).ConfigureAwait(false);
        var report = await inspector
            .InspectAsync(args[0], _selectedTarget ?? string.Empty, new TargetProbes(), ct)
            .ConfigureAwait(false);

        Record(report.Render(), report.VerdictKey != TargetVerdicts.Proxy);
        return new IpcAck(true, report.ToPayload());
    }

    // The rules in force: the inspector that answers under them, and whether they put everything in the tunnel.
    // A named application is added to the rules where the relay can name the owner of a connection, and below
    // that the tunnel is built as an allow list of them instead - the answer follows the mode in force.
    private async Task<(TargetInspector Inspector, bool Whole)> RulesAsync(CancellationToken ct)
    {
        var list = _selectedRoutingList is { } listId
            ? await _store.GetRoutingListAsync(listId, ct).ConfigureAwait(false)
            : null;
        var settings = _selectedRoutingList is { } id
            ? await _store.GetRoutingSettingsAsync(id, ct).ConfigureAwait(false)
            : null;
        var full = settings?.UseGlobalProxy ?? false;
        // Without a list the tun carries everything, so nothing is left outside it to be put back in.
        return (new TargetInspector(list, !full, AppMode(list, settings, RouterEnabled())), _selectedRoutingList is null || full);
    }

    // Measures one destination over the path asked for. The tun fixes its routes at establish, so the tunnel
    // path holds only where the rules take the target there anyway, and a socket is excused from it only inside
    // the process that owns it - that run is handed to the tunnel instead.
    private async Task<IpcAck> ProbeTargetAsync(IReadOnlyList<string> args, CancellationToken ct)
    {
        if (args.Count < 1 || string.IsNullOrWhiteSpace(args[0]))
        {
            return new IpcAck(false, "probe-target requires a domain or an address");
        }

        var target = args[0];
        var path = args.Count > 1 && args[1].Length > 0 ? args[1] : ProbePaths.Auto;
        var upload = args.Count > 2 ? args[2] : string.Empty;
        var running = VpnBridge.IsRunning(Application.Context);
        if (!running && path != ProbePaths.Bypass)
        {
            return ProbeAck(TargetProbe.Refused(target, path, ProbeVerdicts.NotConnected));
        }

        var (inspector, whole) = await RulesAsync(ct).ConfigureAwait(false);
        var forced = running && path == ProbePaths.Tunnel && !whole;
        var routed = path == ProbePaths.Auto || forced
            ? await RoutedPathAsync(inspector, target, ct).ConfigureAwait(false)
            : (Taken: string.Empty, ViaTunnel: false);

        // The target the rules keep outside the tun cannot be put back into it: the run says so itself, so the
        // path stays on offer and the refusal lands in the journal beside the runs that measured.
        if (forced && !routed.ViaTunnel)
        {
            return ProbeAck(TargetProbe.Refused(target, path, ProbeVerdicts.PathUnavailable));
        }

        var taken = path == ProbePaths.Auto ? routed.Taken : path;
        if (running && path == ProbePaths.Bypass)
        {
            return ProbeAck(await HandOverAsync(target, path, taken, upload, ct).ConfigureAwait(false));
        }

        var options = new TargetProbeOptions(target, path, taken, upload);
        return ProbeAck(await TargetProbe.RunAsync(options, ct).ConfigureAwait(false));
    }

    // Where the rules in force send a destination, said the way the desktops say it, and whether that is the
    // tunnel.
    private async Task<(string Taken, bool ViaTunnel)> RoutedPathAsync(TargetInspector inspector, string target, CancellationToken ct)
    {
        var report = await inspector
            .InspectAsync(target, _selectedTarget ?? string.Empty, new TargetProbes(), ct)
            .ConfigureAwait(false);
        return report.VerdictKey switch
        {
            TargetVerdicts.Proxy or TargetVerdicts.ProxyAppsOnly => ("tunnel by rule", true),
            TargetVerdicts.UnlistedFull => ("tunnel by default", true),
            TargetVerdicts.Direct => ("bypass by rule", false),
            TargetVerdicts.UnlistedSplit or TargetVerdicts.AppOutside or TargetVerdicts.AppUnlisted => ("bypass by default", false),
            TargetVerdicts.Blocked => ("blocked by rule", false),
            _ => (string.Empty, false),
        };
    }

    // Hands the run to the tunnel and waits for what it measured; a tunnel that never answers leaves the path
    // unmeasured rather than the caller waiting on it.
    private static async Task<ProbeReport> HandOverAsync(string target, string path, string taken, string upload, CancellationToken ct)
    {
        VpnBridge.ClearProbeResult();
        VpnBridge.WriteProbe(new ProbeRequest(target, path, taken, upload));
        VpnBridge.RequestProbe(Application.Context);
        for (var waited = 0; waited < ProbeWaitMs; waited += ProbePollMs)
        {
            await Task.Delay(ProbePollMs, ct).ConfigureAwait(false);
            var payload = VpnBridge.ReadProbeResult();
            if (payload.Length > 0)
            {
                VpnBridge.ClearProbeResult();
                return ProbeReport.Parse(payload, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            }
        }

        VpnBridge.ClearProbe();
        return TargetProbe.Refused(target, path, ProbeVerdicts.PathUnavailable);
    }

    // Stores a probe in its own journal, which no capture floor reaches, and puts its closing line in the agent log.
    private IpcAck ProbeAck(ProbeReport report)
    {
        var rendered = report.Render().TrimEnd();
        _log.Store.AppendProbe(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), rendered);
        var rows = rendered.Split('\n');
        _log.Info("probe", rows[0].Trim());
        _log.Info("probe", rows[^1].Trim());
        return new IpcAck(true, report.ToPayload());
    }

    // Stores a finished run where no capture floor reaches it, and puts its closing line in the agent log.
    // An MTU that does not fit the measured path is warned about on its own, whoever the run blames.
    private void Record(string rendered, bool blamed, MtuAdvice? advice = null)
    {
        _log.Store.AppendCheck(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), rendered.TrimEnd());
        if (advice is not null)
        {
            _log.Warn("check", advice.Describe());
        }

        var closing = rendered.TrimEnd().Split('\n')[^1].Trim();
        if (blamed)
        {
            _log.Warn("check", closing);
            return;
        }

        _log.Info("check", closing);
    }

    // The server's address as the tunnel dials it.
    // The host the tunnel dials and the port to knock on: a websocket carrier stands at its own address, and
    // the endpoint in the config is only what the server hands the tunnel to behind it.
    private static (string Host, int Port) Carrier(string text, ConfigTransport? transport)
    {
        var endpoint = WgConfigEditor.GetEndpoint(text) ?? string.Empty;
        var colon = endpoint.LastIndexOf(':');
        var host = (colon > 0 ? endpoint[..colon] : endpoint).Trim('[', ']');
        if (transport?.UseWebSocket != true)
        {
            return (host, 0);
        }

        var front = WsEndpoint.Parse(transport.WebSocketHost, transport.WebSocketPort, host);
        return (front.Host, front.Port);
    }

    // One address for a host, as the tunnel resolves it.
    private static async Task<string?> ResolveAsync(string host, CancellationToken ct)
    {
        if (host.Length == 0)
        {
            return null;
        }

        if (System.Net.IPAddress.TryParse(host, out var parsed))
        {
            return parsed.ToString();
        }

        try
        {
            var addresses = await System.Net.Dns.GetHostAddressesAsync(host, ct).ConfigureAwait(false);
            return addresses.FirstOrDefault(one => one.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)?.ToString();
        }
        catch (Exception ex) when (ex is System.Net.Sockets.SocketException or ArgumentException or System.OperationCanceledException)
        {
            return null;
        }
    }

    private async Task<IpcAck> CollectDiagnosticsAsync()
    {
        await EnsureInitAsync().ConfigureAwait(false);
        try
        {
            await _log.Store.FlushAsync().ConfigureAwait(false);
            var bundle = new DiagnosticsBundle(_store, _log.Store);
            var path = await bundle.WriteAsync(DiagnosticsDirectory(), DiagnosticsHeader(), AndroidAgentLog.Render).ConfigureAwait(false);
            _log.Info("agent", $"diagnostics archive written to {path}; keys and credentials in it are masked");
            return new IpcAck(true, path);
        }
        catch (Exception ex)
        {
            _log.Error("agent", "the diagnostics archive could not be built; nothing was written", ex);
            return new IpcAck(false, IpcMessage.Key("Agent_DiagnosticsFailed", ex.Message));
        }
    }

    // Writes where support can pull the archive without root; the private directory is the fallback.
    private static string DiagnosticsDirectory()
    {
        var external = Application.Context.GetExternalFilesDir(null)?.AbsolutePath;
        var root = string.IsNullOrEmpty(external) ? Application.Context.FilesDir?.AbsolutePath ?? "." : external;
        return System.IO.Path.Combine(root, "diagnostics");
    }

    // Opens the diagnostics summary with the build, the device and the agent's own state.
    private string DiagnosticsHeader()
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("AmneziaGeo diagnostics");
        sb.AppendLine($"generated:       {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}");
        sb.AppendLine($"app version:     {AppVersion}");
        sb.AppendLine($"android:         {Build.VERSION.Release} (api {(int)Build.VERSION.SdkInt})");
        sb.AppendLine($"device:          {Build.Manufacturer} {Build.Model}");
        sb.AppendLine($"abi:             {string.Join(", ", Build.SupportedAbis ?? [])}");
        sb.AppendLine();
        sb.AppendLine("[settings]");
        sb.AppendLine($"log level:       {_logLevel}");
        sb.AppendLine($"routing log:     {(_routeLog ? "on" : "off")}");
        sb.AppendLine($"route ttl:       {_routeTtl}s");
        sb.AppendLine();
        sb.AppendLine("[state]");
        sb.AppendLine($"selected target: {_selectedTarget ?? "-"}");
        sb.AppendLine($"bound target:    {_boundTarget ?? "-"}");
        sb.AppendLine($"status:          {_boundStatus}");
        sb.AppendLine($"connect failed:  {_connectFailed}");
        sb.AppendLine();
        return sb.ToString();
    }

    private async Task<IpcAck> RenameConfigAsync(IReadOnlyList<string> args)
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

        if (!_configs.TryGetValue(oldName, out var value))
        {
            return Fail();
        }

        if (_configs.ContainsKey(newName))
        {
            return new IpcAck(false, Loc.Instance.Get("Agent_NameTaken", newName));
        }

        _configs.Remove(oldName);
        _configs[newName] = value;

        // A rename keeps the row where it stands.
        var place = _order.IndexOf(oldName);
        if (place >= 0)
        {
            _order[place] = newName;
        }

        RetargetSelection(oldName, newName);
        Save();
        await EnsureInitAsync().ConfigureAwait(false);
        await ConfigRename.CarryAsync(_store, oldName, newName).ConfigureAwait(false);
        await RefreshTransportsAsync().ConfigureAwait(false);
        PushSnapshot();
        return Ok();
    }

    // Binds the next connect to a config; an empty name leaves none selected.
    private async Task<IpcAck> SelectConfigAsync(IReadOnlyList<string> args)
    {
        var name = args.Count > 0 && args[0].Length > 0 ? args[0] : null;
        if (name is not null && !_configs.ContainsKey(name))
        {
            return Fail();
        }

        // Nothing selected leaves nothing to run: the tunnel bound to the old target goes down with it.
        if (name is null && _active)
        {
            await SetConnectionAsync("disconnect").ConfigureAwait(false);
        }

        Journal(SwitchLog.Config(_selectedTarget, name));
        _selectedTarget = name;
        Save();
        PushSnapshot();
        return Ok();
    }

    // Keeps a switchover in the log whatever the capture floor is.
    private void Journal(string? line)
    {
        if (line is { Length: > 0 })
        {
            _log.Note(SwitchLog.Source, line);
        }
    }

    // The name a routing list id stands for; null id is routing off.
    private async Task<string?> ListNameAsync(long? listId)
    {
        if (listId is not long id)
        {
            return null;
        }

        await EnsureInitAsync().ConfigureAwait(false);
        return (await _store.GetRoutingListAsync(id).ConfigureAwait(false))?.Name;
    }

    // Refuses while the config is the running target; otherwise drops it with its stored settings and clears a selection pointing at it.
    private async Task<IpcAck> AddSubscriptionAsync(IReadOnlyList<string> args)
    {
        await EnsureInitAsync().ConfigureAwait(false);
        var outcome = await Subscriptions().AddAsync(args, CancellationToken.None).ConfigureAwait(false);
        if (!outcome.Ack.Ok)
        {
            return outcome.Ack;
        }

        _log.Info("sub", $"subscription {outcome.Name} brought in {outcome.Added} configuration(s)");

        // A first import is ready to dial: it takes the selection while there is none.
        if (_selectedTarget is null
            && await Subscriptions().MembersAsync(outcome.Name, CancellationToken.None).ConfigureAwait(false) is [var first, ..])
        {
            _selectedTarget = first;
        }

        await RefreshSubscriptionMembersAsync().ConfigureAwait(false);
        Save();
        PushSnapshot();
        return outcome.Ack;
    }

    private async Task<IpcAck> ListSubscriptionsAsync()
    {
        await EnsureInitAsync().ConfigureAwait(false);
        return await Subscriptions().ListAsync(CancellationToken.None).ConfigureAwait(false);
    }

    private async Task<IpcAck> RefreshSubscriptionAsync(IReadOnlyList<string> args)
    {
        await EnsureInitAsync().ConfigureAwait(false);
        var outcome = await Subscriptions().RefreshAsync(args, CancellationToken.None).ConfigureAwait(false);
        DropRunningIfGone();
        FlagRewritten(outcome);
        await RefreshTransportsAsync().ConfigureAwait(false);
        Save();
        PushSnapshot();
        return outcome.Ack;
    }

    private async Task<IpcAck> RemoveSubscriptionAsync(IReadOnlyList<string> args)
    {
        await EnsureInitAsync().ConfigureAwait(false);
        var ack = await Subscriptions().RemoveAsync(args, _active && _boundTarget is { Length: > 0 } ? [_boundTarget] : [], CancellationToken.None).ConfigureAwait(false);
        if (ack.Ok)
        {
            await RefreshTransportsAsync().ConfigureAwait(false);
            Save();
            PushSnapshot();
        }

        return ack;
    }

    private async Task<IpcAck> ConfigSubscriptionAsync(IReadOnlyList<string> args)
    {
        await EnsureInitAsync().ConfigureAwait(false);
        return await Subscriptions().ConfigUrlAsync(args, CancellationToken.None).ConfigureAwait(false);
    }

    // Re-reads the subscriptions whose interval has run out. A subscription lives on the open internet, so this
    // runs whether or not the interface is up; a document of a couple of kilobytes needs no free link either.
    private async Task RefreshSubscriptionsAsync(CancellationToken ct)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(GeoStartDelaySeconds), ct).ConfigureAwait(false);
            while (!ct.IsCancellationRequested)
            {
                if (_subscriptionAutoRefresh)
                {
                    await EnsureInitAsync().ConfigureAwait(false);
                    var service = Subscriptions();
                    var due = await service.DueAsync(_subscriptionIntervalHours, DateTimeOffset.UtcNow, ct).ConfigureAwait(false);
                    foreach (var subscription in due)
                    {
                        var outcome = await service.RefreshAsync([subscription.Name], ct).ConfigureAwait(false);
                        FlagRewritten(outcome);
                        if (!outcome.Ack.Ok)
                        {
                            _log.Warn("sub", $"subscription {subscription.Name} could not be re-read");
                        }
                    }

                    if (due.Count > 0)
                    {
                        Save();
                        PushSnapshot();
                    }
                }

                await Task.Delay(TimeSpan.FromSeconds(SubscriptionTickSeconds), ct).ConfigureAwait(false);
            }
        }
        catch (System.OperationCanceledException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
        catch (Exception ex)
        {
            _log.Error("sub", "the scheduled re-read of the subscriptions stopped", ex);
        }
    }

    // A rewritten text applies on a fresh interface; flag a reconnect when the running target is affected.
    // Переписанный подпиской текст встаёт сразу: туннель поднимается заново на нём же. Упавший от смены
    // адреса поднимается так же - подписка чинит именно его.
    private void FlagRewritten(SubscriptionOutcome outcome)
    {
        if (!_dialWanted)
        {
            return;
        }

        var dialled = _active ? _boundTarget : _selectedTarget;

        if (!outcome.Rewritten.Any(name => string.Equals(name, dialled, StringComparison.Ordinal)))
        {
            return;
        }

        // Выбор успели увести на другую конфигурацию - переподключение остаётся за пользователем.
        if (!string.Equals(dialled, _selectedTarget, StringComparison.Ordinal))
        {
            _restartRequired = true;
            return;
        }

        _log.Info("sub", "the subscription rewrote the configuration it dials; dialling it again");
        _ = RestartTunnelAsync();
    }

    // Подписка снесла работающую конфигурацию - держать туннель не на чем.
    private void DropRunningIfGone()
    {
        if (_active && _boundTarget is { Length: > 0 } bound && !_configs.ContainsKey(bound))
        {
            _log.Info("sub", "the subscription dropped the running configuration; disconnecting");
            _ = SetConnectionAsync("disconnect");
        }
    }

    // Движок читает конфигурацию на старте: новый текст встаёт только на поднятом заново туннеле.
    private async Task RestartTunnelAsync()
    {
        await SetConnectionAsync("disconnect").ConfigureAwait(false);
        for (var attempt = 0; attempt < RestartWaitTicks && _active; attempt++)
        {
            await Task.Delay(RestartTickMs).ConfigureAwait(false);
        }

        await SetConnectionAsync("connect").ConfigureAwait(false);
    }

    private SubscriptionService Subscriptions()
    {
        return new SubscriptionService(_geoHttp, _store, new MapLibrary(this));
    }

    // The configuration library as a subscription sees it: this head keeps its configurations in one map.
    private sealed class MapLibrary(AndroidAgentConnection agent) : ISubscriptionLibrary
    {
        public Task<IReadOnlyCollection<string>> NamesAsync(CancellationToken ct)
        {
            return Task.FromResult<IReadOnlyCollection<string>>([.. agent._configs.Keys]);
        }

        public Task<string?> TextAsync(string name, CancellationToken ct)
        {
            return Task.FromResult(agent._configs.TryGetValue(name, out var text) ? text : null);
        }

        public Task AddAsync(string name, string confText, CancellationToken ct)
        {
            agent._configs[name] = confText;
            return Task.CompletedTask;
        }

        public Task EditAsync(string name, string confText, CancellationToken ct)
        {
            agent._configs[name] = confText;
            return Task.CompletedTask;
        }

        public async Task RemoveAsync(string name, CancellationToken ct)
        {
            agent._configs.Remove(name);
            if (string.Equals(name, agent._selectedTarget, StringComparison.Ordinal))
            {
                agent._selectedTarget = null;
            }

            // Always-on raises the last session on its own: a config that is gone must not come back with it.
            if (VpnBridge.ReadRequest() is { } stored && string.Equals(name, stored.Name, StringComparison.Ordinal))
            {
                VpnBridge.ClearRequest();
            }

            await agent._store.RemoveConfigTransportAsync(name, ct).ConfigureAwait(false);
        }
    }

    private async Task<IpcAck> RemoveConfigAsync(IReadOnlyList<string> args)
    {
        if (args.Count < 1)
        {
            return Fail();
        }

        if (_active && string.Equals(args[0], _boundTarget, StringComparison.Ordinal))
        {
            return new IpcAck(false, $"config {args[0]} is running; disconnect first");
        }

        _configs.Remove(args[0]);
        if (string.Equals(args[0], _selectedTarget, StringComparison.Ordinal))
        {
            _selectedTarget = null;
        }

        // Always-on raises the last session on its own: a config that is gone must not come back with it.
        if (VpnBridge.ReadRequest() is { } stored && string.Equals(args[0], stored.Name, StringComparison.Ordinal))
        {
            VpnBridge.ClearRequest();
        }

        Save();
        await EnsureInitAsync().ConfigureAwait(false);
        await _store.RemoveConfigTransportAsync(args[0]).ConfigureAwait(false);
        await RefreshTransportsAsync().ConfigureAwait(false);
        PushSnapshot();
        return Ok();
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

    // The app set from the selected routing list's app:pkg rules, as the allow list the tunnel falls back to where
    // a connection cannot be traced to its owner; ("off", []) when none.
    private async Task<(string Mode, string[] Packages)> ResolveAppSplitFromRoutingAsync(bool useRouter)
    {
        if (_selectedRoutingList is not { } listId || !useRouter)
        {
            return ("off", []);
        }

        await EnsureInitAsync().ConfigureAwait(false);
        var settings = await _store.GetRoutingSettingsAsync(listId).ConfigureAwait(false);
        if (settings is { UseGlobalProxy: true })
        {
            return ("off", []);
        }

        var list = await _store.GetRoutingListAsync(listId).ConfigureAwait(false);
        var packages = AppPackages(list?.Apps);
        return packages.Length > 0 ? ("include", packages) : ("off", []);
    }

    // What the app rules of a list do here: they add to the rules where a connection can be traced to its owner,
    // and hold the tunnel to themselves where it cannot.
    private static AppScope AppMode(RoutingList? list, RoutingSettings? settings, bool useRouter)
    {
        if (!useRouter || settings is { UseGlobalProxy: true } || AppPackages(list?.Apps).Length == 0)
        {
            return AppScope.None;
        }

        return Build.VERSION.SdkInt >= BuildVersionCodes.Q ? AppScope.Additive : AppScope.Exclusive;
    }

    // Whether the selected configuration decides connections on its own: the flag rides with the tunnel, not
    // with the rules, and a configuration that never said otherwise does.
    private bool RouterEnabled() =>
        _selectedTarget is not { Length: > 0 } name || _transports.GetValueOrDefault(name)?.UseRouter != false;

    // The packages a routing list names, without the marker they are stored under.
    private static string[] AppPackages(IReadOnlyList<string>? apps)
    {
        const string prefix = "pkg=";
        if (apps is not { Count: > 0 })
        {
            return [];
        }

        return [.. apps
            .Where(a => a.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .Select(a => a[prefix.Length..].Trim())
            .Where(p => p.Length > 0)
            .Distinct(StringComparer.Ordinal)];
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
            case SettingKeys.DirectTcp:
                _directTcp = IsOn(args[1]);
                _restartRequired = true;
                Save();
                PushSnapshot();
                return Ok();
            case SettingKeys.ExcludeRoutes:
                _excludeRoutes = IsOn(args[1]);
                _restartRequired = true;
                Save();
                PushSnapshot();
                return Ok();
            case SettingKeys.RouteTtl:
                if (!SettingKeys.TryParseRouteTtl(args[1], out var ttl))
                {
                    return Fail();
                }

                _routeTtl = ttl;
                VpnBridge.WriteRouteTtl(ttl);
                if (VpnBridge.IsRunning(Application.Context))
                {
                    VpnBridge.RequestRouteTtl(Application.Context);
                }

                Save();
                PushSnapshot();
                return Ok();
            case "allow-prerelease":
                _updater.AllowPrerelease = IsOn(args[1]);
                Save();
                PushSnapshot();
                return Ok();
            case "geo-auto-check":
                _geoAutoCheck = IsOn(args[1]);
                Save();
                PushSnapshot();
                return Ok();
            case "geo-check-interval-hours":
                _geoCheckIntervalHours = GeoInterval(args[1]);
                Save();
                PushSnapshot();
                return Ok();
            case SettingKeys.SubscriptionAutoRefresh:
                _subscriptionAutoRefresh = IsOn(args[1]);
                Save();
                PushSnapshot();
                return Ok();
            case SettingKeys.SubscriptionRefreshInterval:
                _subscriptionIntervalHours = SubscriptionInterval(args[1]);
                Save();
                PushSnapshot();
                return Ok();
            case SettingKeys.ProxyEnabled:
            case SettingKeys.ProxyAnonymous:
            case SettingKeys.ProxySocksPort:
            case SettingKeys.ProxyHttpPort:
            case SettingKeys.ProxyCredentials:
                if (!TryProxySetting(args[0], args[1], out var options))
                {
                    return Fail();
                }

                _proxyOptions = options;
                Save();
                PublishProxy();
                PushSnapshot();
                return Ok();
            default:
                return Ok();
        }
    }

    // The single user/password pair became a list; a pair stored by an earlier version becomes its first account.
    private static LocalProxyOptions ReadProxy(JsonElement stored)
    {
        var options = JsonSerializer.Deserialize<LocalProxyOptions>(stored.GetRawText()) ?? new LocalProxyOptions();
        if (options.Credentials.Length > 0
            || !stored.TryGetProperty("User", out var user)
            || user.GetString() is not { Length: > 0 } name)
        {
            return options;
        }

        var password = stored.TryGetProperty("Password", out var secret) ? secret.GetString() ?? string.Empty : string.Empty;
        return options with { Credentials = $"{name}:{password}" };
    }

    // One proxy setting on top of the ones in force.
    private bool TryProxySetting(string key, string value, out LocalProxyOptions options)
    {
        options = _proxyOptions;
        switch (key)
        {
            case SettingKeys.ProxyEnabled:
                options = options with { Enabled = IsOn(value) };
                return true;
            case SettingKeys.ProxyAnonymous:
                options = options with { AllowAnonymous = IsOn(value) };
                return true;
            case SettingKeys.ProxySocksPort:
                if (!SettingKeys.TryParseProxyPort(value, out var socks))
                {
                    return false;
                }

                options = options with { SocksPort = socks };
                return true;
            case SettingKeys.ProxyHttpPort:
                if (!SettingKeys.TryParseProxyPort(value, out var http))
                {
                    return false;
                }

                options = options with { HttpPort = http };
                return true;
            default:
                options = options with { Credentials = value.Trim() };
                return true;
        }
    }

    // The tunnel process owns the listener: it takes the settings now if it runs, and at its next start if not.
    private void PublishProxy()
    {
        VpnBridge.WriteProxy(_proxyOptions);
        VpnBridge.RequestProxy(Application.Context);
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
        var configName = _selectedTarget;
        var report = new System.Text.StringBuilder();
        report.Append("config       : ").Append(configName ?? "(none)").Append('\n');
        report.Append("status       : ").Append(_boundStatus).Append('\n');
        report.Append("active       : ").Append(_active ? "yes" : "no").Append('\n');
        if (configName is not null && _configs.TryGetValue(configName, out var configText))
        {
            report.Append("endpoint     : ").Append(WgConfigEditor.GetEndpoint(configText) ?? "(none)").Append('\n');
        }

        var (appMode, appPkgs) = await ResolveAppSplitFromRoutingAsync(RouterEnabled());
        report.Append("app rules    : ").Append(AppRulesLine(appMode, appPkgs.Length)).Append('\n');
        AppendRoutingReport(report);
        report.Append("log level    : ").Append(_logLevel).Append('\n');
        report.Append("direct tcp   : ").Append(_directTcp ? "on" : "off").Append('\n');
        report.Append("exclude routes: ").Append(_excludeRoutes ? "on" : "off").Append('\n');
        report.Append("route log    : ").Append(_routeLog ? "on" : "off").Append('\n');
        return new IpcAck(true, report.ToString());
    }

    // What the applications a list names get in the session the next connect builds.
    private static string AppRulesLine(string mode, int apps)
    {
        if (mode == "off" || apps == 0)
        {
            return "off";
        }

        return Build.VERSION.SdkInt >= BuildVersionCodes.Q
            ? $"{apps} application(s) tunneled on top of the rules"
            : $"{apps} application(s) alone ride the tunnel, the rest pass every rule by";
    }

    // Appends the selected routing list and its rule counts to the runtime report.
    private void AppendRoutingReport(System.Text.StringBuilder report)
    {
        if (_selectedRoutingList is not { } listId)
        {
            report.Append("routing list : (off)\n");
            return;
        }

        var summary = _routingSummaries?.FirstOrDefault(r => r.Id == listId);
        report.Append("routing list : ").Append(summary?.Name ?? listId.ToString(CultureInfo.InvariantCulture)).Append('\n');
        report.Append("  routes         : ").Append(summary?.RouteCount ?? 0).Append('\n');
        report.Append("  domains        : ").Append(summary?.DomainCount ?? 0).Append('\n');
    }

    // Returns the routing list's own rules, bucket by bucket; the verdicts a running session holds are in
    // 'sessions', which reads them from the relay.
    private async Task<IpcAck> GetCacheEntriesAsync()
    {
        await EnsureInitAsync().ConfigureAwait(false);
        var rows = new List<object>();
        if (_selectedRoutingList is { } listId
            && await _store.GetRoutingListAsync(listId).ConfigureAwait(false) is { } list)
        {
            AddRoutes(rows, "proxy", list.Routes);
            AddRoutes(rows, "direct", list.DirectRoutes);
            AddRoutes(rows, "block", list.BlockRoutes);
            AddDomains(rows, "proxy", list.Domains);
            AddDomains(rows, "direct", list.DirectDomains);
            AddDomains(rows, "block", list.BlockDomains);
        }

        return Rows(rows);
    }

    // Addresses one bucket carries.
    private static void AddRoutes(List<object> rows, string bucket, IReadOnlyList<string>? routes)
    {
        foreach (var route in routes ?? [])
        {
            rows.Add(new { kind = bucket, key = route, value = "route" });
        }
    }

    // Names one bucket carries, each with the way it is matched.
    private static void AddDomains(List<object> rows, string bucket, IReadOnlyList<GeoDomain>? domains)
    {
        foreach (var domain in domains ?? [])
        {
            rows.Add(new { kind = bucket, key = domain.Value, value = domain.Kind.ToString().ToLowerInvariant() });
        }
    }

    private static IpcAck Rows(List<object> rows)
    {
        const int cap = 1000;
        var total = rows.Count;
        var capped = total > cap;
        var entries = capped ? rows.Take(cap).ToList() : rows;
        return new IpcAck(true, JsonSerializer.Serialize(new { total, capped, entries }));
    }

    private static bool IsKnownLogTable(string name) => name is SqliteLogStore.AgentTable or SqliteLogStore.RoutesTable
        or SqliteLogStore.ChecksTable or SqliteLogStore.ProbeTable;

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
