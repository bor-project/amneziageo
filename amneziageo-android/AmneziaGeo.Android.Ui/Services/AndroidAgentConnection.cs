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
    private const int DefaultMtu = 1420;

    // Age past which the tunnel's own snapshot of what it carries is no longer an answer about what runs now.
    private const int SessionWindowSeconds = 60;
    private static readonly string AppVersion = ReadAppVersion();

    private readonly SqliteStateStore _store;
    private readonly GeoConfigurator _geo;
    private readonly GeoFileUpdater _geoUpdater;
    private readonly GeoUpdateChecker _geoChecker;
    private readonly AndroidAgentLog _log;
    private readonly GeoHttp _geoHttp;
    private readonly HttpClient _httpClient = new();
    private readonly AndroidUpdater _updater;
    // Null until the store has been read: the snapshot says "not loaded yet", not "no lists".
    private IReadOnlyList<RoutingListEntry>? _routingSummaries;
    private IReadOnlyDictionary<string, ConfigTransport> _transports = new Dictionary<string, ConfigTransport>(StringComparer.Ordinal);
    private IReadOnlyList<GeoSource> _geoSources = [];
    private IReadOnlyList<GeoFileMetadata> _geoFileMeta = [];
    private readonly HashSet<string> _updatingSources = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string?> _sourceErrors = new(StringComparer.Ordinal);
    private readonly Dictionary<string, bool> _updateAvailable = new(StringComparer.Ordinal);
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
    private bool _connectFailed;
    private string _connectFailReason = string.Empty;
    private string _connectFailDetail = string.Empty;
    private bool _started;
    private bool _disposed;
    private string _logLevel = "error";
    private bool _routeLog;
    private int _routeTtl = 300;
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
        MainActivity.Resumed -= SyncTunnelState;
        if (_events is not null)
        {
            Application.Context.UnregisterReceiver(_events);
            _events = null;
        }

        _updater.Dispose();
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

            case IpcContract.OpCountRoutes:
                return await CountRoutesAsync(args);

            case IpcContract.OpRemoveRoutingList:
                return await RemoveRoutingListAsync(args);

            case IpcContract.OpGetRoutingSettings:
                return await GetRoutingSettingsAsync(args);

            case IpcContract.OpSetRoutingSettings:
                return await SetRoutingSettingsAsync(args);

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

            case IpcContract.OpCheckChannel:
                return await CheckChannelAsync(args, CancellationToken.None).ConfigureAwait(false);

            case IpcContract.OpCheckServers:
                return await CheckServersAsync(CancellationToken.None).ConfigureAwait(false);

            case IpcContract.OpCheckTarget:
                return await CheckTargetAsync(args, CancellationToken.None).ConfigureAwait(false);

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
        // Reports the connecting stage from the request: the tunnel process speaks only once it is up, and until
        // then a snapshot would pull the card back to disconnected.
        _active = true;
        _boundStatus = ConnectionStatus.Connecting;
        _boundTarget = _selectedTarget;
        PushSnapshot();
        var (appMode, appPkgs) = await ResolveAppSplitFromRoutingAsync();
        VpnBridge.WritePlan(await BuildPlanAsync().ConfigureAwait(false));
        _log.Info("agent", $"connect requested: config '{_selectedTarget}', app rules {AppRulesLine(appMode, appPkgs.Length)}");
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
        var proxyAddresses = ProxyAddresses(proxy.Running);
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

    // Addresses the proxy answers on while it listens.
    private static IReadOnlyList<string> ProxyAddresses(bool running)
    {
        return running ? LocalProxyServer.UsableAddresses() : [];
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
        return new ConfigEntry(name, WgConfigEditor.GetEndpoint(config) ?? string.Empty, false, StatusFor(name), [],
            WebSocket: false,
            WebSocketHost: transport?.WebSocketHost ?? string.Empty,
            WebSocketPort: transport?.WebSocketPort ?? 443,
            Mtu: transport?.Mtu ?? 0,
            UseIpv6: transport?.UseIpv6 ?? false,
            HandshakeAgeSeconds: handshake,
            RxBitsPerSecond: reading.RxBitsPerSecond,
            TxBitsPerSecond: reading.TxBitsPerSecond,
            HandshakesPerMinute: reading.HandshakesPerMinute,
            LossPercent: reading.LossPercent,
            RttMs: reading.RttMs);
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
            list.Add(new SourceEntry(source.Name, source.Kind, source.Url, updated, meta?.CategoryCount ?? 0, updating, 0, _updateAvailable.GetValueOrDefault(source.Name), error));
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
        var limit = RouteBudget.Applies ? RouteBudget.Max : 0;
        return new IpcAck(true, $"{{\"routes\":{routes.ToString(CultureInfo.InvariantCulture)},\"limit\":{limit.ToString(CultureInfo.InvariantCulture)}}}");
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
                        transport.UseIpv6),
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

    // Picks the routing list every config uses. Args: list id, or "none" to turn routing off.
    private async Task<IpcAck> AssignRoutingAsync(IReadOnlyList<string> args)
    {
        var listArg = args.Count > 0 ? args[0] : "none";
        var picked = string.Equals(listArg, "none", StringComparison.OrdinalIgnoreCase)
            || !long.TryParse(listArg, NumberStyles.Integer, CultureInfo.InvariantCulture, out var listId)
                ? null
                : (long?)listId;

        Journal(SwitchLog.RoutingList(await ListNameAsync(_selectedRoutingList).ConfigureAwait(false), await ListNameAsync(picked).ConfigureAwait(false)));
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
    private async Task<GeoRoutingPlan> BuildPlanAsync()
    {
        if (_selectedRoutingList is not { } listId)
        {
            return GeoRoutingPlan.Full;
        }

        await EnsureInitAsync().ConfigureAwait(false);
        var list = await _store.GetRoutingListAsync(listId).ConfigureAwait(false);
        if (list is null)
        {
            return GeoRoutingPlan.Full;
        }

        var settings = await _store.GetRoutingSettingsAsync(listId).ConfigureAwait(false);
        var directRoutes = new List<string>(list.DirectRoutes);
        var directDomains = new List<GeoDomain>(list.DirectDomains);
        SplitExclusions(settings?.Exclusions, directRoutes, directDomains);

        // An application the list names rides the tunnel wherever no rule decided for the destination: the relay
        // names the owner of every connection, so the rules keep deciding for everyone and the applications only
        // add to them. Without a relay the owner is unreachable and the tunnel itself has to be restricted to them,
        // which is what the whole tunnel below stands for.
        var apps = AppPackages(list.Apps);
        var perApp = settings is not { UseGlobalProxy: true } && apps.Length > 0;
        var attributed = Build.VERSION.SdkInt >= BuildVersionCodes.Q;
        var plan = new GeoRoutingPlan(
            list.Routes,
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
            builder.Append(",\"RouteTtl\":").Append(_routeTtl);
            builder.Append(",\"AllowPrerelease\":").Append(_updater.AllowPrerelease ? "true" : "false");
            builder.Append(",\"Proxy\":").Append(JsonSerializer.Serialize(_proxyOptions));
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
                _updateAvailable[source.Name] = status == GeoUpdateChecker.Status.Available;
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
            ConfiguredMtu: transport is { Mtu: > 0 } ? transport.Mtu : WgConfigEditor.GetMtu(text),
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

        var list = _selectedRoutingList is { } listId
            ? await _store.GetRoutingListAsync(listId, ct).ConfigureAwait(false)
            : null;
        var settings = _selectedRoutingList is { } id
            ? await _store.GetRoutingSettingsAsync(id, ct).ConfigureAwait(false)
            : null;
        // A named application is added to the rules where the relay can name the owner of a connection, and below
        // that the tunnel is built as an allow list of them instead - the check answers under the mode in force.
        var report = await new TargetInspector(list, !(settings?.UseGlobalProxy ?? false),
                AppMode(list, settings))
            .InspectAsync(args[0], _selectedTarget ?? string.Empty, new TargetProbes(), ct)
            .ConfigureAwait(false);

        Record(report.Render(), report.VerdictKey != TargetVerdicts.Proxy);
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
    private async Task<(string Mode, string[] Packages)> ResolveAppSplitFromRoutingAsync()
    {
        if (_selectedRoutingList is not { } listId)
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
    private static AppScope AppMode(RoutingList? list, RoutingSettings? settings)
    {
        if (settings is { UseGlobalProxy: true } || AppPackages(list?.Apps).Length == 0)
        {
            return AppScope.None;
        }

        return Build.VERSION.SdkInt >= BuildVersionCodes.Q ? AppScope.Additive : AppScope.Exclusive;
    }

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
            case SettingKeys.RouteTtl:
                if (!SettingKeys.TryParseRouteTtl(args[1], out var ttl))
                {
                    return Fail();
                }

                _routeTtl = ttl;
                Save();
                PushSnapshot();
                return Ok();
            case "allow-prerelease":
                _updater.AllowPrerelease = IsOn(args[1]);
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

        var (appMode, appPkgs) = await ResolveAppSplitFromRoutingAsync();
        report.Append("app rules    : ").Append(AppRulesLine(appMode, appPkgs.Length)).Append('\n');
        AppendRoutingReport(report);
        report.Append("log level    : ").Append(_logLevel).Append('\n');
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
        report.Append("  geoip routes   : ").Append(summary?.RouteCount ?? 0).Append('\n');
        report.Append("  geosite domains: ").Append(summary?.DomainCount ?? 0).Append('\n');
    }

    // Returns the routing list's own rules; on Android a verdict lives in the tun's route table, not in a cache.
    private async Task<IpcAck> GetCacheEntriesAsync()
    {
        await EnsureInitAsync().ConfigureAwait(false);
        var rows = new List<object>();
        if (_selectedRoutingList is { } listId
            && await _store.GetRoutingListAsync(listId).ConfigureAwait(false) is { } list)
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

    private static bool IsKnownLogTable(string name) => name is SqliteLogStore.AgentTable or SqliteLogStore.RoutesTable or SqliteLogStore.ChecksTable;

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
