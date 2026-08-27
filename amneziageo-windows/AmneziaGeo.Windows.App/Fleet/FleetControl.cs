using AmneziaGeo.Ipc.Fleet;

namespace AmneziaGeo.Windows.App.Fleet;

/// <summary>
/// The tunnels a machine is asked to keep up at once, what each of them is for, and which one carries what no
/// rule sends elsewhere.
/// </summary>
internal sealed class FleetControl : TunnelDutyRoster
{
    private readonly Lock _gate = new();
    private readonly List<string> _wanted = [];
    private readonly Dictionary<string, string> _roles = new(StringComparer.Ordinal);
    private CancellationTokenSource _change = new();

    /// <summary>
    /// Fires when the set or a role moves.
    /// </summary>
    public CancellationToken ChangeToken
    {
        get
        {
            lock (_gate)
            {
                return _change.Token;
            }
        }
    }

    /// <summary>
    /// The tunnels asked for, in the order they were asked for.
    /// </summary>
    public IReadOnlyList<string> Wanted
    {
        get
        {
            lock (_gate)
            {
                return _wanted.ToArray();
            }
        }
    }

    /// <summary>
    /// The tunnel carrying what no rule sends elsewhere, or null while nothing in the set may.
    /// </summary>
    public string? Carrier
    {
        get
        {
            lock (_gate)
            {
                return CarrierLocked();
            }
        }
    }

    /// <summary>
    /// Asks for a tunnel; answers whether the set moved.
    /// </summary>
    public bool Add(string name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return false;
        }

        lock (_gate)
        {
            if (_wanted.Contains(name))
            {
                return false;
            }

            _wanted.Add(name);
        }

        Signal();
        return true;
    }

    /// <summary>
    /// Drops a tunnel from the set; answers whether it was in it.
    /// </summary>
    public bool Remove(string name)
    {
        lock (_gate)
        {
            if (!_wanted.Remove(name))
            {
                return false;
            }
        }

        Signal();
        return true;
    }

    /// <summary>
    /// The role a tunnel holds.
    /// </summary>
    public string RoleOf(string name)
    {
        lock (_gate)
        {
            return RoleLocked(name);
        }
    }

    /// <summary>
    /// Gives a tunnel its role. A machine holds one primary, so naming a second one demotes the first.
    /// </summary>
    public void SetRole(string name, string role)
    {
        var given = TunnelRoles.Of(role);
        lock (_gate)
        {
            if (given == TunnelRoles.Primary)
            {
                foreach (var other in _roles.Where(r => r.Value == TunnelRoles.Primary).Select(r => r.Key).ToArray())
                {
                    _roles[other] = TunnelRoles.Reserve;
                }
            }

            _roles[name] = given;
        }

        Signal();
    }

    /// <inheritdoc/>
    public override IReadOnlyCollection<string> Standing(string name)
    {
        lock (_gate)
        {
            return _wanted.Contains(name) ? _wanted.ToArray() : [.. _wanted, name];
        }
    }

    /// <inheritdoc/>
    public override TunnelDuties For(string name)
    {
        lock (_gate)
        {
            return string.Equals(CarrierLocked(), name, StringComparison.Ordinal) ? TunnelDuties.Sole : TunnelDuties.None;
        }
    }

    // The primary while it is asked for, else the first reserve in the order the set was asked for. A neutral
    // tunnel is out of the balancer, so it carries nothing but what names it.
    private string? CarrierLocked()
    {
        foreach (var name in _wanted)
        {
            if (RoleLocked(name) == TunnelRoles.Primary)
            {
                return name;
            }
        }

        foreach (var name in _wanted)
        {
            if (TunnelRoles.Balanced(RoleLocked(name)))
            {
                return name;
            }
        }

        return null;
    }

    private string RoleLocked(string name)
    {
        return _roles.TryGetValue(name, out var role) ? role : TunnelRoles.Default;
    }

    private void Signal()
    {
        CancellationTokenSource old;
        lock (_gate)
        {
            old = _change;
            _change = new CancellationTokenSource();
        }

        old.Cancel();
        old.Dispose();
    }
}
