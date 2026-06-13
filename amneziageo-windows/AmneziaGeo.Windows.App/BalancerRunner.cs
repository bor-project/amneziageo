using AmneziaGeo.Decl;

namespace AmneziaGeo.Windows.App;

/// <summary>
/// Runs a balancer group: connects members in priority order, fails over when one becomes unreachable,
/// and periodically rechecks higher-priority members to fail back to them.
/// </summary>
internal sealed class BalancerRunner(BalancerGroup group, int connectTimeoutSeconds, int deadThresholdSeconds)
{
    private static readonly TimeSpan _livenessPoll = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Drives the failover loop until cancellation.
    /// </summary>
    public async Task RunAsync(CancellationToken ct)
    {
        var members = group.Members;
        if (members.Count == 0)
        {
            Console.WriteLine("balancer has no members");
            return;
        }

        foreach (var member in members)
        {
            if (!ConfigRepository.Exists(member))
            {
                Console.WriteLine($"missing config: {member}");
                return;
            }

            ServiceManager.CreateService(member);
        }

        StopAll(members);

        var current = await ConnectBestAsync(members, -1, ct);
        var lastRecheck = DateTimeOffset.UtcNow;

        try
        {
            while (!ct.IsCancellationRequested)
            {
                if (current < 0)
                {
                    Console.WriteLine("no member reachable; retrying");
                    await DelayAsync(_livenessPoll, ct);
                    current = await ConnectBestAsync(members, -1, ct);
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
                    Console.WriteLine($"member {members[current]} unreachable; failing over");
                    Stop(members[current]);
                    current = await ConnectBestAsync(members, current, ct);
                    lastRecheck = DateTimeOffset.UtcNow;
                    continue;
                }

                if (current > 0 && (DateTimeOffset.UtcNow - lastRecheck).TotalSeconds >= group.RecheckSeconds)
                {
                    lastRecheck = DateTimeOffset.UtcNow;
                    Console.WriteLine($"rechecking higher-priority members (active: {members[current]})");
                    Stop(members[current]);
                    var next = await ConnectBestAsync(members, -1, ct);
                    if (next >= 0 && next != current)
                    {
                        Console.WriteLine($"switched to {members[next]}");
                    }

                    current = next;
                }
            }
        }
        finally
        {
            if (current >= 0)
            {
                Stop(members[current]);
            }
        }
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
                Console.WriteLine($"connected: {members[i]} (priority {i})");
                return i;
            }
        }

        return -1;
    }

    private async Task<bool> TryConnectAsync(string member, CancellationToken ct)
    {
        ServiceManager.StartQuiet(member);
        var deadline = DateTimeOffset.UtcNow.AddSeconds(connectTimeoutSeconds);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (ct.IsCancellationRequested)
            {
                return false;
            }

            if (UapiClient.TryGetLastHandshake(member) is > 0)
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
        var handshake = UapiClient.TryGetLastHandshake(member);
        if (handshake is null or 0)
        {
            return false;
        }

        var age = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - handshake.Value;
        return age < deadThresholdSeconds;
    }

    private void Stop(string member)
    {
        ServiceManager.StopQuiet(member);
        WaitStopped(member);
    }

    private void StopAll(IReadOnlyList<string> members)
    {
        foreach (var member in members)
        {
            Stop(member);
        }
    }

    private static void WaitStopped(string member)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(10);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (ServiceManager.QueryState(member) is "STOPPED" or "ABSENT")
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
