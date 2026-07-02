using AmneziaGeo.Decl;
using Microsoft.Extensions.Logging;

namespace AmneziaGeo.Windows.App;

/// <summary>
/// Runs a profile's single configuration: brings the tunnel up, keeps it alive, and re-dials when the
/// handshake dies. Re-runs on each change so connect / disconnect and routing edits apply live without
/// restarting the agent. Returns only on shutdown (the host cancellation token).
/// </summary>
internal sealed class BalancerRunner(
    ServiceManager serviceManager,
    UapiClient uapi,
    ConfigRepository configRepo,
    NetworkReconciler reconciler,
    SettingsStore settingsStore,
    IStateStore store,
    AgentControl control,
    ILogger<BalancerRunner> logger)
{
    private static readonly TimeSpan _livenessPoll = TimeSpan.FromSeconds(5);

    // How long a connect attempt tolerates no handshake AND zero bytes received before treating the config
    // as unreachable - the data-driven failure signal (the server never answered our handshake
    // initiations). ConnectTimeoutSeconds stays the absolute backstop.
    private static readonly TimeSpan _noResponseWindow = TimeSpan.FromSeconds(12);

    private BalancerGroup _group = null!;
    private AppSettings _settings = new();

    /// <summary>
    /// Supervises the agent's target: re-reads the profile and routing, idles while stopped, and
    /// (re)runs a session on each change so connect / disconnect and routing edits apply live without
    /// restarting the agent. Returns only on shutdown (the host cancellation token).
    /// </summary>
    public async Task RunAsync(BalancerGroup initial, CancellationToken ct)
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
                    await SetStateAsync("disconnected", null);
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
                    // A transient fault (service launch failure, store error, ...) must not kill the
                    // supervisor or freeze the status; tear down, mark disconnected, and retry.
                    logger.LogError(ex, "tunnel session failed: {Reason}; retrying", ex.Message);
                    Stop(group.Config);
                    await SetStateAsync("disconnected", null);
                    await DelayAsync(_livenessPoll, linked.Token);
                }
            }
        }
    }

    /// <summary>
    /// Re-reads the target profile from the store so a config or routing edit takes effect. Falls back
    /// to a profile that owns the named config when no profile row exists (so a bare config name is
    /// connectable), and clears a dangling binding when the target names neither.
    /// </summary>
    private async Task<BalancerGroup?> ResolveAsync(BalancerGroup initial, CancellationToken ct)
    {
        // While running, stay on the latched running target so live edits re-apply but a newly-selected
        // profile does NOT switch the tunnel (the user reconnects to apply). While stopped, track the
        // selected target so the idle view reflects the user's choice. Fall back to the launch target.
        var name = (control.Running ? control.RunningTarget : control.Target) ?? control.Target ?? initial.Name;
        var balancer = await store.GetBalancerAsync(name, ct);
        if (balancer is not null)
        {
            return balancer;
        }

        if (await configRepo.ExistsAsync(name, ct))
        {
            return new BalancerGroup(name, name);
        }

        // The target names neither a profile nor a config. Either nothing is selected yet (clean install)
        // or the bound profile/config was deleted - a broken binding. Drop any dangling selection so the
        // UI stops showing a phantom target, and keep the supervisor alive on a nameless empty profile so
        // it idles (the pipe/status stay up) until a profile is created and selected.
        if (!string.IsNullOrEmpty(name))
        {
            await store.SetSettingAsync(AgentControl.SelectedTargetKey, string.Empty, ct);
            control.ClearTarget();
            logger.LogInformation("target '{Group}' does not exist; cleared binding, idling", name);
        }

        return new BalancerGroup(string.Empty, string.Empty);
    }

    /// <summary>
    /// Blocks until the desired state or configuration changes (or the agent shuts down).
    /// </summary>
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

    /// <summary>
    /// Runs one session: projects routing, connects the profile's single config, then keeps it alive
    /// (re-dialing on a dead handshake) until the session token fires (a change signal or shutdown).
    /// </summary>
    private async Task RunSessionAsync(BalancerGroup group, CancellationToken ct)
    {
        _settings = await settingsStore.LoadAsync(ct);
        var config = group.Config;

        if (string.IsNullOrEmpty(config))
        {
            // A named-but-config-less profile is worth a warning; the nameless empty profile (no target
            // selected) is the normal idle state and must stay quiet.
            if (!string.IsNullOrEmpty(group.Name))
            {
                logger.LogWarning("profile {Profile} has no configuration", group.Name);
            }

            await SetStateAsync("disconnected", null);

            // A user connect to a config-less profile can never succeed. Drop the desired state to stopped
            // and raise the one-shot failed notice instead of sitting on Running=true (perpetual
            // "connecting…"). FailConnect signals, so the supervisor re-enters the idle branch next loop.
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
            await SetStateAsync("disconnected", null);

            // Same as the config-less case: a config whose .conf is gone can never dial, so fail the
            // connect rather than leaving Running=true (perpetual "connecting…").
            if (control.Running)
            {
                control.FailConnect();
            }

            await IdleAsync(ct);
            return;
        }

        await ProjectRoutingAsync(group.Name, config, ct);
        Stop(config);

        await SetStateAsync("connecting", null);
        if (!await TryConnectAsync(config, ct))
        {
            // A user-initiated connect could not bring the config up within the data-driven deadline.
            // Give up (отбой) and raise a one-shot "failed" notice for the UI rather than retrying
            // forever; FailConnect drops the desired state to stopped, so the supervisor then idles.
            if (!ct.IsCancellationRequested)
            {
                // Surface the per-tunnel service's own failure reason (wstunnel missing, config parse, etc.)
                // that it forwarded to the shared store; fall back to the generic line when there is none
                // (a plain reachability timeout leaves no message).
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
                await SetStateAsync("disconnected", null);
                control.FailConnect();
            }

            return;
        }

        logger.LogInformation("connected: {Config} ({Profile})", config, group.Name);
        await SetStateAsync("connected", config);

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
                    logger.LogWarning("config {Config} unreachable; re-dialing", config);
                    await SetStateAsync("connecting", null);
                    Stop(config);
                    if (!await TryConnectAsync(config, ct))
                    {
                        if (!ct.IsCancellationRequested)
                        {
                            logger.LogWarning("re-dial failed: {Config} unreachable", config);
                            Stop(config);
                            await SetStateAsync("disconnected", null);
                            control.FailConnect();
                        }

                        return;
                    }

                    await SetStateAsync("connected", config);
                }
            }
        }
        finally
        {
            // Surface a transient "disconnecting" only on a genuine user disconnect (running off),
            // not on a config-change re-run, where the session immediately reconnects.
            if (!control.Running)
            {
                await SetStateAsync("disconnecting", config);
            }

            Stop(config);
            await SetStateAsync("disconnected", null);
        }
    }

    private async Task ProjectRoutingAsync(string profile, string config, CancellationToken ct)
    {
        if (await store.GetBalancerAsync(profile, ct) is null)
        {
            // No profile drives this connect (a bare-config target: legacy set-profile <config> / a seed
            // --agent target). Clear any projection a previous connect left on this config so it reverts to
            // its own set-geo and a stale proj_routing_list_id does not leak a dead routing list's DNS /
            // exclusions / all-UDP into this tunnel (#89).
            await store.ClearTunnelProjectionAsync(config, ct);
            return;
        }

        var (listId, useRouting) = await store.GetProfileRoutingAsync(profile, ct);
        if (!useRouting || listId is null)
        {
            // Routing off (or no list selected): full tunnel via the config's own AllowedIPs
            // (0.0.0.0/0, ::/0). Project an explicit non-split state so the toggle is authoritative
            // and overrides any config set-geo split. This is a kill-switch full tunnel by design.
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
        // Projects an explicit non-split state: GetActiveTunnelGeoAsync then returns geoSplit=false, so
        // AllowedIpsResolver falls back to the config's own AllowedIPs (0.0.0.0/0, ::/0) = full tunnel.
        // Making the projection authoritative means the routing toggle overrides any config set-geo,
        // so turning routing off reliably switches to full tunnel instead of a leftover split.
        await store.SaveTunnelProjectionAsync(config, false, [], [], [], null, ct);
        logger.LogInformation("projected full tunnel to {Config} (routing off)", config);
    }

    private async Task SetStateAsync(string status, string? activeConfig)
    {
        try
        {
            await store.SaveBalancerStateAsync(new BalancerState(_group.Name, status, activeConfig, DateTimeOffset.UtcNow));
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "state write failed");
        }
    }

    private async Task<bool> TryConnectAsync(string member, CancellationToken ct)
    {
        // Clear any forwarded reason from a previous attempt so a failure now reflects THIS run.
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
                // Full per-poll resolution of the handshake progression (the Info heartbeat below is every 4s):
                // at Debug/Trace a support engineer sees exactly when rx starts moving, or that it never does.
                logger.LogDebug("{Member}: poll - handshake={Hs}s tx={Tx}B rx={Rx}B elapsed={Sec}s",
                    member, status.HandshakeSec, status.TxBytes, status.RxBytes, elapsed);
                if (status.HandshakeSec > 0)
                {
                    logger.LogInformation("{Member}: handshake received in {Sec}s", member, elapsed);
                    return true;
                }

                // The pipe answered: the per-tunnel service started and its engine is up - distinguishes a
                // service that never launched from one that launched but the server stays silent.
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

                // Data-driven failure: the engine has had time to send handshake initiations (TxBytes grows),
                // but the server has returned nothing (no handshake, zero rx). That is the structured form of
                // the engine's "handshake did not complete" - give up now rather than waiting the backstop.
                if (status is { HandshakeSec: 0, RxBytes: 0 } && DateTimeOffset.UtcNow - start >= _noResponseWindow)
                {
                    logger.LogWarning("{Member}: server did not answer - no handshake, 0 bytes received in {Sec}s (sent {Tx} B); unreachable",
                        member, (int)_noResponseWindow.TotalSeconds, status.TxBytes);
                    break;
                }
            }
            else
            {
                // The per-tunnel service has not answered UAPI yet (still starting its engine). Visible only
                // at Trace so "waiting for the service to come up" is part of the every-action trace.
                logger.LogTrace("{Member}: tunnel service not responding over UAPI yet ({Sec}s)",
                    member, (int)(DateTimeOffset.UtcNow - start).TotalSeconds);
            }

            await DelayAsync(TimeSpan.FromSeconds(1), ct);
        }

        // Timed out. Make the reason explicit: a service that never answered over UAPI almost certainly never
        // ran its engine (a bad start, e.g. sc start=1053), as opposed to one that ran but the server stayed
        // silent (already logged above as "server did not answer"). Surface any reason the per-tunnel service
        // forwarded before it died, since its own log is not shown in the agent journal.
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

    // Human-readable meaning for the sc.exe / Win32 service error codes seen during connect, so the journal
    // explains a failed start instead of just printing a number.
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
        var handshake = uapi.TryGetLastHandshake(member);
        if (handshake is null or 0)
        {
            return false;
        }

        var age = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - handshake.Value;
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
