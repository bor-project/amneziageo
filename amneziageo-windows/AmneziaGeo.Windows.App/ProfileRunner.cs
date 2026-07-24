using System.Diagnostics;
using AmneziaGeo.Decl;
using AmneziaGeo.Ipc;
using Microsoft.Extensions.Logging;

namespace AmneziaGeo.Windows.App;

/// <summary>
/// Runs the profile config and re-runs on each change.
/// </summary>
internal sealed class ProfileRunner(
    ServiceManager serviceManager,
    UapiClient uapi,
    ConfigRepository configRepo,
    NetworkReconciler reconciler,
    SettingsStore settingsStore,
    IStateStore store,
    AgentControl control,
    ILogger<ProfileRunner> logger)
{
    private static readonly TimeSpan _livenessPoll = TimeSpan.FromSeconds(5);

    // No-handshake/no-rx window: data-driven unreachable signal.
    private static readonly TimeSpan _noResponseWindow = TimeSpan.FromSeconds(12);

    // Stop budget before the tunnel process is killed outright.
    private static readonly TimeSpan _stopTimeout = TimeSpan.FromSeconds(15);

    // Interval between re-issued stop requests.
    private static readonly TimeSpan _stopRetry = TimeSpan.FromSeconds(1);

    private Profile _group = null!;
    private AppSettings _settings = new();

    // Liveness tracking. rx progress proves the tunnel is carrying data even mid-rekey; a re-dial only fires
    // after several consecutive dead polls so a single lost handshake on a lossy link can't tear down a live
    // session. -1 forces the first poll after (re)connect to seed the baseline rather than count as progress.
    private long _lastRxBytes = -1;
    private int _deadStreak;
    private const int DeadStreakLimit = 3;

    /// <summary>
    /// Runs sessions per target change.
    /// </summary>
    public async Task RunAsync(Profile initial, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var changeToken = control.ChangeToken;
            using (var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, changeToken))
            {
                var group = await ResolveAsync(initial, ct);
                if (group is null)
                {
                    return;
                }

                _group = group;

                if (!control.Running)
                {
                    await TeardownForDisconnectAsync(group.Config);
                    await IdleAsync(linked.Token);
                    continue;
                }

                try
                {
                    await RunSessionAsync(group, linked.Token);
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
                    logger.LogError(ex, "tunnel session failed: {Reason}; retrying", ex.Message);
                    Stop(group.Config);
                    await SetStateAsync("disconnected");
                    await DelayAsync(_livenessPoll, linked.Token);
                }
            }
        }
    }

    private async Task<Profile?> ResolveAsync(Profile initial, CancellationToken ct)
    {
        // Latch the running target so a new selection doesn't switch until reconnect; teardown follows the same
        // latch, otherwise a disconnect after a mid-session switch stops the service of the wrong config.
        var name = control.RunningTarget ?? control.Target ?? initial.Name;
        var profile = await store.GetProfileAsync(name, ct);
        if (profile is not null)
        {
            return profile;
        }

        if (await configRepo.ExistsAsync(name, ct))
        {
            return new Profile(name, name);
        }

        // Broken binding: clear the dangling selection and idle.
        if (!string.IsNullOrEmpty(name))
        {
            await store.SetSettingAsync(AgentControl.SelectedTargetKey, string.Empty, ct);
            control.ClearTarget();
            logger.LogInformation("target '{Profile}' does not exist; cleared binding, idling", name);
        }

        return new Profile(string.Empty, string.Empty);
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

    private async Task RunSessionAsync(Profile group, CancellationToken ct)
    {
        _settings = await settingsStore.LoadAsync(ct);
        var config = group.Config;

        if (string.IsNullOrEmpty(config))
        {
            // Named-but-config-less warns; nameless idle stays quiet.
            if (!string.IsNullOrEmpty(group.Name))
            {
                logger.LogWarning("profile {Profile} has no configuration", group.Name);
            }

            await SetStateAsync("disconnected");

            // Fail the connect instead of perpetual "connecting…".
            if (control.Running)
            {
                control.FailConnect(ConnectFailureReason.ProfileEmpty, string.Empty);
            }

            await IdleAsync(ct);
            return;
        }

        if (!await configRepo.ExistsAsync(config, ct))
        {
            logger.LogError("missing config: {Config}", config);
            await SetStateAsync("disconnected");

            // Missing .conf: fail the connect.
            if (control.Running)
            {
                control.FailConnect(ConnectFailureReason.ConfigMissing, string.Empty);
            }

            await IdleAsync(ct);
            return;
        }

        await ProjectRoutingAsync(group.Name, config, ct);
        ReapForeignTunnels(config);
        Stop(config);

        await SetStateAsync("connecting");
        if (!await ConnectWithRetryAsync(config, group.Name, ct))
        {
            return;
        }

        logger.LogInformation("connected: {Config} ({Profile})", config, group.Name);
        await SetStateAsync("connected");

        _lastRxBytes = -1;
        _deadStreak = 0;

        try
        {
            while (!ct.IsCancellationRequested)
            {
                await DelayAsync(_livenessPoll, ct);
                if (ct.IsCancellationRequested)
                {
                    break;
                }

                if (!IsAlive(config))
                {
                    if (++_deadStreak < DeadStreakLimit)
                    {
                        logger.LogDebug("config {Config} liveness miss {Streak}/{Limit}", config, _deadStreak, DeadStreakLimit);
                        continue;
                    }

                    logger.LogWarning("config {Config} unreachable ({Streak} consecutive misses); re-dialing", config, _deadStreak);
                    _deadStreak = 0;
                    _lastRxBytes = -1;
                    await SetStateAsync("connecting");
                    Stop(config);
                    if (!await ConnectWithRetryAsync(config, group.Name, ct))
                    {
                        return;
                    }

                    await SetStateAsync("connected");
                    _lastRxBytes = -1;
                }
                else
                {
                    _deadStreak = 0;
                }
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
    private async Task<bool> ConnectWithRetryAsync(string config, string profile, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var outcome = await TryConnectAsync(config, ct);
            if (outcome.Ok)
            {
                control.ClearRetry();
                return true;
            }

            if (ct.IsCancellationRequested)
            {
                return false;
            }

            if (!IsTransient(outcome.Reason))
            {
                logger.LogWarning("connect failed: {Config} ({Profile}) - {Reason} {Detail}", config, profile, outcome.Reason, outcome.Detail);
                Stop(config);
                await SetStateAsync("disconnected");
                control.FailConnect(outcome.Reason, outcome.Detail);
                return false;
            }

            var attempt = control.NextRetry();
            var delay = RetryDelay(attempt);
            logger.LogWarning("connect unreachable: {Config} ({Profile}) - {Reason}; retry #{Attempt} in {Delay}s",
                config, profile, outcome.Reason, attempt, (int)delay.TotalSeconds);
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
    // the handshake), so NoHandshake counts as transient.
    private static bool IsTransient(ConnectFailureReason reason) => reason switch
    {
        ConnectFailureReason.NoHandshake or ConnectFailureReason.UnderlayUnreachable
            or ConnectFailureReason.Timeout or ConnectFailureReason.Unknown => true,
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

    private async Task ProjectRoutingAsync(string profile, string config, CancellationToken ct)
    {
        if (await store.GetProfileAsync(profile, ct) is null)
        {
            // Clear stale projection so a dead routing list doesn't leak into this tunnel.
            await store.ClearTunnelProjectionAsync(config, ct);
            return;
        }

        var (listId, useRouting) = await store.GetProfileRoutingAsync(profile, ct);
        if (!useRouting || listId is null)
        {
            // Routing off: project full tunnel, override config set-geo.
            await ProjectFullTunnelAsync(config, ct);
            return;
        }

        var list = await store.GetRoutingListAsync(listId.Value, ct);
        if (list is null)
        {
            logger.LogWarning("profile {Profile} references missing routing list {Id}", profile, listId.Value);
            await ProjectFullTunnelAsync(config, ct);
            return;
        }

        await store.SaveTunnelProjectionAsync(config, true, list.Routes, list.Domains, list.Apps, list.Id, ct);
        logger.LogInformation("projected routing list '{List}' to {Config}", list.Name, config);
    }

    private async Task ProjectFullTunnelAsync(string config, CancellationToken ct)
    {
        // geoSplit=false -> full tunnel via config AllowedIPs.
        await store.SaveTunnelProjectionAsync(config, false, [], [], [], null, ct);
        logger.LogInformation("projected full tunnel to {Config} (routing off)", config);
    }

    private async Task SetStateAsync(string status)
    {
        try
        {
            await store.SaveProfileStateAsync(new ProfileState(_group.Name, status, DateTimeOffset.UtcNow));
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "state write failed");
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
            logger.LogWarning("disconnect incomplete: tunnel service {Config} still {State} after stop; keeping connected", config, state);
            control.FailDisconnect(state);
            await SetStateAsync("connected");
            return;
        }

        control.ClearDisconnectFail();
        control.ClearRunningTarget();
        await SetStateAsync("disconnected");
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

        logger.LogInformation("connecting {Member}: creating and starting tunnel service", member);
        var created = serviceManager.CreateService(member);
        var started = serviceManager.StartQuiet(member);
        var startFailed = created != 0 || started != 0;
        if (startFailed)
        {
            logger.LogWarning("tunnel service {Member} did not start cleanly: sc create={Create} ({CreateMsg}), sc start={Start} ({StartMsg})",
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
                logger.LogDebug("{Member}: poll - handshake={Hs}s tx={Tx}B rx={Rx}B elapsed={Sec}s",
                    member, status.HandshakeSec, status.TxBytes, status.RxBytes, elapsed);
                if (status.HandshakeSec > 0)
                {
                    logger.LogInformation("{Member}: handshake received in {Sec}s", member, elapsed);
                    return ConnectOutcome.Success;
                }

                // Service responded: distinguishes launch failure from silent server.
                if (!sawService)
                {
                    sawService = true;
                    logger.LogInformation("{Member}: tunnel service responding over UAPI; waiting for handshake", member);
                }

                if (DateTimeOffset.UtcNow - lastHeartbeat >= TimeSpan.FromSeconds(4))
                {
                    lastHeartbeat = DateTimeOffset.UtcNow;
                    logger.LogInformation("{Member}: no handshake yet (sent {Tx} B, received {Rx} B, {Sec}s)",
                        member, status.TxBytes, status.RxBytes, elapsed);
                }

                // No rx after the window: server silent, give up.
                if (status is { HandshakeSec: 0, RxBytes: 0 } && DateTimeOffset.UtcNow - start >= _noResponseWindow)
                {
                    logger.LogWarning("{Member}: server did not answer - no handshake, 0 bytes received in {Sec}s (sent {Tx} B); unreachable",
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
        var storedReason = await store.GetSettingAsync(TunnelPaths.ConnectReasonKey(member), ct);
        var storedMessage = await store.GetSettingAsync(TunnelPaths.ConnectMessageKey(member), ct);
        logger.LogWarning(
            "{Member}: tunnel service never responded over UAPI within {Sec}s - it likely failed to launch (sc start={Start}: {StartMsg}){Reason}",
            member, _settings.ConnectTimeoutSeconds, started, ScError(started),
            string.IsNullOrWhiteSpace(storedMessage) ? string.Empty : $"; reason: {storedMessage}");

        if (Enum.TryParse<ConnectFailureReason>(storedReason, out var specific) && specific != ConnectFailureReason.Unknown)
        {
            return new ConnectOutcome(false, specific, TrimDetail(storedMessage));
        }

        if (startFailed)
        {
            return new ConnectOutcome(false, ConnectFailureReason.ServiceStartFailed, ScError(started != 0 ? started : created));
        }

        return new ConnectOutcome(false, ConnectFailureReason.ServiceLaunchFailed, ScError(started));
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

    // sc.exe error code names.
    private static string ScError(int code) => code switch
    {
        0 => "ok",
        2 => "file not found",
        5 => "access denied",
        1053 => "service did not report running in time (timeout)",
        1056 => "service already running",
        1060 => "service does not exist",
        1072 => "service marked for deletion",
        1073 => "service already exists",
        _ => $"code {code}",
    };

    private bool IsAlive(string member)
    {
        if (uapi.TryGetPeerStatus(member) is not { } status)
        {
            // UAPI momentarily unreadable - inconclusive, not a reason to tear down a live session.
            return true;
        }

        // Data still arriving is definitive liveness, even while a rekey handshake is in flight on a lossy link.
        if (status.RxBytes > _lastRxBytes)
        {
            _lastRxBytes = status.RxBytes;
            return true;
        }

        // No handshake recorded yet (e.g. right after a re-dial) - give it time rather than declaring dead.
        if (status.HandshakeSec <= 0)
        {
            return true;
        }

        var age = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - status.HandshakeSec;
        return age < _settings.DeadThresholdSeconds;
    }

    // Gas any other per-tunnel service before raising this target, so two adapters never fight over routes (#168).
    private void ReapForeignTunnels(string keep)
    {
        var reaped = InstallerMaintenance.ReapTransientServices(keep);
        if (reaped.Count > 0)
        {
            logger.LogInformation("reaped {Count} foreign tunnel service(s) before connect: {Names}", reaped.Count, string.Join(", ", reaped));
            reconciler.Reconcile();
        }
    }

    private void Stop(string member)
    {
        if (string.IsNullOrEmpty(member))
        {
            return;
        }

        StopService(member);
        serviceManager.DeleteService(member);
        reconciler.Reconcile();
    }

    // Stops the tunnel and leaves the service installed for the next retry.
    private void Halt(string member)
    {
        if (string.IsNullOrEmpty(member))
        {
            return;
        }

        StopService(member);
        reconciler.Reconcile();
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
            logger.LogWarning("tunnel service {Member} did not stop in {Sec}s and has no process to kill", member, (int)_stopTimeout.TotalSeconds);
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
                    logger.LogWarning("tunnel service {Member} pid {Pid} belongs to {Name}; not killing", member, pid, process.ProcessName);
                    return;
                }

                logger.LogWarning("tunnel service {Member} did not stop in {Sec}s; killing pid {Pid}", member, (int)_stopTimeout.TotalSeconds, pid);
                process.Kill(entireProcessTree: true);
                process.WaitForExit(5000);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "could not kill tunnel service {Member} (pid {Pid})", member, pid);
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
