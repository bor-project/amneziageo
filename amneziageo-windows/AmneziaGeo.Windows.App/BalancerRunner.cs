using AmneziaGeo.Decl;
using Microsoft.Extensions.Logging;

namespace AmneziaGeo.Windows.App;

/// <summary>
/// Runs a balancer group: connects members in priority order, fails over when one becomes unreachable,
/// and fails back to a higher-priority member once an out-of-band probe confirms it is reachable.
/// </summary>
internal sealed class BalancerRunner(
    ServiceManager serviceManager,
    UapiClient uapi,
    ConfigRepository configRepo,
    DnsRedirector dns,
    EndpointProbe probe,
    SettingsStore settingsStore,
    IStateStore store,
    ILogger<BalancerRunner> logger)
{
    private static readonly TimeSpan _livenessPoll = TimeSpan.FromSeconds(5);

    private BalancerGroup _group = null!;
    private AppSettings _settings = new();
    private int[] _failbackStreak = [];

    /// <summary>
    /// Drives the failover loop for a group until cancellation.
    /// </summary>
    public async Task RunAsync(BalancerGroup group, CancellationToken ct)
    {
        _group = group;
        _settings = await settingsStore.LoadAsync(ct);

        var members = group.Members;
        if (members.Count == 0)
        {
            logger.LogWarning("balancer {Group} has no members", group.Name);
            return;
        }

        foreach (var member in members)
        {
            if (!configRepo.Exists(member))
            {
                logger.LogError("missing config: {Member}", member);
                return;
            }
        }

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

                if (current > 0 && (DateTimeOffset.UtcNow - lastRecheck).TotalSeconds >= group.RecheckSeconds)
                {
                    lastRecheck = DateTimeOffset.UtcNow;
                    if (await ShouldFailBackAsync(members, current, ct))
                    {
                        logger.LogInformation("higher-priority member reachable; failing back from {Member}", members[current]);
                        Stop(members[current]);
                        var next = await ConnectBestAsync(members, -1, ct);
                        if (next >= 0 && next != current)
                        {
                            logger.LogInformation("switched to {Member}", members[next]);
                        }

                        current = next;
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

    private static string StatusFor(int current)
    {
        if (current < 0)
        {
            return "disconnected";
        }

        return current == 0 ? "connected" : "degraded";
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

    private async Task<bool> ShouldFailBackAsync(IReadOnlyList<string> members, int current, CancellationToken ct)
    {
        var trigger = false;
        var timeoutMs = _settings.ProbeTimeoutSeconds * 1000;
        for (var i = 0; i < current; i++)
        {
            if (ct.IsCancellationRequested)
            {
                return false;
            }

            if (await probe.IsReachableAsync(members[i], timeoutMs))
            {
                _failbackStreak[i]++;
                if (_failbackStreak[i] >= _settings.FailbackProbes)
                {
                    logger.LogInformation("member {Member} reachable for {Count} probe(s)", members[i], _failbackStreak[i]);
                    trigger = true;
                }
            }
            else
            {
                _failbackStreak[i] = 0;
            }
        }

        return trigger;
    }

    private async Task<int> ConnectBestAsync(IReadOnlyList<string> members, int skip, CancellationToken ct)
    {
        for (var i = 0; i < members.Count; i++)
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
