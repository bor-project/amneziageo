using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using AmneziaGeo.Decl;
using AmneziaGeo.Geo;
using AmneziaGeo.Ipc;
using Microsoft.Extensions.Logging;

namespace AmneziaGeo.Windows.App;

/// <summary>
/// Runs the selected config and re-runs on each change.
/// </summary>
internal sealed class ConfigRunner(
    ServiceManager serviceManager,
    UapiClient uapi,
    NetworkReconciler reconciler,
    SettingsStore settingsStore,
    AgentControl control,
    ActiveTunnelScope activeScope,
    ILogger<ConfigRunner> logger)
{
    private IStateStore store => activeScope.Store;
    private ConfigRepository configRepo => activeScope.ConfigRepo;

    private static readonly TimeSpan _livenessPoll = TimeSpan.FromSeconds(5);

    // No-handshake/no-rx window: data-driven unreachable signal.
    private static readonly TimeSpan _noResponseWindow = TimeSpan.FromSeconds(12);

    // Stop budget before the tunnel process is killed outright.
    private static readonly TimeSpan _stopTimeout = TimeSpan.FromSeconds(15);

    // Interval between re-issued stop requests.
    private static readonly TimeSpan _stopRetry = TimeSpan.FromSeconds(1);

    private string _config = string.Empty;
    private AppSettings _settings = new();

    // Liveness tracking. -1 forces the first poll after (re)connect to seed the baseline rather than count as
    // progress.
    private long _lastRxBytes = -1;
    private long _lastTxBytes = -1;

    // The ladder a link that has stopped carrying is repaired by. The rungs below the reconnect leave the
    // session, its routes, its DNS and its firewall standing, so a NAT that dropped the mapping or a server that
    // moved costs a second instead of a full bring-up.
    private static readonly RecoveryStep[] _ladder = [RecoveryStep.Rebind, RecoveryStep.Resolve, RecoveryStep.Restart];
    private LinkRecovery _recovery = new(_ladder);

    // Launches that failed in a row. A busy machine misses the window once or twice; one that keeps missing it
    // has something wrong with the tunnel process itself, and the user hears that instead of dialling forever.
    private int _launchStreak;
    private const int LaunchStreakLimit = 5;

    // The service manager's own start deadline; the service is still coming up when it elapses.
    private const int ScStartTimeout = 1053;

    // Throughput and handshake rate of the running tunnel, and the journal line that keeps their history.
    private readonly LinkMeter _meter = new();
    private static readonly TimeSpan _linkLogInterval = TimeSpan.FromSeconds(60);
    private DateTimeOffset _linkLoggedAt;
    private bool _churnLogged;

    // Echoes inside the tunnel: the only thing that says what it loses, the peer counters keeping no trace of a
    // packet that never arrived.
    private LinkLossProbe? _loss;

    /// <summary>
    /// Runs sessions per target change.
    /// </summary>
    public async Task RunAsync(string initial, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var changeToken = control.ChangeToken;
            using (var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, changeToken))
            {
                var config = await ResolveAsync(initial, ct);
                _config = config;

                if (!control.Running)
                {
                    await TeardownForDisconnectAsync(config);
                    await IdleAsync(linked.Token);
                    continue;
                }

                try
                {
                    await RunSessionAsync(config, linked.Token);
                }
                catch (OperationCanceledException)
                {
                    // Shutdown ends the supervisor; a change signal just re-runs the loop.
                    if (ct.IsCancellationRequested)
                    {
                        return;
                    }
                }
                catch (Exception ex)
                {
                    // Don't let a transient fault kill the supervisor.
                    logger.LogError(ex, "the tunnel session stopped with an error ({Reason}); the connection is torn down and dialled again", ex.Message);
                    Stop(config);
                    await SetStateAsync("disconnected");
                    await DelayAsync(_livenessPoll, linked.Token);
                }
            }
        }
    }

    private async Task<string> ResolveAsync(string initial, CancellationToken ct)
    {
        // Latch the running target so a new selection doesn't switch until reconnect; teardown follows the same
        // latch, otherwise a disconnect after a mid-session switch stops the service of the wrong config.
        var name = control.RunningTarget ?? control.Target ?? initial;
        if (await configRepo.ExistsAsync(name, ct))
        {
            return name;
        }

        // Broken binding: clear the dangling selection and idle.
        if (!string.IsNullOrEmpty(name))
        {
            await store.SetSettingAsync(AgentControl.SelectedTargetKey, string.Empty, ct);
            control.ClearTarget();
            logger.LogInformation("the selected configuration '{Config}' no longer exists; the selection is cleared and nothing will connect until you pick another one", name);
        }

        return string.Empty;
    }

    private static async Task IdleAsync(CancellationToken token)
    {
        try
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task RunSessionAsync(string config, CancellationToken ct)
    {
        _settings = await settingsStore.LoadAsync(ct);

        if (string.IsNullOrEmpty(config))
        {
            await SetStateAsync("disconnected");

            // Fail the connect instead of perpetual "connecting…".
            if (control.Running)
            {
                control.FailConnect(ConnectFailureReason.NoTargetSelected, string.Empty);
            }

            await IdleAsync(ct);
            return;
        }

        if (!await configRepo.ExistsAsync(config, ct))
        {
            logger.LogError("configuration {Config} is no longer in the library; the connection cannot start until it is imported again", config);
            await SetStateAsync("disconnected");

            // Missing .conf: fail the connect.
            if (control.Running)
            {
                control.FailConnect(ConnectFailureReason.ConfigMissing, string.Empty);
            }

            await IdleAsync(ct);
            return;
        }

        await ProjectRoutingAsync(config, ct);
        ReapForeignTunnels([config]);
        Stop(config);

        _launchStreak = 0;
        await SetStateAsync("connecting");
        if (!await ConnectWithRetryAsync(config, ct))
        {
            return;
        }

        logger.LogInformation("connected through {Config}; traffic now follows the routing rules", config);
        await SetStateAsync("connected");

        _lastRxBytes = -1;
        _lastTxBytes = -1;
        _recovery = new LinkRecovery(_ladder, _settings.DeadThresholdSeconds);
        await StartLossProbeAsync(config, ct);

        try
        {
            while (!ct.IsCancellationRequested)
            {
                await DelayAsync(_livenessPoll, ct);
                if (ct.IsCancellationRequested)
                {
                    break;
                }

                if (Sample(config) is not { } repair)
                {
                    continue;
                }

                if (repair != RecoveryStep.Restart)
                {
                    await RepairAsync(config, repair, ct);
                    continue;
                }

                logger.LogWarning("{Config}: {Reason}, and the repairs that keep the session standing did not bring it back; reconnecting now (attempt {Attempt})",
                    config, _recovery.Reason, _recovery.Attempt);
                _lastRxBytes = -1;
                _lastTxBytes = -1;
                await SetStateAsync("connecting");
                Stop(config);

                // A live config rename may have moved the config; re-resolve and re-project before re-dialing.
                var current = await ReresolveConfigAsync(config, ct);
                if (!string.Equals(current, config, StringComparison.Ordinal))
                {
                    logger.LogInformation("configuration {Old} was renamed to {New} while connected; reconnecting under the new name", config, current);
                    await ProjectRoutingAsync(current, ct);
                    config = current;
                }

                if (!await ConnectWithRetryAsync(config, ct))
                {
                    return;
                }

                await SetStateAsync("connected");
                _lastRxBytes = -1;
                _lastTxBytes = -1;

                // The session that follows is a new one: it is judged from the bottom of the ladder again.
                _recovery.Reset();
                _loss?.Reset();
            }
        }
        finally
        {
            // A user disconnect announces "disconnecting", then tears down and reports the outcome (clean, or a
            // stuck teardown kept as connected); a re-run (reconfigure/re-dial) just drops to disconnected.
            if (!control.Running)
            {
                await SetStateAsync("disconnecting");
                await TeardownForDisconnectAsync(config);
            }
            else
            {
                Stop(config);
                await SetStateAsync("disconnected");
            }
        }
    }

    // Dials with retry. Transient/network failures keep the connection desired and retry - a capped backoff by
    // default, the configured interval when periodic reconnect is on - while local/config failures latch and
    // stop. Returns true on handshake, false on a fatal failure or a change signal (disconnect/reconfigure).
    // The attempt counter lives on the control so it survives a signal-driven supervisor re-entry.
    private async Task<bool> ConnectWithRetryAsync(string config, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var outcome = await TryConnectAsync(config, ct);
            if (outcome.Ok)
            {
                control.ClearRetry();
                _launchStreak = 0;
                return true;
            }

            if (ct.IsCancellationRequested)
            {
                return false;
            }

            _launchStreak = outcome.Reason == ConnectFailureReason.ServiceLaunchFailed ? _launchStreak + 1 : 0;
            if (!IsTransient(outcome.Reason) || _launchStreak > LaunchStreakLimit)
            {
                logger.LogWarning("could not connect through {Config}: {Reason} {Detail}; a retry does not get past this, so dialling stops here", config, outcome.Reason, outcome.Detail);
                Stop(config);
                await SetStateAsync("disconnected");
                control.FailConnect(outcome.Reason, outcome.Detail);
                return false;
            }

            var attempt = control.NextRetry();
            var delay = RetryDelay(attempt);
            logger.LogWarning("could not reach the server of {Config}: {Reason}; trying again in {Delay}s, attempt {Attempt}",
                config, outcome.Reason, (int)delay.TotalSeconds, attempt);
            await SetStateAsync("connecting");
            await WaitRetryAsync(delay, ct);
        }

        return false;
    }

    // Serves the announced backoff. A network wake ends this wait and nothing else: it must not cancel the
    // change token, or the supervisor re-enters from the top and dials again immediately (#206).
    private async Task WaitRetryAsync(TimeSpan delay, CancellationToken ct)
    {
        var wake = control.BeginRetryWait();
        try
        {
            using (var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, wake))
            {
                await DelayAsync(delay, linked.Token);
            }
        }
        finally
        {
            control.EndRetryWait();
        }
    }

    // A transient failure is a network/server condition worth retrying; the rest are local/config faults that
    // need user action. WireGuard-over-UDP cannot tell "server unreachable" from "keys rejected" (both silence
    // the handshake), so NoHandshake counts as transient. A tunnel service that was too slow to answer counts
    // as well: a machine that has just booted misses the window and comes up on the next attempt (#247).
    private static bool IsTransient(ConnectFailureReason reason) => reason switch
    {
        ConnectFailureReason.NoHandshake or ConnectFailureReason.UnderlayUnreachable
            or ConnectFailureReason.Timeout or ConnectFailureReason.ServiceLaunchFailed
            or ConnectFailureReason.Unknown => true,
        _ => false,
    };

    // Wait before the next attempt: the configured periodic interval when auto-reconnect is on, else a capped
    // exponential backoff (5, 10, 20, 40, 60s).
    private TimeSpan RetryDelay(int attempt)
    {
        if (_settings.PeriodicReconnect && _settings.PeriodicReconnectIntervalSeconds > 0)
        {
            return TimeSpan.FromSeconds(_settings.PeriodicReconnectIntervalSeconds);
        }

        var steps = Math.Min(Math.Max(attempt - 1, 0), 4);
        return TimeSpan.FromSeconds(Math.Min(60, 5 * (1 << steps)));
    }

    // The retargeted latch after a live rename moved the config.
    private async Task<string> ReresolveConfigAsync(string current, CancellationToken ct)
    {
        var latched = control.RunningTarget;
        if (!string.IsNullOrEmpty(latched) && await configRepo.ExistsAsync(latched, ct))
        {
            return latched;
        }

        return current;
    }

    private async Task ProjectRoutingAsync(string config, CancellationToken ct)
    {
        var listId = await store.GetSelectedRoutingListAsync(ct);
        if (listId is null)
        {
            // No list picked: project full tunnel, override config set-geo.
            await ProjectFullTunnelAsync(config, ct);
            return;
        }

        var list = await store.GetRoutingListAsync(listId.Value, ct);
        if (list is null)
        {
            logger.LogWarning("routing list {Id} no longer exists; until another list is picked, everything goes through the tunnel", listId.Value);
            await ProjectFullTunnelAsync(config, ct);
            return;
        }

        await store.SaveTunnelProjectionAsync(config, true, list.Routes, list.Domains, list.Apps, list.Id, ct);
        logger.LogInformation("routing list '{List}' now applies to {Config}: only what it names goes through the tunnel", list.Name, config);
    }

    private async Task ProjectFullTunnelAsync(string config, CancellationToken ct)
    {
        // geoSplit=false -> full tunnel via config AllowedIPs.
        await store.SaveTunnelProjectionAsync(config, false, [], [], [], null, ct);
        logger.LogInformation("routing rules are off for {Config}: all traffic goes through the tunnel", config);
    }

    private async Task SetStateAsync(string status)
    {
        try
        {
            await store.SaveTunnelStateAsync(new TunnelState(_config, status, DateTimeOffset.UtcNow));
            control.SignalStatus();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "the connection state could not be saved; the app may show an out-of-date state until the next change");
        }
    }

    // Tears the tunnel down for a user disconnect and reports the outcome: a clean "disconnected", or - when the
    // service refuses to stop - a latched disconnect failure that keeps the connected state so the user can retry.
    private async Task TeardownForDisconnectAsync(string config)
    {
        Stop(config);
        // Only RUNNING is a genuine refusal to stop; STOP_PENDING (mapped to "PENDING") is a slow-but-successful
        // stop still in progress, which the next teardown pass and the periodic snapshot resolve to disconnected.
        var state = string.IsNullOrEmpty(config) ? "ABSENT" : serviceManager.QueryState(config);
        if (state == "RUNNING")
        {
            logger.LogWarning("the tunnel of {Config} is still {State} after being asked to stop, so the disconnect did not finish; it stays shown as connected — try again", config, state);
            control.FailDisconnect(state);
            await SetStateAsync("connected");
            return;
        }

        control.ClearDisconnectFail();
        control.ClearRunningTarget();
        await SetStateAsync("disconnected");

        // Return the connect-session transient before the agent idles.
        MemoryReclaim.Trim();
    }

    // Outcome of a single connect attempt with its classified reason.
    private sealed record ConnectOutcome(bool Ok, ConnectFailureReason Reason, string Detail)
    {
        public static readonly ConnectOutcome Success = new(true, ConnectFailureReason.Unknown, string.Empty);
        public static readonly ConnectOutcome Cancelled = new(false, ConnectFailureReason.Unknown, string.Empty);
    }

    private async Task<ConnectOutcome> TryConnectAsync(string member, CancellationToken ct)
    {
        // Clear prior reason so this run's failure isn't stale.
        await store.SetSettingAsync(TunnelPaths.ConnectMessageKey(member), string.Empty, ct);
        await store.SetSettingAsync(TunnelPaths.ConnectReasonKey(member), string.Empty, ct);

        // Hold the tunnel back while the machine has no network of its own. Right after a restart the agent is
        // up seconds before the adapters are, and a tunnel raised into a machine with nowhere to send its
        // handshake only burns the attempt; the network watcher wakes this dial the moment an address appears.
        if (!UnderlayReady())
        {
            logger.LogInformation("{Member}: this machine is not on a network yet, so the tunnel is not started; it is dialled as soon as one is there", member);
            return new ConnectOutcome(false, ConnectFailureReason.UnderlayUnreachable, string.Empty);
        }

        // A service left half-started by an earlier run refuses every start with "already running", so the
        // retry never gets past it and the tunnel never comes up; it is forced down before this attempt.
        if (serviceManager.QueryState(member) == "PENDING")
        {
            logger.LogWarning("{Member}: a tunnel service from an earlier run is stuck starting, so it is forced down first", member);
            StopService(member);
        }

        logger.LogInformation("{Member}: starting the tunnel process", member);
        var created = serviceManager.CreateService(member, activeScope.OwnerRoot);
        var started = serviceManager.StartQuiet(member);
        var startFailed = created != 0 || started != 0;
        if (startFailed)
        {
            logger.LogWarning("{Member}: the tunnel process did not start cleanly (create {Create}: {CreateMsg}; start {Start}: {StartMsg}); waiting for it anyway, the connect fails if it never answers",
                member, created, ScError(created), started, ScError(started));
        }

        var start = DateTimeOffset.UtcNow;
        var deadline = start.AddSeconds(_settings.ConnectTimeoutSeconds);
        var sawService = false;
        var serverSilent = false;
        var lastHeartbeat = start;
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (ct.IsCancellationRequested)
            {
                return ConnectOutcome.Cancelled;
            }

            if (uapi.TryGetPeerStatus(member) is { } status)
            {
                var elapsed = (int)(DateTimeOffset.UtcNow - start).TotalSeconds;
                // Per-poll handshake detail for Debug/Trace.
                logger.LogDebug("{Member}: waiting for the server — last handshake {Hs}s ago, sent {Tx} B, received {Rx} B, {Sec}s into this attempt",
                    member, status.HandshakeSec, status.TxBytes, status.RxBytes, elapsed);
                if (status.HandshakeSec > 0)
                {
                    logger.LogInformation("{Member}: the server answered after {Sec}s, the tunnel is up", member, elapsed);
                    return ConnectOutcome.Success;
                }

                // Service responded: distinguishes launch failure from silent server.
                if (!sawService)
                {
                    sawService = true;
                    logger.LogInformation("{Member}: the tunnel process is running; waiting for the server to answer", member);
                }

                if (DateTimeOffset.UtcNow - lastHeartbeat >= TimeSpan.FromSeconds(4))
                {
                    lastHeartbeat = DateTimeOffset.UtcNow;
                    logger.LogInformation("{Member}: still no answer from the server — sent {Tx} B, received {Rx} B in {Sec}s",
                        member, status.TxBytes, status.RxBytes, elapsed);
                }

                // No rx after the window: server silent, give up.
                if (status is { HandshakeSec: 0, RxBytes: 0 } && DateTimeOffset.UtcNow - start >= _noResponseWindow)
                {
                    logger.LogWarning("{Member}: the server sent nothing back in {Sec}s ({Tx} B went out), so it is unreachable — check the address, the port and whether the server is running",
                        member, (int)_noResponseWindow.TotalSeconds, status.TxBytes);
                    serverSilent = true;
                    break;
                }
            }
            else
            {
                // Service not up yet; Trace only.
                logger.LogTrace("{Member}: tunnel service not responding over UAPI yet ({Sec}s)",
                    member, (int)(DateTimeOffset.UtcNow - start).TotalSeconds);
            }

            await DelayAsync(TimeSpan.FromSeconds(1), ct);
        }

        if (ct.IsCancellationRequested)
        {
            return ConnectOutcome.Cancelled;
        }

        var outcome = await ClassifyFailureAsync(member, sawService, serverSilent, startFailed, created, started, ct);
        // A retryable failure keeps the service installed, so the next attempt only restarts it instead of
        // reinstalling a fresh service on every pass (#206).
        if (IsTransient(outcome.Reason))
        {
            Halt(member);
        }
        else
        {
            Stop(member);
        }

        return outcome;
    }

    // Classifies a failed connect attempt; the service's own stored reason wins over an inferred one.
    private async Task<ConnectOutcome> ClassifyFailureAsync(string member, bool sawService, bool serverSilent, bool startFailed, int created, int started, CancellationToken ct)
    {
        var storedReason = await store.GetSettingAsync(TunnelPaths.ConnectReasonKey(member), ct);
        var storedMessage = await store.GetSettingAsync(TunnelPaths.ConnectMessageKey(member), ct);
        var stored = Enum.TryParse<ConnectFailureReason>(storedReason, out var parsed) ? parsed : ConnectFailureReason.Unknown;

        // A carrier that refused the connection outright silences the handshake exactly like an unreachable
        // server, so the stored reason wins - otherwise a permanent fault is retried forever.
        if (stored == ConnectFailureReason.TransportRejected)
        {
            logger.LogWarning("{Member}: the server's carrier refused the connection ({Message}); retrying will not help until that is fixed on the server", member, storedMessage);
            return new ConnectOutcome(false, stored, TrimDetail(storedMessage));
        }

        if (serverSilent)
        {
            return new ConnectOutcome(false, ConnectFailureReason.NoHandshake, string.Empty);
        }

        // UAPI answered but no handshake before the deadline.
        if (sawService)
        {
            return new ConnectOutcome(false, ConnectFailureReason.Timeout, string.Empty);
        }

        // Service never answered UAPI: prefer the reason it stored, else infer from the sc codes.
        logger.LogWarning(
            "{Member}: the tunnel process never came up in {Sec}s, so it most likely failed to launch (start {Start}: {StartMsg}){Reason}",
            member, _settings.ConnectTimeoutSeconds, started, ScError(started),
            string.IsNullOrWhiteSpace(storedMessage) ? string.Empty : $"; reason: {storedMessage}");

        if (stored != ConnectFailureReason.Unknown)
        {
            return new ConnectOutcome(false, stored, TrimDetail(storedMessage));
        }

        // A start that ran out of the service manager's own patience is the same slow launch as one that
        // answered nothing at all - the process is on its way up and the next attempt finds it. Only a refused
        // create, or a start refused outright, is the user's to fix.
        if (startFailed && (created != 0 || started != ScStartTimeout))
        {
            return new ConnectOutcome(false, ConnectFailureReason.ServiceStartFailed, ScError(started != 0 ? started : created));
        }

        return new ConnectOutcome(false, ConnectFailureReason.ServiceLaunchFailed, started == 0 ? string.Empty : ScError(started));
    }

    // Keep the surfaced detail short; the message never carries secrets but may be long.
    private static string TrimDetail(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return string.Empty;
        }

        var trimmed = message.Trim();
        return trimmed.Length > 160 ? trimmed[..160] : trimmed;
    }

    // Whether this machine has a network of its own: an adapter that is up, is neither loopback nor a tunnel,
    // and carries an address it can be reached at. A gateway is not asked for - a server on the same network
    // needs none - so the address is the whole test. The machine's own access point is not a way out of it.
    private static bool UnderlayReady()
    {
        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.OperationalStatus != OperationalStatus.Up
                || ni.NetworkInterfaceType == NetworkInterfaceType.Loopback
                || RouteManager.IsTunnelAdapter(ni))
            {
                continue;
            }

            try
            {
                foreach (var ua in ni.GetIPProperties().UnicastAddresses)
                {
                    if (IsRoutable(ua.Address))
                    {
                        return true;
                    }
                }
            }
            catch (NetworkInformationException)
            {
            }
        }

        return false;
    }

    // The address shared access takes on the machine's own access point: clients hang off it, and nothing
    // leaves the machine through it.
    private static readonly IPAddress SharedAccessHost = IPAddress.Parse("192.168.137.1");

    // An address the machine gave itself because nothing handed it one is not a network.
    private static bool IsRoutable(IPAddress address)
    {
        if (address.IsIPv6LinkLocal || IPAddress.IsLoopback(address) || address.Equals(SharedAccessHost))
        {
            return false;
        }

        var octets = address.AddressFamily == AddressFamily.InterNetwork ? address.GetAddressBytes() : null;
        return octets is not [169, 254, _, _];
    }

    // sc.exe error code names.
    private static string ScError(int code) => code switch
    {
        0 => "ok",
        2 => "file not found",
        5 => "access denied",
        ScStartTimeout => "service did not report running in time (timeout)",
        1056 => "service already running",
        1060 => "service does not exist",
        1072 => "service marked for deletion",
        1073 => "service already exists",
        _ => $"code {code}",
    };

    // Reads the peer counters into the ladder and returns the repair it asks for. The same reading feeds the
    // screen: the connect control is coloured from the handshake age and the rates.
    private RecoveryStep? Sample(string member)
    {
        if (uapi.TryGetPeerStatus(member) is not { } status)
        {
            // UAPI momentarily unreadable - inconclusive, not a reason to touch a live session.
            return null;
        }

        // Hand the keepalive view to the snapshot: the UI colours the connect control from it. Only a moved
        // step is worth a push; the seconds in between change nothing on screen.
        var handshakeAge = status.HandshakeSec > 0
            ? HandshakeAge.Step(DateTimeOffset.UtcNow.ToUnixTimeSeconds() - status.HandshakeSec)
            : -1;
        if (control.SetHandshakeAge(handshakeAge))
        {
            control.SignalStatus();
        }

        var reading = _meter.Sample(status.RxBytes, status.TxBytes, status.HandshakeSec, _loss?.Percent ?? LinkHealth.LossUnknown, _loss?.RttMs ?? -1);
        LogLink(member, reading);
        if (control.SetLink(reading))
        {
            control.SignalStatus();
        }

        var moved = new LinkSample(
            status.TxBytes > _lastTxBytes,
            status.RxBytes > _lastRxBytes,
            reading.LossPercent,
            reading.HandshakesPerMinute,
            status.HandshakeSec > 0 ? (int)(DateTimeOffset.UtcNow.ToUnixTimeSeconds() - status.HandshakeSec) : 0);
        _lastRxBytes = status.RxBytes;
        _lastTxBytes = status.TxBytes;

        // No handshake recorded yet (e.g. right after a re-dial) - give it time rather than declaring dead.
        if (status.HandshakeSec <= 0)
        {
            return null;
        }

        return _recovery.Sample(moved, Environment.TickCount64);
    }

    // Repairs the link without taking the session down: another source port for a NAT that has dropped the
    // mapping, the endpoint resolved again for a server that has moved. A carried tunnel dials its carrier on
    // loopback, and the address in its config belongs to the carrier - re-pointing the peer at it would take the
    // tunnel off the carrier, so that one is left to the carrier's own watchdog.
    private async Task<bool> RepairAsync(string config, RecoveryStep step, CancellationToken ct)
    {
        if (step == RecoveryStep.Rebind)
        {
            var rebound = uapi.Rebind(config);
            logger.LogWarning("{Config}: {Reason}; binding the tunnel to another source port (attempt {Attempt}){Outcome}",
                config, _recovery.Reason, _recovery.Attempt, rebound ? string.Empty : " - the tunnel would not take it");
            return rebound;
        }

        var text = await store.GetConfigTextAsync(config, ct).ConfigureAwait(false) ?? string.Empty;
        var declared = WgConfigEditor.GetEndpoint(text);
        var key = WgConfigEditor.GetPeerPublicKey(text);
        if (string.IsNullOrEmpty(declared) || string.IsNullOrEmpty(key) || Carried(config))
        {
            return false;
        }

        var resolved = await ResolveEndpointAsync(declared, ct);
        if (resolved is null)
        {
            logger.LogWarning("{Config}: {Reason}, and the server's address {Endpoint} does not resolve; the name itself is unreachable from here",
                config, _recovery.Reason, declared);
            return false;
        }

        var pointed = uapi.SetEndpoint(config, key, resolved);
        logger.LogWarning("{Config}: {Reason}; the server's address was resolved again to {Endpoint} and handed to the tunnel (attempt {Attempt}){Outcome}",
            config, _recovery.Reason, resolved, _recovery.Attempt, pointed ? string.Empty : " - the tunnel would not take it");
        return pointed;
    }

    private bool Carried(string config)
    {
        return uapi.TryGetEndpoint(config) is { } running
            && IPAddress.TryParse(Host(running), out var address)
            && IPAddress.IsLoopback(address);
    }

    // Resolves the endpoint a config declares. A host that is already an address resolves to itself, which costs
    // one rung and nothing else.
    private static async Task<string?> ResolveEndpointAsync(string endpoint, CancellationToken ct)
    {
        var colon = endpoint.LastIndexOf(':');
        if (colon <= 0)
        {
            return null;
        }

        var host = endpoint[..colon].Trim('[', ']');
        var port = endpoint[colon..];
        try
        {
            var addresses = await Dns.GetHostAddressesAsync(host, AddressFamily.InterNetwork, ct).ConfigureAwait(false);
            return addresses.Length == 0 ? null : addresses[0] + port;
        }
        catch (Exception)
        {
            return null;
        }
    }

    // The host half of a "host:port", brackets and all; an address that carries none is returned whole.
    private static string Host(string endpoint)
    {
        var colon = endpoint.LastIndexOf(':');
        return colon <= 0 ? endpoint : endpoint[..colon];
    }

    // Gas any other per-tunnel service before raising this target, so two adapters never fight over routes (#168).
    // Writes the link to the journal: a line a minute while it runs, and a warning when it starts or stops
    // re-establishing the session.
    private void LogLink(string member, LinkReading reading)
    {
        var churning = LinkHealth.Churning(reading.HandshakesPerMinute);
        if (churning != _churnLogged)
        {
            _churnLogged = churning;
            if (churning)
            {
                // Loud enough to survive the default capture floor: this is the record a transient outage leaves.
                logger.LogError("{Member}: the session is re-established {Rate} times a minute, so the link carries almost nothing", member, reading.HandshakesPerMinute);
            }
            else
            {
                logger.LogInformation("{Member}: the session stopped re-establishing", member);
            }
        }

        var now = DateTimeOffset.UtcNow;
        if (now - _linkLoggedAt < _linkLogInterval)
        {
            return;
        }

        _linkLoggedAt = now;
        logger.LogInformation("{Member}: link receives {Rx} kbit/s, sends {Tx} kbit/s, handshakes {Rate}/min, loses {Loss}",
            member, reading.RxBitsPerSecond / 1000, reading.TxBitsPerSecond / 1000, reading.HandshakesPerMinute, LossText(reading.LossPercent));
    }

    // The measured share, or a word for a tunnel that has found nothing inside it to answer an echo.
    private static string LossText(int percent)
    {
        return LinkHealth.LossKnown(percent) ? $"{percent}%" : "nothing that answers";
    }

    // Starts this session's loss probe: a target inside the tunnel is echoed once a second, and what fails to come
    // back is the loss the screen shows. The resolvers follow the peer, which a configuration carving out the
    // local networks routes out of the tunnel: nothing answers there, and the session then measures nothing at all.
    private async Task StartLossProbeAsync(string config, CancellationToken ct)
    {
        var text = await store.GetConfigTextAsync(config, ct).ConfigureAwait(false) ?? string.Empty;
        var probe = new LinkLossProbe(LinkLossProbe.Targets(WgConfigEditor.GetAddresses(text), WgConfigEditor.GetDns(text)));
        _loss = probe;
        _ = Task.Run(() => probe.RunAsync(ct), ct);
    }

    private void ReapForeignTunnels(IReadOnlyCollection<string> keep)
    {
        var reaped = InstallerMaintenance.ReapTransientServices(keep);
        if (reaped.Count > 0)
        {
            logger.LogInformation("removed {Count} tunnel(s) left over from an earlier run ({Names}), so they cannot fight over the routes", reaped.Count, string.Join(", ", reaped));
            reconciler.Reconcile(keep: keep);
        }
    }

    private void Stop(string member)
    {
        control.SetHandshakeAge(-1);
        _meter.Reset();
        _loss?.Reset();
        _churnLogged = false;
        control.SetLink(LinkReading.Empty);

        if (string.IsNullOrEmpty(member))
        {
            return;
        }

        StopService(member);
        serviceManager.DeleteService(member);
        reconciler.Reconcile(member);
    }

    // Stops the tunnel and leaves the service installed for the next retry.
    private void Halt(string member)
    {
        if (string.IsNullOrEmpty(member))
        {
            return;
        }

        StopService(member);
        reconciler.Reconcile(member);
    }

    // Stops the service and waits for it to die. A service still starting refuses the stop, so it is re-issued
    // until it takes; one that outlives the budget is killed, which drops its WFP kill-switch with the process.
    private void StopService(string member)
    {
        serviceManager.StopQuiet(member);
        var deadline = DateTimeOffset.UtcNow + _stopTimeout;
        var lastStop = DateTimeOffset.UtcNow;
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (serviceManager.QueryState(member) is "STOPPED" or "ABSENT")
            {
                return;
            }

            if (DateTimeOffset.UtcNow - lastStop >= _stopRetry)
            {
                lastStop = DateTimeOffset.UtcNow;
                serviceManager.StopQuiet(member);
            }

            Thread.Sleep(300);
        }

        KillService(member);
    }

    private void KillService(string member)
    {
        var pid = serviceManager.QueryPid(member);
        if (pid == 0)
        {
            logger.LogWarning("{Member}: the tunnel did not stop in {Sec}s and its process is already gone; nothing left to end", member, (int)_stopTimeout.TotalSeconds);
            return;
        }

        try
        {
            using (var self = Process.GetCurrentProcess())
            using (var process = Process.GetProcessById((int)pid))
            {
                // The tunnel service runs our own image; a recycled pid must not have its tree killed.
                if (!string.Equals(process.ProcessName, self.ProcessName, StringComparison.OrdinalIgnoreCase))
                {
                    logger.LogWarning("{Member}: process {Pid} is now {Name}, not our tunnel, so it is left alone", member, pid, process.ProcessName);
                    return;
                }

                logger.LogWarning("{Member}: the tunnel did not stop in {Sec}s, ending process {Pid} by force; this also drops its firewall protection", member, (int)_stopTimeout.TotalSeconds, pid);
                process.Kill(entireProcessTree: true);
                process.WaitForExit(5000);
            }
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            logger.LogInformation("{Member}: process {Pid} ended on its own while it was being stopped", member, pid);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "{Member}: process {Pid} could not be ended; the next connect may find the old tunnel still there", member, pid);
        }
    }

    private static async Task DelayAsync(TimeSpan delay, CancellationToken ct)
    {
        try
        {
            await Task.Delay(delay, ct);
        }
        catch (OperationCanceledException)
        {
        }
    }
}
