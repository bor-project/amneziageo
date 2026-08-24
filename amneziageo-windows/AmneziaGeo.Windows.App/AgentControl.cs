using System.Collections.Concurrent;
using AmneziaGeo.Decl;
using AmneziaGeo.Ipc;

namespace AmneziaGeo.Windows.App;

/// <summary>
/// Control surface shared between the IPC broker and the tunnel supervisors. Holds one entry per tunnel the
/// agent has been asked to raise.
/// </summary>
internal sealed class AgentControl
{
    /// <summary>
    /// Store key for the selected config.
    /// </summary>
    public const string SelectedTargetKey = StateKeys.SelectedTarget;

    private readonly Lock _gate = new();
    private readonly ConcurrentDictionary<string, TunnelControl> _tunnels = new(StringComparer.Ordinal);
    private volatile string? _target;
    private volatile string? _defaultOwner;
    private volatile string? _resolverOwner;
    private CancellationTokenSource _membership = new();
    private CancellationTokenSource _status = new();

    /// <summary>
    /// Every tunnel the agent knows of, desired or torn down.
    /// </summary>
    public IReadOnlyList<TunnelControl> Tunnels => [.. _tunnels.Values.OrderBy(tunnel => tunnel.Sequence)];

    /// <summary>
    /// The tunnels the agent keeps up.
    /// </summary>
    public IReadOnlyList<TunnelControl> Desired => [.. _tunnels.Values.Where(tunnel => tunnel.Running).OrderBy(tunnel => tunnel.Sequence)];

    /// <summary>
    /// Whether the agent keeps any tunnel up.
    /// </summary>
    public bool Running => _tunnels.Values.Any(tunnel => tunnel.Running);

    /// <summary>
    /// The tunnel a machine-wide reading is taken from: the first one still desired.
    /// </summary>
    public TunnelControl? Primary => Desired.FirstOrDefault();

    /// <summary>
    /// The config the running tunnel is bound to; the first one when several are up.
    /// </summary>
    public string? RunningTarget => Primary?.Config;

    /// <summary>
    /// The user-selected config (radio).
    /// </summary>
    public string? Target => _target;

    /// <summary>
    /// Any tunnel must be reconnected to apply a changed setting.
    /// </summary>
    public bool RestartRequired => _tunnels.Values.Any(tunnel => tunnel.Running && tunnel.RestartRequired);

    /// <summary>
    /// How long ago the peer of the first running tunnel last answered; -1 when none is up.
    /// </summary>
    public int HandshakeAge => Primary?.HandshakeAge ?? -1;

    /// <summary>
    /// Throughput and handshake rate of the first running tunnel.
    /// </summary>
    public LinkReading Link => Primary?.Link ?? LinkReading.Empty;

    /// <summary>
    /// Whether the resolver this machine sends its lookups to stopped answering while a tunnel is up.
    /// </summary>
    public bool DnsUnreachable => _tunnels.Values.Any(tunnel => tunnel.Running && tunnel.DnsUnreachable);

    /// <summary>
    /// Fires when the set of desired tunnels changes.
    /// </summary>
    public CancellationToken MembershipToken
    {
        get
        {
            lock (_gate)
            {
                return _membership.Token;
            }
        }
    }

    /// <summary>
    /// Fires when a status change should be pushed to UI clients, without waking a supervisor.
    /// </summary>
    public CancellationToken StatusToken
    {
        get
        {
            lock (_gate)
            {
                return _status.Token;
            }
        }
    }

    /// <summary>
    /// The tunnel that carries the default route right now; null while none does.
    /// </summary>
    public string? DefaultRouteOwner => _defaultOwner;

    /// <summary>
    /// Hands the default route to a config. The config the user picked takes it from whoever holds it; without
    /// a pick the first claim wins and the rest carry only the ranges they name.
    /// </summary>
    public TunnelClaim ClaimDefaultRoute(string config, bool preferred)
    {
        lock (_gate)
        {
            var held = _defaultOwner;
            if (held is null || string.Equals(held, config, StringComparison.Ordinal))
            {
                _defaultOwner = config;
                return new TunnelClaim(true, null);
            }

            if (!preferred)
            {
                return new TunnelClaim(false, null);
            }

            _defaultOwner = config;
            return new TunnelClaim(true, Find(held));
        }
    }

    /// <summary>
    /// Gives the default route back when the tunnel holding it goes down.
    /// </summary>
    public void ReleaseDefaultRoute(string config)
    {
        lock (_gate)
        {
            if (string.Equals(_defaultOwner, config, StringComparison.Ordinal))
            {
                _defaultOwner = null;
            }
        }
    }

    /// <summary>
    /// The tunnel every lookup on this machine goes to; null while the resolvers are the machine's own.
    /// </summary>
    public string? ResolverOwner => _resolverOwner;

    /// <summary>
    /// Hands the machine's name lookups to a tunnel. There is one resolver per machine, so the first claim
    /// wins and the tunnel carrying the default route takes it - it is the one every lookup would follow.
    /// </summary>
    public TunnelClaim ClaimResolver(string config)
    {
        lock (_gate)
        {
            var held = _resolverOwner;
            if (held is null || string.Equals(held, config, StringComparison.Ordinal))
            {
                _resolverOwner = config;
                return new TunnelClaim(true, null);
            }

            if (!string.Equals(_defaultOwner, config, StringComparison.Ordinal))
            {
                return new TunnelClaim(false, null);
            }

            _resolverOwner = config;
            return new TunnelClaim(true, Find(held));
        }
    }

    /// <summary>
    /// Gives the machine's name lookups back when the tunnel holding them goes down.
    /// </summary>
    public void ReleaseResolver(string config)
    {
        lock (_gate)
        {
            if (string.Equals(_resolverOwner, config, StringComparison.Ordinal))
            {
                _resolverOwner = null;
            }
        }
    }

    /// <summary>
    /// Returns the control of a config, creating it on first use.
    /// </summary>
    public TunnelControl For(string config, string? ownerRoot = null, string? ownerSid = null)
    {
        var tunnel = _tunnels.GetOrAdd(
            config,
            name => new TunnelControl(name, ownerRoot ?? AppDataRoot.Base(), ownerSid, SignalStatus, SignalMembership));
        if (ownerRoot is not null)
        {
            tunnel.SetOwner(ownerRoot, ownerSid);
        }

        return tunnel;
    }

    /// <summary>
    /// Returns the control of a config when the agent already knows it.
    /// </summary>
    public TunnelControl? Find(string? config)
    {
        return config is { Length: > 0 } && _tunnels.TryGetValue(config, out var tunnel) ? tunnel : null;
    }

    /// <summary>
    /// Returns whether the named config is desired.
    /// </summary>
    public bool IsRunning(string? config)
    {
        return Find(config) is { Running: true };
    }

    /// <summary>
    /// Drops a torn-down tunnel from the set. One holding a latched failure stays: the entry is what carries
    /// the reason to the screen until the next connect clears it.
    /// </summary>
    public void Forget(string config)
    {
        if (_tunnels.TryGetValue(config, out var tunnel)
            && !tunnel.Running
            && !tunnel.ConnectFailed
            && !tunnel.DisconnectFailed)
        {
            _tunnels.TryRemove(config, out _);
        }
    }

    /// <summary>
    /// Selects the active target config without changing running state.
    /// </summary>
    public void SetTarget(string name)
    {
        // No signal: selecting does not switch a live tunnel.
        _target = name;
    }

    /// <summary>
    /// Clears the selected target.
    /// </summary>
    public void ClearTarget()
    {
        _target = null;
    }

    /// <summary>
    /// Follows a config rename in the selection and in the live binding.
    /// </summary>
    public void RetargetName(string oldName, string newName)
    {
        if (string.Equals(_target, oldName, StringComparison.Ordinal))
        {
            _target = newName;
        }

        if (_tunnels.TryRemove(oldName, out var tunnel))
        {
            tunnel.Rename(newName);
            _tunnels[newName] = tunnel;
        }
    }

    /// <summary>
    /// Signals every tunnel that persisted configuration changed and must be re-applied.
    /// </summary>
    public void Invalidate()
    {
        foreach (var tunnel in _tunnels.Values)
        {
            tunnel.Invalidate();
        }

        SignalMembership();
        SignalStatus();
    }

    /// <summary>
    /// Ends the backoff wait of every dialling tunnel early on a network change.
    /// </summary>
    public void WakeIfRetrying()
    {
        foreach (var tunnel in _tunnels.Values)
        {
            tunnel.WakeIfRetrying();
        }
    }

    /// <summary>
    /// Completes on the next status change; throws only on shutdown.
    /// </summary>
    public async Task WaitForStatusAsync(CancellationToken ct)
    {
        var token = StatusToken;
        using (var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, token))
        {
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, linked.Token);
            }
            catch (OperationCanceledException)
            {
            }
        }

        ct.ThrowIfCancellationRequested();
    }

    /// <summary>
    /// Completes once any tunnel is desired, idling on the membership signal until then.
    /// </summary>
    public async Task WaitUntilRunningAsync(CancellationToken ct)
    {
        while (true)
        {
            var token = MembershipToken;
            if (Running)
            {
                return;
            }

            ct.ThrowIfCancellationRequested();
            using (var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, token))
            {
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, linked.Token);
                }
                catch (OperationCanceledException)
                {
                }
            }
        }
    }

    /// <summary>
    /// Wakes the supervisor after the set of desired tunnels changed.
    /// </summary>
    public void SignalMembership()
    {
        CancellationTokenSource old;
        lock (_gate)
        {
            old = _membership;
            _membership = new CancellationTokenSource();
        }

        old.Cancel();
        old.Dispose();
    }

    /// <summary>
    /// Wakes status waiters after a state change, without re-entering a supervisor.
    /// </summary>
    public void SignalStatus()
    {
        CancellationTokenSource old;
        lock (_gate)
        {
            old = _status;
            _status = new CancellationTokenSource();
        }

        old.Cancel();
        old.Dispose();
    }
}

/// <summary>
/// Outcome of a claim on something only one tunnel can hold: whether it was granted, and the tunnel that had
/// to give it up.
/// </summary>
internal sealed record TunnelClaim(bool Granted, TunnelControl? Displaced);
