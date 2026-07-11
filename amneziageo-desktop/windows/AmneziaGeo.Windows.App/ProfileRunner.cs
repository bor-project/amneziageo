using AmneziaGeo.Decl;
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
                    Stop(group.Config);
                    await SetStateAsync("disconnected");
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
        // Latch the running target so a new selection doesn't switch until reconnect.
        var name = (control.Running ? control.RunningTarget : control.Target) ?? control.Target ?? initial.Name;
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
                control.FailConnect();
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
                control.FailConnect();
            }

            await IdleAsync(ct);
            return;
        }

        await ProjectRoutingAsync(group.Name, config, ct);
        Stop(config);

        await SetStateAsync("connecting");
        if (!await TryConnectAsync(config, ct))
        {
            // Give up and raise the failed notice; supervisor idles after.
            if (!ct.IsCancellationRequested)
            {
                // Surface the service's failure reason if any.
                var reason = await store.GetSettingAsync(TunnelPaths.ConnectMessageKey(config), ct);
                if (string.IsNullOrEmpty(reason))
                {
                    logger.LogWarning("connect failed: {Config} unreachable for {Profile}", config, group.Name);
                }
                else
                {
                    logger.LogWarning("connect failed: {Config} ({Profile}) - {Reason}", config, group.Name, reason);
                }

                Stop(config);
                await SetStateAsync("disconnected");
                control.FailConnect();
            }

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
                    if (!await TryConnectAsync(config, ct))
                    {
                        if (!ct.IsCancellationRequested)
                        {
                            logger.LogWarning("re-dial failed: {Config} unreachable", config);
                            Stop(config);
                            await SetStateAsync("disconnected");
                            control.FailConnect();
                        }

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
            // "disconnecting" only on a real user disconnect, not a re-run.
            if (!control.Running)
            {
                await SetStateAsync("disconnecting");
            }

            Stop(config);
            await SetStateAsync("disconnected");
        }
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

    private async Task<bool> TryConnectAsync(string member, CancellationToken ct)
    {
        // Clear prior reason so this run's failure isn't stale.
        await store.SetSettingAsync(TunnelPaths.ConnectMessageKey(member), string.Empty, ct);

        logger.LogInformation("connecting {Member}: creating and starting tunnel service", member);
        var created = serviceManager.CreateService(member);
        var started = serviceManager.StartQuiet(member);
        if (created != 0 || started != 0)
        {
            logger.LogWarning("tunnel service {Member} did not start cleanly: sc create={Create} ({CreateMsg}), sc start={Start} ({StartMsg})",
                member, created, ScError(created), started, ScError(started));
        }

        var start = DateTimeOffset.UtcNow;
        var deadline = start.AddSeconds(_settings.ConnectTimeoutSeconds);
        var sawService = false;
        var lastHeartbeat = start;
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (ct.IsCancellationRequested)
            {
                return false;
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
                    return true;
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

        // Never responded: surface why the service failed to launch.
        if (!sawService)
        {
            var reason = await store.GetSettingAsync(TunnelPaths.ConnectMessageKey(member), ct);
            logger.LogWarning(
                "{Member}: tunnel service never responded over UAPI within {Sec}s - it likely failed to launch (sc start={Start}: {StartMsg}){Reason}",
                member, _settings.ConnectTimeoutSeconds, started, ScError(started),
                string.IsNullOrWhiteSpace(reason) ? string.Empty : $"; reason: {reason}");
        }

        Stop(member);
        return false;
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

    private void Stop(string member)
    {
        if (string.IsNullOrEmpty(member))
        {
            return;
        }

        serviceManager.StopQuiet(member);
        WaitStopped(member);
        serviceManager.DeleteService(member);
        reconciler.Reconcile();
    }

    private void WaitStopped(string member)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(10);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (serviceManager.QueryState(member) is "STOPPED" or "ABSENT")
            {
                return;
            }

            Thread.Sleep(300);
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
