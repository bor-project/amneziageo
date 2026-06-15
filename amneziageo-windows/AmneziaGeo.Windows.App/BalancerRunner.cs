using AmneziaGeo.Decl;
using Microsoft.Extensions.Logging;

namespace AmneziaGeo.Windows.App;

/// <summary>
/// Runs a balancer group: connects members (by priority order or lowest probe latency), fails over when
/// the active member dies, and switches to a consistently better member confirmed by out-of-band probes.
/// </summary>
internal sealed class BalancerRunner(
    ServiceManager serviceManager,
    UapiClient uapi,
    ConfigRepository configRepo,
    DnsRedirector dns,
    EndpointProbe probe,
    SettingsStore settingsStore,
    IStateStore store,
    AgentControl control,
    ILogger<BalancerRunner> logger)
{
    private static readonly TimeSpan _livenessPoll = TimeSpan.FromSeconds(5);

    private BalancerGroup _group = null!;
    private AppSettings _settings = new();
    private int[] _failbackStreak = [];

    /// <summary>
    /// Supervises the agent's target: re-reads the group and routing, idles while stopped, and
    /// (re)runs a failover session on each change so connect / disconnect and routing edits apply
    /// live without restarting the agent. Returns only on shutdown (the host cancellation token).
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
                    StopAll(group.Members);
                    await SetStateAsync("disconnected", group.Members, -1);
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
                    // A transient fault (service launch failure, store or probe error, ...) must not
                    // kill the supervisor or freeze the status; tear down, mark disconnected, and retry.
                    logger.LogError(ex, "balancer session failed; retrying");
                    StopAll(group.Members);
                    await SetStateAsync("disconnected", group.Members, -1);
                    await DelayAsync(_livenessPoll, linked.Token);
                }
            }
        }
    }

    /// <summary>
    /// Re-reads the target group from the store so member, mode, and recheck edits take effect.
    /// Falls back to the synthesized single-config group when no balancer row exists.
    /// </summary>
    private async Task<BalancerGroup?> ResolveAsync(BalancerGroup initial, CancellationToken ct)
    {
        var balancer = await store.GetBalancerAsync(initial.Name, ct);
        if (balancer is not null)
        {
            return balancer;
        }

        if (configRepo.Exists(initial.Name))
        {
            return initial;
        }

        logger.LogError("balancer {Group} no longer resolvable", initial.Name);
        return null;
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
    /// Runs one failover session: projects routing, connects the best member, then keeps the
    /// active member alive until the session token fires (a change signal or shutdown).
    /// </summary>
    private async Task RunSessionAsync(BalancerGroup group, CancellationToken ct)
    {
        _settings = await settingsStore.LoadAsync(ct);

        var members = group.Members;
        if (members.Count == 0)
        {
            logger.LogWarning("balancer {Group} has no members", group.Name);
            await SetStateAsync("disconnected", members, -1);
            await IdleAsync(ct);
            return;
        }

        foreach (var member in members)
        {
            if (!configRepo.Exists(member))
            {
                logger.LogError("missing config: {Member}", member);
                await SetStateAsync("disconnected", members, -1);
                await IdleAsync(ct);
                return;
            }
        }

        logger.LogInformation("balancer {Group} mode={Mode}", group.Name, group.Mode);
        await ProjectRoutingAsync(group.Name, members, ct);
        StopAll(members);
        _failbackStreak = new int[members.Count];

        await SetStateAsync("connecting", members, -1);
        var current = await ConnectBestAsync(members, -1, ct);
        await SetStateAsync(StatusFor(current), members, current);
        var lastRecheck = DateTimeOffset.UtcNow;

        try
        {
            while (!ct.IsCancellationRequested)
            {
                if (current < 0)
                {
                    logger.LogWarning("no member reachable; retrying");
                    await DelayAsync(_livenessPoll, ct);
                    current = await ConnectBestAsync(members, -1, ct);
                    Array.Clear(_failbackStreak);
                    await SetStateAsync(StatusFor(current), members, current);
                    lastRecheck = DateTimeOffset.UtcNow;
                    continue;
                }

                await DelayAsync(_livenessPoll, ct);
                if (ct.IsCancellationRequested)
                {
                    break;
                }

                if (!IsAlive(members[current]))
                {
                    logger.LogWarning("member {Member} unreachable; failing over", members[current]);
                    await SetStateAsync("failover", members, current);
                    Stop(members[current]);
                    current = await ConnectBestAsync(members, current, ct);
                    Array.Clear(_failbackStreak);
                    await SetStateAsync(StatusFor(current), members, current);
                    lastRecheck = DateTimeOffset.UtcNow;
                    continue;
                }

                if ((DateTimeOffset.UtcNow - lastRecheck).TotalSeconds >= group.RecheckSeconds)
                {
                    lastRecheck = DateTimeOffset.UtcNow;
                    var challenger = await FindChallengerAsync(members, current, ct);
                    if (challenger < 0)
                    {
                        Array.Clear(_failbackStreak);
                        continue;
                    }

                    BumpStreak(challenger);
                    if (_failbackStreak[challenger] >= _settings.FailbackProbes)
                    {
                        logger.LogInformation("switching to {Member} (better by {Mode})", members[challenger], group.Mode);
                        Stop(members[current]);
                        current = await ConnectBestAsync(members, -1, ct);
                        Array.Clear(_failbackStreak);
                        await SetStateAsync(StatusFor(current), members, current);
                    }
                }
            }
        }
        finally
        {
            if (current >= 0)
            {
                Stop(members[current]);
            }

            await SetStateAsync("disconnected", members, -1);
        }
    }

    private async Task ProjectRoutingAsync(string profile, IReadOnlyList<string> members, CancellationToken ct)
    {
        if (await store.GetBalancerAsync(profile, ct) is null)
        {
            return;
        }

        var (listId, useRouting) = await store.GetProfileRoutingAsync(profile, ct);
        if (!useRouting || listId is null)
        {
            await ClearMemberProjectionsAsync(members, ct);
            return;
        }

        var list = await store.GetRoutingListAsync(listId.Value, ct);
        if (list is null)
        {
            logger.LogWarning("profile {Profile} references missing routing list {Id}", profile, listId.Value);
            await ClearMemberProjectionsAsync(members, ct);
            return;
        }

        foreach (var member in members)
        {
            if (ct.IsCancellationRequested)
            {
                return;
            }

            await store.SaveTunnelGeoAsync(new TunnelGeo(member, true, list.Rules, list.Routes, list.Domains), ct);
        }

        logger.LogInformation("projected routing list '{List}' to {Count} member(s)", list.Name, members.Count);
    }

    private async Task ClearMemberProjectionsAsync(IReadOnlyList<string> members, CancellationToken ct)
    {
        foreach (var member in members)
        {
            if (ct.IsCancellationRequested)
            {
                return;
            }

            var existing = await store.GetTunnelGeoAsync(member, ct);
            if (existing is not null && existing.GeoSplit)
            {
                await store.SaveTunnelGeoAsync(existing with { GeoSplit = false }, ct);
            }
        }
    }

    private bool IsLatencyMode()
    {
        return _group.Mode.Equals("latency", StringComparison.OrdinalIgnoreCase);
    }

    private string StatusFor(int current)
    {
        if (current < 0)
        {
            return "disconnected";
        }

        if (IsLatencyMode())
        {
            return "connected";
        }

        return current == 0 ? "connected" : "degraded";
    }

    private void BumpStreak(int challenger)
    {
        for (var i = 0; i < _failbackStreak.Length; i++)
        {
            if (i != challenger)
            {
                _failbackStreak[i] = 0;
            }
        }

        _failbackStreak[challenger]++;
    }

    private async Task SetStateAsync(string status, IReadOnlyList<string> members, int current)
    {
        try
        {
            var member = current >= 0 ? members[current] : null;
            await store.SaveBalancerStateAsync(new BalancerState(_group.Name, status, member, DateTimeOffset.UtcNow));
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "state write failed");
        }
    }

    private async Task<int> FindChallengerAsync(IReadOnlyList<string> members, int current, CancellationToken ct)
    {
        var timeoutMs = _settings.ProbeTimeoutSeconds * 1000;
        if (IsLatencyMode())
        {
            long? bestRtt = null;
            long? currentRtt = null;
            var best = -1;
            for (var i = 0; i < members.Count; i++)
            {
                if (ct.IsCancellationRequested)
                {
                    return -1;
                }

                var rtt = await probe.PingAsync(members[i], timeoutMs);
                if (i == current)
                {
                    currentRtt = rtt;
                }

                if (rtt is not null && (bestRtt is null || rtt < bestRtt))
                {
                    bestRtt = rtt;
                    best = i;
                }
            }

            if (best < 0 || best == current)
            {
                return -1;
            }

            return currentRtt is null || bestRtt < currentRtt ? best : -1;
        }

        for (var i = 0; i < current; i++)
        {
            if (ct.IsCancellationRequested)
            {
                return -1;
            }

            if (await probe.IsReachableAsync(members[i], timeoutMs))
            {
                return i;
            }
        }

        return -1;
    }

    private async Task<int> ConnectBestAsync(IReadOnlyList<string> members, int skip, CancellationToken ct)
    {
        foreach (var i in await SelectionOrderAsync(members, ct))
        {
            if (i == skip || ct.IsCancellationRequested)
            {
                continue;
            }

            if (await TryConnectAsync(members[i], ct))
            {
                logger.LogInformation("connected: {Member} (priority {Priority})", members[i], i);
                return i;
            }
        }

        return -1;
    }

    private async Task<IReadOnlyList<int>> SelectionOrderAsync(IReadOnlyList<string> members, CancellationToken ct)
    {
        if (!IsLatencyMode())
        {
            return [.. Enumerable.Range(0, members.Count)];
        }

        var timeoutMs = _settings.ProbeTimeoutSeconds * 1000;
        var rtts = new long?[members.Count];
        for (var i = 0; i < members.Count; i++)
        {
            if (ct.IsCancellationRequested)
            {
                break;
            }

            rtts[i] = await probe.PingAsync(members[i], timeoutMs);
        }

        return [.. Enumerable.Range(0, members.Count).Where(i => rtts[i] is not null).OrderBy(i => rtts[i]!.Value)];
    }

    private async Task<bool> TryConnectAsync(string member, CancellationToken ct)
    {
        serviceManager.CreateService(member);
        serviceManager.StartQuiet(member);
        var deadline = DateTimeOffset.UtcNow.AddSeconds(_settings.ConnectTimeoutSeconds);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (ct.IsCancellationRequested)
            {
                return false;
            }

            if (uapi.TryGetLastHandshake(member) is > 0)
            {
                return true;
            }

            await DelayAsync(TimeSpan.FromSeconds(1), ct);
        }

        Stop(member);
        return false;
    }

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
        serviceManager.StopQuiet(member);
        WaitStopped(member);
        serviceManager.DeleteService(member);
        dns.RestoreSaved();
    }

    private void StopAll(IReadOnlyList<string> members)
    {
        foreach (var member in members)
        {
            Stop(member);
        }
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
