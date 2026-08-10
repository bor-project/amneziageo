using System.Globalization;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using AmneziaGeo.Dal;
using AmneziaGeo.Decl;
using AmneziaGeo.Geo;
using AmneziaGeo.Ipc;
using AmneziaGeo.Routing;
using Microsoft.Extensions.Logging.Abstractions;

namespace AmneziaGeo.Linux.App;

/// <summary>
/// Linux agent core: owns the library, projects status snapshots, and executes the commands the UI sends.
/// </summary>
internal sealed class LinuxAgent : IDisposable
{
    private const string LogLevelKey = "log-level";
    private const string RouteLogKey = "route-log";
    private const string SurviveRebootKey = "survive-reboot";
    private const string PeriodicReconnectKey = "periodic-reconnect-enabled";
    private const string ReconnectIntervalKey = "periodic-reconnect-interval-seconds";
    private const string RouteTtlKey = SettingKeys.RouteTtl;
    private const int SupervisorTickSeconds = 5;

    private readonly SqliteStateStore _store;
    private readonly LinuxGeoFileStore _geoFiles;
    private readonly GeoConfigurator _geo;
    private readonly GeoFileUpdater _geoUpdater;
    private readonly GeoUpdateChecker _geoChecker;
    private readonly DiagnosticsBundle _diagnostics;
    private readonly GeoHttp _geoHttp;
    private readonly HttpClient _httpClient = new();
    private readonly TunnelController _tunnel;
    private readonly BundleCommands _bundles;
    private readonly LinuxUpdater _updater;
    private readonly AgentLog _log;
    private readonly SemaphoreSlim _commandGate = new(1, 1);
    private readonly HashSet<string> _updatingSources = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string?> _sourceErrors = new(StringComparer.Ordinal);
    private readonly Dictionary<string, bool> _updateAvailable = new(StringComparer.Ordinal);

    private string? _selectedTarget;
    private string? _boundTarget;
    private string _boundStatus = ConnectionStatus.Disconnected;
    private string _logLevel = "error";
    private bool _routeLog;
    private bool _surviveReboot;
    private bool _periodicReconnect;
    private int _reconnectIntervalSeconds = 30;
    private int _routeTtlSeconds = TunnelOptions.DefaultRouteTtlSeconds;
    private bool _desiredConnected;
    private int _handshakeAge = -1;
    private readonly LinkMeter _meter = new();
    private LinkReading _link = LinkReading.Empty;

    // Echoes inside the tunnel: the only thing that says what it loses, the peer counters keeping no trace of a
    // packet that never arrived.
    private LinkLossProbe? _loss;
    private CancellationTokenSource? _lossRun;
    private DateTimeOffset _linkLoggedAt;
    private bool _churnLogged;
    private DateTime _nextRetryUtc = DateTime.MinValue;
    private bool _connectFailed;
    private bool _restartRequired;
    private string _connectFailReason = string.Empty;
    private string _connectFailDetail = string.Empty;
    private bool _disposed;

    /// <summary>
    /// ctor
    /// </summary>
    public LinuxAgent(AgentLog log, string enginePath, string interfaceName)
    {
        _log = log;
        _store = new SqliteStateStore(AgentPaths.StateDb);
        _geoFiles = new LinuxGeoFileStore(AgentPaths.GeoDirectory);
        _geoHttp = new GeoHttp(_httpClient, NullLogger<GeoHttp>.Instance);
        _geoUpdater = new GeoFileUpdater(_store, _geoHttp, _geoFiles);
        _geoChecker = new GeoUpdateChecker(_store, _geoHttp, _geoFiles);
        _diagnostics = new DiagnosticsBundle(_store, log.Store);
        _geo = new GeoConfigurator(_store, _geoFiles);
        _tunnel = new TunnelController(enginePath, interfaceName, log);
        _bundles = new BundleCommands(_store, _geo);
        _updater = new LinuxUpdater(_httpClient, log, PushAsync);
    }

    /// <summary>
    /// Raised when the state changed and the connected clients need a fresh snapshot.
    /// </summary>
    public event Func<CancellationToken, Task>? StateChanged;

    /// <summary>
    /// Opens the library, seeds the default geo sources, and restores the saved settings.
    /// </summary>
    public async Task InitializeAsync(CancellationToken ct)
    {
        await _store.InitializeAsync(ct).ConfigureAwait(false);
        await GeoDefaults.SeedIfEmptyAsync(_store, null, ct).ConfigureAwait(false);
        await _geo.RematerializeIfStaleAsync(ct).ConfigureAwait(false);

        var settings = await _store.GetSettingsAsync(ct).ConfigureAwait(false);
        _selectedTarget = settings.TryGetValue(StateKeys.SelectedTarget, out var target) && target.Length > 0 ? target : null;
        _logLevel = settings.TryGetValue(LogLevelKey, out var level) ? KnownLogLevel(level) : "error";
        _routeLog = settings.TryGetValue(RouteLogKey, out var route) && IsOn(route);
        _surviveReboot = settings.TryGetValue(SurviveRebootKey, out var survive) && IsOn(survive);
        _periodicReconnect = settings.TryGetValue(PeriodicReconnectKey, out var periodic) && IsOn(periodic);
        _reconnectIntervalSeconds = ReconnectInterval(settings.TryGetValue(ReconnectIntervalKey, out var interval) ? interval : null);
        _routeTtlSeconds = settings.TryGetValue(RouteTtlKey, out var ttl) && SettingKeys.TryParseRouteTtl(ttl, out var seconds) ? seconds : TunnelOptions.DefaultRouteTtlSeconds;
        _log.SetCaptureLevel(_logLevel);
        _log.SetRouteLog(_routeLog);
        _updater.CollectInstallResult();
        _log.Info("agent", $"library {AgentPaths.Root}, target '{_selectedTarget ?? "(none)"}'");
    }

    /// <summary>
    /// Connects at start when the tunnel is set to survive a reboot, then keeps it up until cancellation.
    /// </summary>
    public async Task RunSupervisorAsync(CancellationToken ct)
    {
        if (_surviveReboot && _selectedTarget is { Length: > 0 })
        {
            _log.Info("agent", $"auto-connect on start: {_selectedTarget}");
            var ack = await DispatchAsync(new IpcCommand(IpcContract.OpSetConnection, ["connect"]), ct).ConfigureAwait(false);
            if (!ack.Ok)
            {
                _log.Warn("agent", $"auto-connect failed: {ack.Message}");
            }
        }

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(SupervisorTickSeconds));
        while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
        {
            await SuperviseAsync(ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Executes one command; concurrent callers are serialized.
    /// </summary>
    public async Task<IpcAck> DispatchAsync(IpcCommand command, CancellationToken ct)
    {
        await _commandGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await RunAsync(command, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.Error("agent", $"command '{command.Op}' failed", ex);
            return new IpcAck(false, $"{command.Op} failed: {ex.Message}");
        }
        finally
        {
            _commandGate.Release();
        }
    }

    /// <summary>
    /// Builds the current status snapshot.
    /// </summary>
    public async Task<StatusSnapshot> BuildSnapshotAsync(CancellationToken ct)
    {
        var configs = new List<ConfigEntry>();
        foreach (var name in await _store.ListConfigNamesAsync(ct).ConfigureAwait(false))
        {
            configs.Add(await BuildConfigEntryAsync(name, ct).ConfigureAwait(false));
        }

        var routingLists = (await _store.ListRoutingListSummariesAsync(ct).ConfigureAwait(false))
            .Select(s => new RoutingListEntry(s.Id, s.Name, s.RuleCount, s.RouteCount, s.DomainCount))
            .ToList();

        return new StatusSnapshot(
            AgentVersion: AgentBuild.Version,
            BoundTarget: _boundTarget,
            Configs: configs,
            RoutingLists: routingLists,
            Active: _tunnel.Running,
            BoundStatus: _boundStatus,
            RestartRequired: _restartRequired,
            SelectedTarget: _selectedTarget ?? string.Empty,
            SelectedRoutingList: await _store.GetSelectedRoutingListAsync(ct).ConfigureAwait(false),
            Sources: await BuildSourcesAsync(ct).ConfigureAwait(false),
            ConnectFailed: _connectFailed,
            LogLevel: _logLevel,
            RouteLog: _routeLog,
            ConnectFailReason: _connectFailReason,
            ConnectFailDetail: _connectFailDetail,
            SurviveReboot: _surviveReboot,
            PeriodicReconnect: _periodicReconnect,
            PeriodicReconnectIntervalSeconds: _reconnectIntervalSeconds,
            UpdateUrl: _updater.Url,
            UpdateAvailable: _updater.Available,
            UpdateVersion: _updater.Version,
            UpdateSetupUrl: _updater.SetupUrl,
            UpdateDescription: _updater.Description,
            UpdateSetupSha256: _updater.Sha256,
            UpdateSetupPath: _updater.SetupPath,
            UpdateDownloading: _updater.Downloading,
            UpdateDownloaded: _updater.Downloaded,
            UpdateDownloadPercent: _updater.Percent,
            UpdateDownloadFailed: _updater.Failed,
            UpdateCancelRequested: _updater.CancelRequested,
            UpdateChecking: _updater.Checking,
            UpdateCheckFailed: _updater.CheckFailed,
            UpdateInstalling: _updater.Installing);
    }

    // Notices a tunnel that went down under us and redials it while periodic reconnect is on, and carries the
    // keepalive view to the clients: nothing else pushes a snapshot while the tunnel just runs.
    private async Task SuperviseAsync(CancellationToken ct)
    {
        var counters = _tunnel.Running ? await _tunnel.PeerCountersAsync(ct).ConfigureAwait(false) : null;
        var age = -1;
        var reading = LinkReading.Empty;
        if (counters is { } peer)
        {
            age = peer.HandshakeUnix > 0
                ? HandshakeAge.Step(Math.Max(0, DateTimeOffset.UtcNow.ToUnixTimeSeconds() - peer.HandshakeUnix))
                : -1;
            reading = _meter.Sample(peer.RxBytes, peer.TxBytes, peer.HandshakeUnix, _loss?.Percent ?? LinkHealth.LossUnknown);
            LogLink(reading);
        }
        else
        {
            _meter.Reset();
            _loss?.Reset();
            _churnLogged = false;
        }

        if (age != _handshakeAge || reading.DiffersFrom(_link))
        {
            _handshakeAge = age;
            _link = reading;
            await PushAsync(ct).ConfigureAwait(false);
        }

        if (!_desiredConnected || _tunnel.Running)
        {
            return;
        }

        if (_boundStatus == ConnectionStatus.Connected)
        {
            _log.Warn("agent", "the tunnel went down outside the agent");
            _connectFailed = true;
            _connectFailReason = ConnectFailureReason.Unknown.ToString();
            _connectFailDetail = "engine stopped";
            _boundStatus = ConnectionStatus.Failed;
            _nextRetryUtc = DateTime.UtcNow.AddSeconds(_reconnectIntervalSeconds);
            await PushAsync(ct).ConfigureAwait(false);
        }

        if (!_periodicReconnect || DateTime.UtcNow < _nextRetryUtc)
        {
            return;
        }

        _nextRetryUtc = DateTime.UtcNow.AddSeconds(_reconnectIntervalSeconds);
        _log.Info("agent", $"reconnecting to '{_selectedTarget ?? "(none)"}'");
        var ack = await DispatchAsync(new IpcCommand(IpcContract.OpSetConnection, ["connect"]), ct).ConfigureAwait(false);
        if (!ack.Ok)
        {
            _log.Warn("agent", $"reconnect failed: {ack.Message}");
        }
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

    // Starts this connection's loss probe: the resolvers the config declares and the peer's own address on the
    // tunnel are echoed once a second, and what fails to come back is the loss the screen shows.
    private void StartLossProbe(string config)
    {
        StopLossProbe();
        var run = new CancellationTokenSource();
        var probe = new LinkLossProbe(LinkLossProbe.TargetsFor(WgConfigEditor.GetDns(config), WgConfigEditor.GetAddresses(config)));
        _loss = probe;
        _lossRun = run;
        _ = Task.Run(() => probe.RunAsync(run.Token));
    }

    // Ends the probe with the tunnel it measured.
    private void StopLossProbe()
    {
        _lossRun?.Cancel();
        _lossRun?.Dispose();
        _lossRun = null;
        _loss = null;
    }

    private async Task<IpcAck> RunAsync(IpcCommand command, CancellationToken ct)
    {
        var args = command.Args;
        switch (command.Op)
        {
            case IpcContract.OpAttachUi:
                return Ok();

            case IpcContract.OpLogClient:
                if (args.Count > 0 && args[0].Length > 0)
                {
                    _log.Warn("ui", args[0]);
                }

                return Ok();

            case IpcContract.OpImportConfig:
                return await ImportConfigAsync(args, ct).ConfigureAwait(false);

            case IpcContract.OpEditConfig:
                return await EditConfigAsync(args, ct).ConfigureAwait(false);

            case IpcContract.OpAddConfig:
                return await AddConfigAsync(args, ct).ConfigureAwait(false);

            case IpcContract.OpGetConfig:
                return await GetConfigAsync(args, ct).ConfigureAwait(false);

            case IpcContract.OpRemoveConfig:
                return await RemoveConfigAsync(args, ct).ConfigureAwait(false);

            case IpcContract.OpRenameConfig:
                return await RenameConfigAsync(args, ct).ConfigureAwait(false);

            case IpcContract.OpReorderConfigs:
                return await ReorderConfigsAsync(args, ct).ConfigureAwait(false);

            case IpcContract.OpCopyConfig:
                return await CopyConfigAsync(args, ct).ConfigureAwait(false);

            case IpcContract.OpSelectConfig:
                return await SelectTargetAsync(args, ct).ConfigureAwait(false);

            case IpcContract.OpAssignRouting:
                return await AssignRoutingAsync(args, ct).ConfigureAwait(false);

            case IpcContract.OpSetGeo:
                return await SetGeoAsync(args, ct).ConfigureAwait(false);

            case IpcContract.OpSetWebSocket:
                return await SetTransportAsync(args, ct).ConfigureAwait(false);

            case IpcContract.OpSetConfigDns:
                return await SetConfigDnsAsync(args, ct).ConfigureAwait(false);

            case IpcContract.OpSetConfigExclusions:
                return await SetConfigExclusionsAsync(args, ct).ConfigureAwait(false);

            case IpcContract.OpListLocalSubnets:
                return new IpcAck(true, string.Join('\n', LocalSubnets()));

            case IpcContract.OpListGeo:
                return new IpcAck(true, string.Join('\n', await _geo.CategoriesAsync(ct).ConfigureAwait(false)));

            case IpcContract.OpGetGeoEntries:
                return await GetGeoEntriesAsync(args, ct).ConfigureAwait(false);

            case IpcContract.OpSaveRoutingList:
                return await SaveRoutingListAsync(args, ct).ConfigureAwait(false);

            case IpcContract.OpGetRoutingList:
                return await GetRoutingListAsync(args, ct).ConfigureAwait(false);

            case IpcContract.OpRemoveRoutingList:
                return await RemoveRoutingListAsync(args, ct).ConfigureAwait(false);

            case IpcContract.OpGetRoutingSettings:
                return await GetRoutingSettingsAsync(args, ct).ConfigureAwait(false);

            case IpcContract.OpSetRoutingSettings:
                return await SetRoutingSettingsAsync(args, ct).ConfigureAwait(false);

            case IpcContract.OpAddSource:
                return await AddSourceAsync(args, ct).ConfigureAwait(false);

            case IpcContract.OpEditSource:
                return await EditSourceAsync(args, ct).ConfigureAwait(false);

            case IpcContract.OpRemoveSource:
                return await RemoveSourceAsync(args, ct).ConfigureAwait(false);

            case IpcContract.OpUpdateSource:
                return await UpdateSourcesAsync(args.Count > 0 ? args[0] : null, ct).ConfigureAwait(false);

            case IpcContract.OpUpdateSources:
            case IpcContract.OpDownloadGeo:
                return await UpdateSourcesAsync(null, ct).ConfigureAwait(false);

            case IpcContract.OpCheckSource:
                return await CheckSourceAsync(args, ct).ConfigureAwait(false);

            case IpcContract.OpCheckSources:
                return await CheckSourcesAsync(ct).ConfigureAwait(false);

            case IpcContract.OpCountRoutes:
                return await CountRoutesAsync(args, ct).ConfigureAwait(false);

            case IpcContract.OpCollectDiagnostics:
                return await CollectDiagnosticsAsync(ct).ConfigureAwait(false);

            // Nothing here routes by application: the tunnel is a kernel interface with no per-process verdict.
            case IpcContract.OpListProcesses:
                return new IpcAck(false, IpcMessage.Key("Agent_PerAppUnsupported"));

            case IpcContract.OpSetSetting:
                return await SetSettingAsync(args, ct).ConfigureAwait(false);

            case IpcContract.OpReadLog:
                return await ReadLogAsync(args, ct).ConfigureAwait(false);

            case IpcContract.OpClearLog:
                return await ClearLogAsync(args, ct).ConfigureAwait(false);

            case IpcContract.OpExportLog:
                return await ExportLogAsync(args, ct).ConfigureAwait(false);

            case IpcContract.OpGetRuntimeConfig:
                return await GetRuntimeConfigAsync(ct).ConfigureAwait(false);

            case IpcContract.OpGetCacheEntries:
                return await GetCacheEntriesAsync(ct).ConfigureAwait(false);

            case IpcContract.OpCheckChannel:
                return await CheckChannelAsync(ct).ConfigureAwait(false);

            case IpcContract.OpCheckTarget:
                return await CheckTargetAsync(args, ct).ConfigureAwait(false);

            case IpcContract.OpExportBundle:
                return await _bundles.ExportAsync(args, ct).ConfigureAwait(false);

            case IpcContract.OpImportBundle:
                return await ImportBundleAsync(args, ct).ConfigureAwait(false);

            case IpcContract.OpSetConnection:
                return await SetConnectionAsync(args.Count > 0 ? args[0] : string.Empty, ct).ConfigureAwait(false);

            case IpcContract.OpCheckUpdate:
                return await _updater.CheckAsync(args.Count > 0 && args[0] == "silent", ct).ConfigureAwait(false);

            case IpcContract.OpDownloadUpdate:
            {
                var started = _updater.StartDownload(ct);
                await PushAsync(ct).ConfigureAwait(false);
                return started;
            }

            case IpcContract.OpCancelUpdateDownload:
                return _updater.Cancel();

            case IpcContract.OpApplyUpdate:
                return await _updater.InstallAsync(ct).ConfigureAwait(false);

            // The agent owns the download here, so a client report changes nothing.
            case IpcContract.OpReportUpdateDownload:
                return Ok();

            default:
                _log.Warn("agent", $"command '{command.Op}' is not wired in the Linux agent");
                return new IpcAck(false, IpcMessage.Key("Linux_OpNotWired", command.Op));
        }
    }

    private async Task<IpcAck> SetConnectionAsync(string desired, CancellationToken ct)
    {
        if (desired == "disconnect")
        {
            _desiredConnected = false;
            _restartRequired = false;
            _boundStatus = ConnectionStatus.Disconnecting;
            await PushAsync(ct).ConfigureAwait(false);
            await _tunnel.DownAsync(ct).ConfigureAwait(false);
            StopLossProbe();
            _boundTarget = null;
            _boundStatus = ConnectionStatus.Disconnected;
            await PushAsync(ct).ConfigureAwait(false);
            return Ok();
        }

        if (desired != "connect")
        {
            return new IpcAck(false, $"unknown connection state '{desired}'");
        }

        var configName = _selectedTarget;
        var config = configName is null ? null : await _store.GetConfigTextAsync(configName, ct).ConfigureAwait(false);
        if (configName is null || config is null)
        {
            _connectFailed = true;
            _connectFailReason = (configName is null ? ConnectFailureReason.NoTargetSelected : ConnectFailureReason.ConfigMissing).ToString();
            _connectFailDetail = "no configuration";
            _boundStatus = ConnectionStatus.Failed;
            await PushAsync(ct).ConfigureAwait(false);
            return new IpcAck(false, configName is null ? "no configuration is selected" : $"configuration '{configName}' not found");
        }

        _desiredConnected = true;
        _connectFailed = false;
        _restartRequired = false;
        _connectFailReason = string.Empty;
        _connectFailDetail = string.Empty;
        _boundTarget = _selectedTarget;
        _boundStatus = ConnectionStatus.Connecting;
        await PushAsync(ct).ConfigureAwait(false);

        var routing = await TunnelRouting.LoadAsync(_store, ct).ConfigureAwait(false);
        var configDns = await _store.GetConfigDnsAsync(configName, ct).ConfigureAwait(false);
        var options = TunnelOptions.Read(configDns?.Servers, _routeTtlSeconds);
        var failure = await _tunnel.UpAsync(config, routing, options, ct).ConfigureAwait(false);
        if (failure is not null)
        {
            _connectFailed = true;
            _connectFailReason = ConnectFailureReason.ServiceStartFailed.ToString();
            _connectFailDetail = failure;
            _boundStatus = ConnectionStatus.Failed;
            _nextRetryUtc = DateTime.UtcNow.AddSeconds(_reconnectIntervalSeconds);
            await PushAsync(ct).ConfigureAwait(false);
            return new IpcAck(false, failure);
        }

        _boundStatus = ConnectionStatus.Connected;
        StartLossProbe(config);
        _log.Info("agent", $"connected: {_boundTarget}");
        await PushAsync(ct).ConfigureAwait(false);
        return Ok();
    }

    private async Task<IpcAck> GetConfigAsync(IReadOnlyList<string> args, CancellationToken ct)
    {
        if (args.Count < 1)
        {
            return Fail();
        }

        var text = await _store.GetConfigTextAsync(args[0], ct).ConfigureAwait(false);
        return text is null ? NotFound(args[0]) : new IpcAck(true, text);
    }

    private async Task<IpcAck> ImportConfigAsync(IReadOnlyList<string> args, CancellationToken ct)
    {
        if (args.Count < 2)
        {
            return Fail();
        }

        if (!VpnLinkCodec.LooksLikeConf(args[1]))
        {
            return new IpcAck(false, "the text is not a WireGuard configuration");
        }

        // Import creates; replacing the text of an existing configuration is edit-config. Without this an import
        // under a taken name silently overwrote another configuration's keys.
        if (await _store.ConfigExistsAsync(args[0], ct).ConfigureAwait(false))
        {
            return new IpcAck(false, IpcMessage.Key("Agent_ConfigNameTaken", args[0]));
        }

        var ack = await SaveConfigAsync(args, ct).ConfigureAwait(false);

        // A first import is ready to dial: it takes the selection while there is none.
        if (ack.Ok && _selectedTarget is null)
        {
            await StoreSelectedTargetAsync(args[0], ct).ConfigureAwait(false);
            await PushAsync(ct).ConfigureAwait(false);
        }

        return ack;
    }

    private async Task<IpcAck> EditConfigAsync(IReadOnlyList<string> args, CancellationToken ct)
    {
        if (args.Count < 2)
        {
            return Fail();
        }

        return await _store.ConfigExistsAsync(args[0], ct).ConfigureAwait(false)
            ? await SaveConfigAsync(args, ct).ConfigureAwait(false)
            : NotFound(args[0]);
    }

    private async Task<IpcAck> SaveConfigAsync(IReadOnlyList<string> args, CancellationToken ct)
    {
        if (args.Count < 2)
        {
            return Fail();
        }

        await _store.SaveConfigAsync(args[0], args[1], ct).ConfigureAwait(false);
        await PushAsync(ct).ConfigureAwait(false);
        return Ok();
    }

    private async Task<IpcAck> AddConfigAsync(IReadOnlyList<string> args, CancellationToken ct)
    {
        if (args.Count < 2 || !File.Exists(args[1]))
        {
            return new IpcAck(false, args.Count < 2 ? "expected a name and a file path" : $"{args[1]} not found");
        }

        await _store.SaveConfigAsync(args[0], await File.ReadAllTextAsync(args[1], ct).ConfigureAwait(false), ct).ConfigureAwait(false);
        await PushAsync(ct).ConfigureAwait(false);
        return Ok();
    }

    private async Task<IpcAck> RemoveConfigAsync(IReadOnlyList<string> args, CancellationToken ct)
    {
        if (args.Count < 1)
        {
            return Fail();
        }

        if (!await _store.ConfigExistsAsync(args[0], ct).ConfigureAwait(false))
        {
            return NotFound(args[0]);
        }

        if (string.Equals(args[0], _boundTarget, StringComparison.Ordinal) && _tunnel.Running)
        {
            return new IpcAck(false, $"config {args[0]} is running; disconnect first");
        }

        await _store.RemoveConfigAsync(args[0], ct).ConfigureAwait(false);
        await _store.RemoveTunnelGeoAsync(args[0], ct).ConfigureAwait(false);
        await _store.RemoveConfigTransportAsync(args[0], ct).ConfigureAwait(false);
        await _store.RemoveConfigDnsAsync(args[0], ct).ConfigureAwait(false);
        await _store.RemoveConfigExclusionsAsync(args[0], ct).ConfigureAwait(false);
        if (string.Equals(args[0], _selectedTarget, StringComparison.Ordinal))
        {
            await StoreSelectedTargetAsync(null, ct).ConfigureAwait(false);
        }

        await PushAsync(ct).ConfigureAwait(false);
        return Ok();
    }

    // Stores the order the config list is shown in.
    private async Task<IpcAck> ReorderConfigsAsync(IReadOnlyList<string> args, CancellationToken ct)
    {
        if (args.Count == 0)
        {
            return Fail();
        }

        await _store.SetConfigOrderAsync(args, ct).ConfigureAwait(false);
        await PushAsync(ct).ConfigureAwait(false);
        return Ok();
    }

    private async Task<IpcAck> RenameConfigAsync(IReadOnlyList<string> args, CancellationToken ct)
    {
        if (args.Count < 2)
        {
            return Fail();
        }

        if (await _store.ConfigExistsAsync(args[1], ct).ConfigureAwait(false))
        {
            return new IpcAck(false, $"'{args[1]}' already exists");
        }

        await _store.RenameConfigAsync(args[0], args[1], ct).ConfigureAwait(false);
        await ConfigRename.CarryAsync(_store, args[0], args[1], ct).ConfigureAwait(false);
        await PushAsync(ct).ConfigureAwait(false);
        return Ok();
    }

    private async Task<IpcAck> CopyConfigAsync(IReadOnlyList<string> args, CancellationToken ct)
    {
        if (args.Count < 2)
        {
            return Fail();
        }

        var source = await _store.GetConfigTextAsync(args[0], ct).ConfigureAwait(false);
        if (source is null)
        {
            return new IpcAck(false, $"'{args[0]}' not found");
        }

        if (await _store.ConfigExistsAsync(args[1], ct).ConfigureAwait(false))
        {
            return new IpcAck(false, $"'{args[1]}' already exists");
        }

        await _store.SaveConfigAsync(args[1], source, ct).ConfigureAwait(false);
        await PushAsync(ct).ConfigureAwait(false);
        return Ok();
    }

    private async Task<IpcAck> SelectTargetAsync(IReadOnlyList<string> args, CancellationToken ct)
    {
        var config = args.Count > 0 && args[0].Length > 0 ? args[0] : null;
        if (config is not null && !await _store.ConfigExistsAsync(config, ct).ConfigureAwait(false))
        {
            return NotFound(config);
        }

        // Nothing selected leaves nothing to run: the tunnel bound to the old target goes down with it.
        if (config is null && _tunnel.Running)
        {
            await SetConnectionAsync("disconnect", ct).ConfigureAwait(false);
        }

        await StoreSelectedTargetAsync(config, ct).ConfigureAwait(false);
        await PushAsync(ct).ConfigureAwait(false);
        return Ok();
    }

    // Picks the routing list every config uses; a missing or unparsable id turns routing off.
    private async Task<IpcAck> AssignRoutingAsync(IReadOnlyList<string> args, CancellationToken ct)
    {
        var listId = args.Count > 0 && long.TryParse(args[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) && parsed > 0
            ? parsed
            : (long?)null;
        if (listId is not null && await _store.GetRoutingListAsync(listId.Value, ct).ConfigureAwait(false) is null)
        {
            return NotFound(args[0]);
        }

        await _store.SetSelectedRoutingListAsync(listId, ct).ConfigureAwait(false);
        await ApplyRoutingAsync(ct).ConfigureAwait(false);
        await PushAsync(ct).ConfigureAwait(false);
        return Ok();
    }

    // Hands the edited rules to the running tunnel; what a live session cannot take raises the reconnect banner.
    private async Task ApplyRoutingAsync(CancellationToken ct)
    {
        if (!_tunnel.Running)
        {
            return;
        }

        var routing = await TunnelRouting.LoadAsync(_store, ct).ConfigureAwait(false);
        if (!_tunnel.ApplyRules(routing))
        {
            _restartRequired = true;
            _log.Info("agent", "the edited rules change the tunnel mode; they apply on the next connect");
        }
    }

    private async Task<IpcAck> SetGeoAsync(IReadOnlyList<string> args, CancellationToken ct)
    {
        if (args.Count < 2)
        {
            return Fail();
        }

        if (!await _store.ConfigExistsAsync(args[0], ct).ConfigureAwait(false))
        {
            return NotFound(args[0]);
        }

        var applied = await _geo.ApplyAsync(args[0], IsOn(args[1]), [.. args.Skip(2)], ct).ConfigureAwait(false);
        await PushAsync(ct).ConfigureAwait(false);
        var summary = $"{applied.Rules} rules, {applied.Routes} routes, {applied.Domains} domains";
        return new IpcAck(true, applied.Skipped > 0 ? $"{summary}, {applied.Skipped} tokens ignored" : summary);
    }

    private async Task<IpcAck> SetTransportAsync(IReadOnlyList<string> args, CancellationToken ct)
    {
        if (args.Count < 3)
        {
            return Fail();
        }

        var stored = await _store.GetConfigTransportAsync(args[0], ct).ConfigureAwait(false);
        var port = int.TryParse(args[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedPort) ? parsedPort : 443;
        var host = args.Count > 3 ? args[3] : string.Empty;
        var mtu = args.Count > 4 && int.TryParse(args[4], NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedMtu) ? parsedMtu : stored?.Mtu ?? 1420;
        var ipv6 = args.Count > 5 ? IsOn(args[5]) : stored?.UseIpv6 ?? false;
        await _store.SetConfigTransportAsync(new ConfigTransport(args[0], IsOn(args[1]), host, port, mtu, ipv6), ct).ConfigureAwait(false);
        await PushAsync(ct).ConfigureAwait(false);
        return Ok();
    }

    private async Task<IpcAck> SetConfigDnsAsync(IReadOnlyList<string> args, CancellationToken ct)
    {
        if (args.Count < 1)
        {
            return Fail();
        }

        var servers = args.Count > 1 ? args[1].Trim() : string.Empty;
        if (servers.Length == 0)
        {
            await _store.RemoveConfigDnsAsync(args[0], ct).ConfigureAwait(false);
        }
        else
        {
            await _store.SetConfigDnsAsync(new ConfigDns(args[0], servers), ct).ConfigureAwait(false);
        }

        await PushAsync(ct).ConfigureAwait(false);
        return Ok();
    }

    private async Task<IpcAck> SetConfigExclusionsAsync(IReadOnlyList<string> args, CancellationToken ct)
    {
        if (args.Count < 1)
        {
            return Fail();
        }

        var exclusions = args.Count > 1 ? args[1].Trim() : string.Empty;
        if (exclusions.Length == 0)
        {
            await _store.RemoveConfigExclusionsAsync(args[0], ct).ConfigureAwait(false);
        }
        else
        {
            await _store.SetConfigExclusionsAsync(new ConfigExclusions(args[0], exclusions), ct).ConfigureAwait(false);
        }

        await PushAsync(ct).ConfigureAwait(false);
        return Ok();
    }

    private async Task<IpcAck> ImportBundleAsync(IReadOnlyList<string> args, CancellationToken ct)
    {
        var ack = await _bundles.ImportAsync(args, ct).ConfigureAwait(false);
        await PushAsync(ct).ConfigureAwait(false);
        return ack;
    }

    private async Task<IpcAck> GetGeoEntriesAsync(IReadOnlyList<string> args, CancellationToken ct)
    {
        if (args.Count < 1)
        {
            return Fail();
        }

        var entries = await _geo.EntriesAsync(args[0], ct).ConfigureAwait(false);
        // A limit of 0 asks for the whole category; without one the answer stays a short page.
        var cap = args.Count > 1 && int.TryParse(args[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? (parsed <= 0 ? entries.Count : parsed)
            : 300;
        return new IpcAck(true, JsonSerializer.Serialize(new { total = entries.Count, entries = entries.Take(cap).ToArray() }));
    }

    private async Task<IpcAck> SaveRoutingListAsync(IReadOnlyList<string> args, CancellationToken ct)
    {
        if (args.Count < 2)
        {
            return Fail();
        }

        var id = long.TryParse(args[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : 0;
        var name = args[1].Trim();
        if (name.Length == 0)
        {
            return Fail();
        }

        // The name column is unique: a clash used to surface as a raw SQLite error from the insert.
        var lists = await _store.ListRoutingListsAsync(ct).ConfigureAwait(false);
        if (lists.Any(l => l.Id != id && string.Equals(l.Name, name, StringComparison.Ordinal)))
        {
            return new IpcAck(false, IpcMessage.Key("Agent_RoutingListNameTaken", name));
        }

        var saved = await _geo.ApplyToRoutingListAsync(id, name, [.. args.Skip(2)], ct).ConfigureAwait(false);
        await ApplyRoutingAsync(ct).ConfigureAwait(false);
        await PushAsync(ct).ConfigureAwait(false);
        return new IpcAck(true, saved.ToString(CultureInfo.InvariantCulture));
    }

    private async Task<IpcAck> GetRoutingListAsync(IReadOnlyList<string> args, CancellationToken ct)
    {
        if (args.Count < 1 || !long.TryParse(args[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var id))
        {
            return Fail();
        }

        var list = await _store.GetRoutingListAsync(id, ct).ConfigureAwait(false);
        return list is null
            ? Fail()
            : new IpcAck(true, string.Join('\n', list.Rules.Select(GeoConfigurator.FormatWithRole)));
    }

    private async Task<IpcAck> RemoveRoutingListAsync(IReadOnlyList<string> args, CancellationToken ct)
    {
        if (args.Count < 1 || !long.TryParse(args[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var id))
        {
            return Fail();
        }

        await _store.RemoveRoutingListAsync(id, ct).ConfigureAwait(false);
        await _store.RemoveRoutingSettingsAsync(id, ct).ConfigureAwait(false);
        await ApplyRoutingAsync(ct).ConfigureAwait(false);
        await PushAsync(ct).ConfigureAwait(false);
        return Ok();
    }

    private async Task<IpcAck> GetRoutingSettingsAsync(IReadOnlyList<string> args, CancellationToken ct)
    {
        if (args.Count < 1 || !long.TryParse(args[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var id))
        {
            return Fail();
        }

        var settings = await _store.GetRoutingSettingsAsync(id, ct).ConfigureAwait(false);
        return new IpcAck(true, JsonSerializer.Serialize(new
        {
            exclusions = settings?.Exclusions ?? string.Empty,
            allUdp = settings?.AllUdp ?? false,
            mode = settings?.Mode ?? "split",
            useGlobalProxy = settings?.UseGlobalProxy ?? false,
        }));
    }

    private async Task<IpcAck> SetRoutingSettingsAsync(IReadOnlyList<string> args, CancellationToken ct)
    {
        if (args.Count < 1 || !long.TryParse(args[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var id))
        {
            return Fail();
        }

        var exclusions = args.Count > 1 ? args[1] : string.Empty;
        var allUdp = args.Count > 2 && IsOn(args[2]);
        var useGlobalProxy = args.Count > 4 && IsOn(args[4]);
        if (exclusions.Length == 0 && !allUdp && !useGlobalProxy)
        {
            await _store.RemoveRoutingSettingsAsync(id, ct).ConfigureAwait(false);
        }
        else
        {
            await _store.SetRoutingSettingsAsync(new RoutingSettings(id, exclusions, allUdp, useGlobalProxy ? "full" : "split", useGlobalProxy), ct).ConfigureAwait(false);
        }

        await ApplyRoutingAsync(ct).ConfigureAwait(false);
        await PushAsync(ct).ConfigureAwait(false);
        return Ok();
    }

    private async Task<IpcAck> AddSourceAsync(IReadOnlyList<string> args, CancellationToken ct)
    {
        if (args.Count < 2)
        {
            return Fail();
        }

        var sources = await _store.ListGeoSourcesAsync(ct).ConfigureAwait(false);
        var position = sources.Count + 1;
        var name = $"{args[0]}-{position}";
        await _store.SaveGeoSourceAsync(new GeoSource(name, args[0], args[1], position), ct).ConfigureAwait(false);
        await PushAsync(ct).ConfigureAwait(false);
        return await UpdateSourcesAsync(name, ct).ConfigureAwait(false);
    }

    private async Task<IpcAck> EditSourceAsync(IReadOnlyList<string> args, CancellationToken ct)
    {
        if (args.Count < 3)
        {
            return Fail();
        }

        var sources = await _store.ListGeoSourcesAsync(ct).ConfigureAwait(false);
        var existing = sources.FirstOrDefault(s => string.Equals(s.Name, args[0], StringComparison.Ordinal));
        if (existing is null)
        {
            return new IpcAck(false, $"'{args[0]}' not found");
        }

        await _store.SaveGeoSourceAsync(new GeoSource(existing.Name, args[1], args[2], existing.Position), ct).ConfigureAwait(false);
        await PushAsync(ct).ConfigureAwait(false);
        return await UpdateSourcesAsync(existing.Name, ct).ConfigureAwait(false);
    }

    private async Task<IpcAck> RemoveSourceAsync(IReadOnlyList<string> args, CancellationToken ct)
    {
        if (args.Count < 1)
        {
            return Fail();
        }

        await _store.RemoveGeoSourceAsync(args[0], ct).ConfigureAwait(false);
        _sourceErrors.Remove(args[0]);
        await PushAsync(ct).ConfigureAwait(false);
        return Ok();
    }

    // Downloads one source, or every source when no name is given, then re-materializes the routing lists.
    private async Task<IpcAck> UpdateSourcesAsync(string? name, CancellationToken ct)
    {
        var sources = await _store.ListGeoSourcesAsync(ct).ConfigureAwait(false);
        var targets = name is null ? sources : sources.Where(s => string.Equals(s.Name, name, StringComparison.Ordinal)).ToList();
        if (targets.Count == 0)
        {
            return new IpcAck(false, name is null ? "no geo sources configured" : $"'{name}' not found");
        }

        var failures = new List<string>();
        foreach (var source in targets)
        {
            _updatingSources.Add(source.Name);
        }

        await PushAsync(ct).ConfigureAwait(false);
        foreach (var source in targets)
        {
            try
            {
                var meta = await _geoUpdater.UpdateAsync(source, null, ct).ConfigureAwait(false);
                _sourceErrors[source.Name] = null;
                _log.Info("geo", $"{source.Name}: {meta.CategoryCount} categories");
            }
            catch (Exception ex)
            {
                _sourceErrors[source.Name] = ex.Message;
                failures.Add($"{source.Name}: {ex.Message}");
                _log.Error("geo", $"{source.Name} download failed", ex);
            }
            finally
            {
                _updatingSources.Remove(source.Name);
            }
        }

        await _geo.RematerializeAllRoutingListsAsync(ct).ConfigureAwait(false);
        await PushAsync(ct).ConfigureAwait(false);
        return failures.Count == 0
            ? new IpcAck(true, $"{targets.Count} source(s) updated")
            : new IpcAck(false, string.Join('\n', failures));
    }

    private async Task<IpcAck> SetSettingAsync(IReadOnlyList<string> args, CancellationToken ct)
    {
        if (args.Count < 2)
        {
            return Fail();
        }

        switch (args[0])
        {
            case LogLevelKey:
                _logLevel = KnownLogLevel(args[1]);
                _log.SetCaptureLevel(_logLevel);
                await _store.SetSettingAsync(LogLevelKey, _logLevel, ct).ConfigureAwait(false);
                break;
            case RouteLogKey:
                _routeLog = IsOn(args[1]);
                _log.SetRouteLog(_routeLog);
                await _store.SetSettingAsync(RouteLogKey, _routeLog ? "on" : "off", ct).ConfigureAwait(false);
                break;
            case SurviveRebootKey:
                _surviveReboot = IsOn(args[1]);
                await _store.SetSettingAsync(SurviveRebootKey, _surviveReboot ? "on" : "off", ct).ConfigureAwait(false);
                break;
            case PeriodicReconnectKey:
                _periodicReconnect = IsOn(args[1]);
                await _store.SetSettingAsync(PeriodicReconnectKey, _periodicReconnect ? "on" : "off", ct).ConfigureAwait(false);
                break;
            case ReconnectIntervalKey:
                _reconnectIntervalSeconds = ReconnectInterval(args[1]);
                await _store.SetSettingAsync(ReconnectIntervalKey, _reconnectIntervalSeconds.ToString(CultureInfo.InvariantCulture), ct).ConfigureAwait(false);
                break;
            case RouteTtlKey:
                if (!SettingKeys.TryParseRouteTtl(args[1], out var ttlSeconds))
                {
                    return Fail();
                }

                _routeTtlSeconds = ttlSeconds;
                _tunnel.SetRouteTtl(ttlSeconds);
                await _store.SetSettingAsync(RouteTtlKey, _routeTtlSeconds.ToString(CultureInfo.InvariantCulture), ct).ConfigureAwait(false);
                break;
            default:
                await _store.SetSettingAsync(args[0], args[1], ct).ConfigureAwait(false);
                break;
        }

        await PushAsync(ct).ConfigureAwait(false);
        return Ok();
    }

    private async Task<IpcAck> ReadLogAsync(IReadOnlyList<string> args, CancellationToken ct)
    {
        if (args.Count < 1 || !IsKnownLogTable(args[0]))
        {
            return Fail();
        }

        var limit = args.Count > 1 && int.TryParse(args[1], out var parsedLimit) ? Math.Clamp(parsedLimit, 1, 2000) : 400;
        var beforeId = args.Count > 2 && long.TryParse(args[2], out var parsedBefore) && parsedBefore > 0 ? parsedBefore : (long?)null;
        var minLevelId = args[0] == SqliteLogStore.AgentTable && args.Count > 3 ? AgentLog.MinId(args[3]) : null;
        var search = args.Count > 4 && args[4].Length > 0 ? args[4] : null;

        var page = await _log.QueryAsync(args[0], beforeId, limit, minLevelId, search, ct).ConfigureAwait(false);
        return new IpcAck(true, JsonSerializer.Serialize(new
        {
            lines = page.Rows.Select(AgentLog.Render).ToList(),
            firstId = page.Rows.Count > 0 ? page.Rows[^1].Id : 0L,
            hasOlder = page.HasOlder,
            matchCount = search is null ? 0 : await _log.CountAsync(args[0], minLevelId, search, ct).ConfigureAwait(false),
        }));
    }

    private async Task<IpcAck> ClearLogAsync(IReadOnlyList<string> args, CancellationToken ct)
    {
        if (args.Count < 1 || !IsKnownLogTable(args[0]))
        {
            return Fail();
        }

        await _log.ClearAsync(args[0], ct).ConfigureAwait(false);
        return Ok();
    }

    private async Task<IpcAck> ExportLogAsync(IReadOnlyList<string> args, CancellationToken ct)
    {
        if (args.Count < 1 || !IsKnownLogTable(args[0]))
        {
            return Fail();
        }

        return new IpcAck(true, await _log.RenderAllAsync(args[0], ct).ConfigureAwait(false));
    }

    private async Task<IpcAck> GetRuntimeConfigAsync(CancellationToken ct)
    {
        var configName = _selectedTarget;
        var report = new StringBuilder();
        report.Append("library      : ").Append(AgentPaths.Root).Append('\n');
        report.Append("config       : ").Append(configName ?? "(none)").Append('\n');
        report.Append("status       : ").Append(_boundStatus).Append('\n');
        report.Append("active       : ").Append(_tunnel.Running ? "yes" : "no").Append('\n');
        if (configName is not null && await _store.GetConfigTextAsync(configName, ct).ConfigureAwait(false) is { } text)
        {
            report.Append("endpoint     : ").Append(WgConfigEditor.GetEndpoint(text) ?? "(none)").Append('\n');
            report.Append("addresses    : ").Append(string.Join(", ", WgConfigEditor.GetAddresses(text))).Append('\n');
            report.Append("allowed ips  : ").Append(string.Join(", ", WgConfigEditor.GetAllowedIps(text))).Append('\n');
            report.Append("mtu          : ").Append(WgConfigEditor.GetMtu(text)).Append('\n');
        }

        var selectedList = await _store.GetSelectedRoutingListAsync(ct).ConfigureAwait(false) is { } selectedId
            ? await _store.GetRoutingListAsync(selectedId, ct).ConfigureAwait(false)
            : null;
        report.Append("routing list : ").Append(selectedList?.Name ?? "(off)").Append('\n');
        if (selectedList is not null)
        {
            report.Append("  geoip routes   : ").Append(selectedList.Routes.Count).Append('\n');
            report.Append("  geosite domains: ").Append(selectedList.Domains.Count).Append('\n');
        }

        report.Append("mode         : ").Append(_tunnel.Mode).Append('\n');
        if (_tunnel.Running)
        {
            report.Append("advertised   : ").Append(string.Join(", ", _tunnel.Advertised)).Append('\n');
            report.Append("live routes  : ").Append(_tunnel.Tunneled.Count).Append(" tunneled, ").Append(_tunnel.Bypassed.Count).Append(" bypassed").Append('\n');
            report.Append("route ttl    : ").Append(_routeTtlSeconds).Append(" s idle").Append('\n');
        }

        if (_connectFailDetail.Length > 0)
        {
            report.Append("last failure : ").Append(_connectFailDetail).Append('\n');
        }

        report.Append("log level    : ").Append(_logLevel).Append('\n');
        report.Append("route log    : ").Append(_routeLog ? "on" : "off").Append('\n');
        return new IpcAck(true, report.ToString());
    }

    // The destinations the running tunnel holds, one per line, for the support archive.
    private string CacheText()
    {
        var text = new StringBuilder();
        foreach (var host in _tunnel.Tunneled)
        {
            text.Append("tunnel  ").Append(host).Append('\n');
        }

        foreach (var host in _tunnel.Bypassed)
        {
            text.Append("direct  ").Append(host).Append('\n');
        }

        return text.Length == 0 ? "the tunnel holds nothing right now" : text.ToString();
    }

    private async Task<IpcAck> GetCacheEntriesAsync(CancellationToken ct)
    {
        var rows = new List<object>();
        rows.AddRange(_tunnel.Tunneled.Select(host => (object)new { kind = "live", key = host, value = "tunnel" }));
        rows.AddRange(_tunnel.Bypassed.Select(host => (object)new { kind = "live", key = host, value = "direct" }));
        if (await _store.GetSelectedRoutingListAsync(ct).ConfigureAwait(false) is { } listId
            && await _store.GetRoutingListAsync(listId, ct).ConfigureAwait(false) is { } list)
        {
            rows.AddRange(list.Routes.Select(route => (object)new { kind = "proxy", key = route, value = "geoip" }));
            rows.AddRange(list.Domains.Select(domain => (object)new { kind = "domain", key = domain.Value, value = domain.Kind.ToString().ToLowerInvariant() }));
        }

        const int cap = 1000;
        var capped = rows.Count > cap;
        return new IpcAck(true, JsonSerializer.Serialize(new { total = rows.Count, capped, entries = capped ? rows.Take(cap).ToList() : rows }));
    }

    // Asks every source whether its remote file changed, without downloading it.
    private async Task<IpcAck> CheckSourcesAsync(CancellationToken ct)
    {
        var sources = await _store.ListGeoSourcesAsync(ct).ConfigureAwait(false);
        if (sources.Count == 0)
        {
            return new IpcAck(true, IpcMessage.Key("Agent_NoSourcesToCheck"));
        }

        var available = 0;
        foreach (var source in sources)
        {
            if (await CheckOneSourceAsync(source, ct).ConfigureAwait(false) == GeoUpdateChecker.Status.Available)
            {
                available++;
            }
        }

        await PushAsync(ct).ConfigureAwait(false);
        return new IpcAck(true, available == 0
            ? IpcMessage.Key("Agent_CheckedNoUpdates", sources.Count)
            : IpcMessage.Key("Agent_CheckedUpdatesAvailable", sources.Count, available));
    }

    private async Task<IpcAck> CheckSourceAsync(IReadOnlyList<string> args, CancellationToken ct)
    {
        if (args.Count < 1 || args[0].Length == 0)
        {
            return Fail();
        }

        var sources = await _store.ListGeoSourcesAsync(ct).ConfigureAwait(false);
        var source = sources.FirstOrDefault(entry => string.Equals(entry.Name, args[0], StringComparison.Ordinal));
        if (source is null)
        {
            return NotFound(args[0]);
        }

        var status = await CheckOneSourceAsync(source, ct).ConfigureAwait(false);
        await PushAsync(ct).ConfigureAwait(false);
        return new IpcAck(true, status switch
        {
            GeoUpdateChecker.Status.Available => IpcMessage.Key("Agent_SourceUpdateAvailable", source.Name),
            GeoUpdateChecker.Status.UpToDate => IpcMessage.Key("Agent_SourceUpToDate", source.Name),
            _ => IpcMessage.Key("Agent_SourceCheckFailed", source.Name),
        });
    }

    // Checks one source under its own ceiling; an unreachable host must not hold the command gate.
    private async Task<GeoUpdateChecker.Status> CheckOneSourceAsync(GeoSource source, CancellationToken ct)
    {
        try
        {
            using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
            budget.CancelAfter(TimeSpan.FromSeconds(10));
            var status = await _geoChecker.CheckAsync(source, budget.Token).ConfigureAwait(false);
            if (status != GeoUpdateChecker.Status.Unknown)
            {
                _updateAvailable[source.Name] = status == GeoUpdateChecker.Status.Available;
            }

            return status;
        }
        catch (OperationCanceledException)
        {
            return GeoUpdateChecker.Status.Unknown;
        }
        catch (Exception ex)
        {
            _log.Error("geo", $"'{source.Name}' could not be checked for a newer file; the copy at hand stays in use", ex);
            return GeoUpdateChecker.Status.Unknown;
        }
    }

    // Counts the routes a rule set would put into the tunnel; a Linux host carries any number of them.
    private async Task<IpcAck> CountRoutesAsync(IReadOnlyList<string> args, CancellationToken ct)
    {
        if (args.Count < 1)
        {
            return Fail();
        }

        var full = string.Equals(args[0], "full", StringComparison.OrdinalIgnoreCase);
        var draft = await _geo.MaterializeDraftAsync([.. args.Skip(1)], ct).ConfigureAwait(false);
        // A name carries no address until connect, where it resolves and cuts or adds about two routes.
        var names = draft.Domains.Count + draft.DirectDomains.Count + draft.BlockDomains.Count;
        var routes = SystemRoutes.Tunneled(full, draft.Routes, draft.DirectRoutes, draft.BlockRoutes).Count + (names * 2);
        return new IpcAck(true, $"{{\"routes\":{routes.ToString(CultureInfo.InvariantCulture)},\"limit\":0}}");
    }

    // The ladder from the local gateway out to a download through the tunnel.
    private async Task<IpcAck> CheckChannelAsync(CancellationToken ct)
    {
        var config = _selectedTarget;
        if (string.IsNullOrEmpty(config))
        {
            return new IpcAck(false, IpcMessage.Key("Agent_NoConfigSelected"));
        }

        var text = await _store.GetConfigTextAsync(config, ct).ConfigureAwait(false) ?? string.Empty;
        var options = new ChannelProbeOptions(
            config,
            _tunnel.Running,
            LocalGateway.Find(),
            await ResolveEndpointAsync(text, ct).ConfigureAwait(false),
            LinkLossProbe.TargetsFor(WgConfigEditor.GetDns(text), WgConfigEditor.GetAddresses(text)),
            !string.Equals(_tunnel.Mode, "split", StringComparison.OrdinalIgnoreCase),
            true,
            _tunnel.Running ? _handshakeAge : -1,
            _tunnel.Running ? _link.HandshakesPerMinute : -1,
            ConfiguredMtu: WgConfigEditor.GetMtu(text));

        var report = await ChannelProbe.RunAsync(options, ct).ConfigureAwait(false);
        Record(report.Render(), report.Culprit.Length > 0, report.Advice);
        return new IpcAck(true, report.ToPayload());
    }

    // Why one destination goes where it goes, under the rules in force.
    private async Task<IpcAck> CheckTargetAsync(IReadOnlyList<string> args, CancellationToken ct)
    {
        if (args.Count < 1 || string.IsNullOrWhiteSpace(args[0]))
        {
            return new IpcAck(false, "check-target requires a domain, an address, an app token or a geo rule");
        }

        var list = await _store.GetSelectedRoutingListAsync(ct).ConfigureAwait(false) is { } listId
            ? await _store.GetRoutingListAsync(listId, ct).ConfigureAwait(false)
            : null;
        var split = !string.Equals(_tunnel.Mode, "full", StringComparison.OrdinalIgnoreCase);
        var report = await new TargetInspector(list, split)
            .InspectAsync(args[0], _selectedTarget ?? string.Empty, new TargetProbes(Held), ct)
            .ConfigureAwait(false);

        Record(report.Render(), report.VerdictKey != TargetVerdicts.Proxy);
        return new IpcAck(true, report.ToPayload());
    }

    // What the running tunnel holds for an address right now.
    private string? Held(System.Net.IPAddress address)
    {
        var value = address.ToString();
        if (_tunnel.Tunneled.Contains(value))
        {
            return "routed into the tunnel right now";
        }

        return _tunnel.Bypassed.Contains(value) ? "routed past the tunnel right now" : null;
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
    private static async Task<string?> ResolveEndpointAsync(string text, CancellationToken ct)
    {
        var endpoint = WgConfigEditor.GetEndpoint(text);
        if (string.IsNullOrEmpty(endpoint))
        {
            return null;
        }

        var colon = endpoint.LastIndexOf(':');
        var host = (colon > 0 ? endpoint[..colon] : endpoint).Trim('[', ']');
        if (System.Net.IPAddress.TryParse(host, out var parsed))
        {
            return parsed.ToString();
        }

        try
        {
            var addresses = await System.Net.Dns.GetHostAddressesAsync(host, ct).ConfigureAwait(false);
            return addresses.FirstOrDefault(one => one.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)?.ToString();
        }
        catch (Exception ex) when (ex is System.Net.Sockets.SocketException or ArgumentException or OperationCanceledException)
        {
            return null;
        }
    }

    private async Task<IpcAck> CollectDiagnosticsAsync(CancellationToken ct)
    {
        try
        {
            await _log.FlushAsync(ct).ConfigureAwait(false);
            var path = await _diagnostics.WriteAsync(
                Path.Combine(AgentPaths.Root, "diagnostics"),
                DiagnosticsHeader(),
                AgentLog.Render,
                new BundleSources(
                    null,
                    async token => (await GetRuntimeConfigAsync(token).ConfigureAwait(false)).Message,
                    token => Task.FromResult(CacheText())),
                ct).ConfigureAwait(false);
            _log.Info("agent", $"diagnostics archive written to {path}; keys and credentials in it are masked");
            return new IpcAck(true, path);
        }
        catch (Exception ex)
        {
            _log.Error("agent", "the diagnostics archive could not be built; nothing was written", ex);
            return new IpcAck(false, IpcMessage.Key("Agent_DiagnosticsFailed", ex.Message));
        }
    }

    // Opens the diagnostics summary with the build, the machine and the agent's own state.
    private string DiagnosticsHeader()
    {
        var sb = new StringBuilder();
        sb.AppendLine("AmneziaGeo diagnostics");
        sb.AppendLine($"generated:       {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}");
        sb.AppendLine($"app version:     {AgentBuild.Version}");
        sb.AppendLine($"os:              {RuntimeInformation.OSDescription} ({RuntimeInformation.OSArchitecture})");
        sb.AppendLine($"runtime:         {RuntimeInformation.FrameworkDescription}");
        sb.AppendLine($"library:         {AgentPaths.Root}");
        sb.AppendLine();
        sb.AppendLine("[settings]");
        sb.AppendLine($"log level:       {_logLevel}");
        sb.AppendLine($"routing log:     {(_routeLog ? "on" : "off")}");
        sb.AppendLine($"route ttl:       {_routeTtlSeconds}s");
        sb.AppendLine($"survive reboot:  {(_surviveReboot ? "on" : "off")}");
        sb.AppendLine($"reconnect:       {(_periodicReconnect ? $"every {_reconnectIntervalSeconds}s" : "off")}");
        sb.AppendLine();
        sb.AppendLine("[state]");
        sb.AppendLine($"selected target: {_selectedTarget ?? "-"}");
        sb.AppendLine($"bound target:    {_boundTarget ?? "-"}");
        sb.AppendLine($"status:          {_boundStatus}");
        sb.AppendLine($"connect failed:  {_connectFailed}");
        sb.AppendLine();
        return sb.ToString();
    }

    private async Task<ConfigEntry> BuildConfigEntryAsync(string name, CancellationToken ct)
    {
        var text = await _store.GetConfigTextAsync(name, ct).ConfigureAwait(false) ?? string.Empty;
        var geo = await _store.GetTunnelGeoAsync(name, ct).ConfigureAwait(false);
        var transport = await _store.GetConfigTransportAsync(name, ct).ConfigureAwait(false);
        var dns = await _store.GetConfigDnsAsync(name, ct).ConfigureAwait(false);
        var exclusions = await _store.GetConfigExclusionsAsync(name, ct).ConfigureAwait(false);
        var bound = string.Equals(name, _boundTarget, StringComparison.Ordinal);
        var handshake = bound ? _handshakeAge : -1;
        var reading = bound ? _link : LinkReading.Empty;
        return new ConfigEntry(
            name,
            WgConfigEditor.GetEndpoint(text) ?? string.Empty,
            geo?.GeoSplit ?? false,
            StatusFor(name),
            geo is null ? [] : [.. geo.Rules.Select(GeoConfigurator.Format)],
            transport?.UseWebSocket ?? false,
            transport?.WebSocketHost ?? string.Empty,
            transport?.WebSocketPort ?? 443,
            dns?.Servers ?? string.Empty,
            exclusions?.Exclusions ?? string.Empty,
            transport?.Mtu ?? WgConfigEditor.GetMtu(text),
            transport?.UseIpv6 ?? false,
            handshake,
            reading.RxBitsPerSecond,
            reading.TxBitsPerSecond,
            reading.HandshakesPerMinute,
            reading.LossPercent);
    }

    private async Task<IReadOnlyList<SourceEntry>> BuildSourcesAsync(CancellationToken ct)
    {
        var metaByName = (await _store.ListGeoFilesAsync(ct).ConfigureAwait(false)).ToDictionary(m => m.Name, StringComparer.Ordinal);
        var entries = new List<SourceEntry>();
        foreach (var source in await _store.ListGeoSourcesAsync(ct).ConfigureAwait(false))
        {
            var meta = metaByName.GetValueOrDefault(source.Name);
            var updating = _updatingSources.Contains(source.Name);
            entries.Add(new SourceEntry(
                source.Name,
                source.Kind,
                source.Url,
                meta?.UpdatedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture),
                meta?.CategoryCount ?? 0,
                updating,
                0,
                _updateAvailable.GetValueOrDefault(source.Name),
                updating ? null : _sourceErrors.GetValueOrDefault(source.Name)));
        }

        return entries;
    }

    private async Task StoreSelectedTargetAsync(string? target, CancellationToken ct)
    {
        _selectedTarget = target;
        await _store.SetSettingAsync(StateKeys.SelectedTarget, target ?? string.Empty, ct).ConfigureAwait(false);
    }

    private string StatusFor(string name)
    {
        return string.Equals(name, _boundTarget, StringComparison.Ordinal) ? _boundStatus : ConnectionStatus.Idle;
    }

    private async Task PushAsync(CancellationToken ct)
    {
        if (StateChanged is { } handler)
        {
            await handler(ct).ConfigureAwait(false);
        }
    }

    // The machine's connected local subnets, offered to the exclusions editor.
    private static IEnumerable<string> LocalSubnets()
    {
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback)
            {
                continue;
            }

            foreach (var address in nic.GetIPProperties().UnicastAddresses)
            {
                if (address.Address.AddressFamily == AddressFamily.InterNetwork && address.PrefixLength > 0)
                {
                    yield return $"{address.Address}/{address.PrefixLength}";
                }
            }
        }
    }

    private static bool IsKnownLogTable(string name) => name is SqliteLogStore.AgentTable or SqliteLogStore.RoutesTable or SqliteLogStore.ChecksTable;

    // Reconnect interval in seconds, clamped to a sane window.
    private static int ReconnectInterval(string? value) =>
        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds)
            ? Math.Clamp(seconds, 5, 3600)
            : 30;

    private static bool IsOn(string? token) => token is "on" or "true" or "1";

    private static string KnownLogLevel(string token)
    {
        return token switch
        {
            "none" or "trace" or "debug" or "info" or "warning" or "error" => token,
            _ => "error",
        };
    }

    private static IpcAck Ok() => new(true, string.Empty);

    private static IpcAck Fail() => new(false, "malformed command");

    private static IpcAck NotFound(string name) => new(false, $"'{name}' not found");

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        StopLossProbe();
        _updater.Dispose();
        _tunnel.Dispose();
        _geoHttp.Dispose();
        _httpClient.Dispose();
        _commandGate.Dispose();
        _store.ClearPool();
    }
}
