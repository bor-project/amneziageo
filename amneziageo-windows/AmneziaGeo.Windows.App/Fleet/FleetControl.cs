using AmneziaGeo.Ipc.Fleet;

namespace AmneziaGeo.Windows.App.Fleet;

/// <summary>
/// The tunnels a machine is asked to keep up at once, what each of them is for, and which one carries what no
/// rule sends elsewhere.
/// </summary>
internal sealed class FleetControl : TunnelDutyRoster
{
    private readonly Lock _gate = new();
    private readonly List<string> _order = [];
    private readonly List<string> _wanted = [];
    private readonly Dictionary<string, string> _roles = new(StringComparer.Ordinal);
    private CancellationTokenSource _change = new();
    private bool _moved;

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
    /// The servers in the order the mode lists them, which is the order it falls back through.
    /// </summary>
    public IReadOnlyList<string> Order
    {
        get
        {
            lock (_gate)
            {
                return _order.ToArray();
            }
        }
    }

    /// <summary>
    /// The server named to carry the machine, empty while none is.
    /// </summary>
    public string Primary
    {
        get
        {
            lock (_gate)
            {
                return PrimaryLocked();
            }
        }
    }

    /// <summary>
    /// Whether a request has moved the set since it was stood back up. A start that raised nothing must not
    /// write over what the mode last stood on.
    /// </summary>
    public bool Moved
    {
        get
        {
            lock (_gate)
            {
                return _moved;
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
            _moved = true;
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

            _moved = true;
        }

        Signal();
        return true;
    }

    /// <summary>
    /// Lists the servers in the order the mode keeps them.
    /// </summary>
    public void SetOrder(IReadOnlyList<string> names)
    {
        lock (_gate)
        {
            _order.Clear();
            Fill(_order, names);
            _moved = true;
        }

        Signal();
    }

    /// <summary>
    /// Stands the set on what the mode last stored.
    /// </summary>
    public void Restore(FleetState state)
    {
        lock (_gate)
        {
            _order.Clear();
            Fill(_order, state.Order);
            _roles.Clear();
            foreach (var pair in state.Roles)
            {
                _roles[pair.Key] = TunnelRoles.Of(pair.Value);
            }

            if (state.Primary.Length > 0)
            {
                PromoteLocked(state.Primary);
            }

            _wanted.Clear();
            Fill(_wanted, state.Desired);
            _moved = false;
        }

        Signal();
    }

    /// <summary>
    /// What the mode stands on, as it is stored.
    /// </summary>
    public FleetState Snapshot()
    {
        lock (_gate)
        {
            return new FleetState(
                _order.ToArray(),
                new Dictionary<string, string>(_roles, StringComparer.Ordinal),
                PrimaryLocked(),
                _wanted.ToArray());
        }
    }

    /// <summary>
    /// The set as the window sees it: every server of the library, in the order the mode lists them.
    /// </summary>
    public FleetSnapshot Describe(IReadOnlyList<string> library)
    {
        lock (_gate)
        {
            var servers = new List<FleetEntry>();
            foreach (var name in ListedLocked(library))
            {
                var duties = DutiesLocked(name);
                servers.Add(new FleetEntry(name, RoleLocked(name), _wanted.Contains(name), duties.CarriesDefault, duties.HoldsResolver));
            }

            return new FleetSnapshot(servers, PrimaryLocked(), CarrierLocked() ?? string.Empty);
        }
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
                PromoteLocked(name);
            }
            else
            {
                _roles[name] = given;
            }

            _moved = true;
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
            return DutiesLocked(name);
        }
    }

    private TunnelDuties DutiesLocked(string name)
    {
        return string.Equals(CarrierLocked(), name, StringComparison.Ordinal) ? TunnelDuties.Sole : TunnelDuties.None;
    }

    // The library in the order the mode lists it: what the order names, then what it does not.
    private IEnumerable<string> ListedLocked(IReadOnlyList<string> library)
    {
        foreach (var name in _order)
        {
            if (library.Contains(name))
            {
                yield return name;
            }
        }

        foreach (var name in library)
        {
            if (!_order.Contains(name))
            {
                yield return name;
            }
        }
    }

    // The primary while it is asked for, else the first reserve the mode lists. A neutral tunnel is out of the
    // balancer, so it carries nothing but what names it.
    private string? CarrierLocked()
    {
        foreach (var name in PriorityLocked())
        {
            if (RoleLocked(name) == TunnelRoles.Primary)
            {
                return name;
            }
        }

        foreach (var name in PriorityLocked())
        {
            if (TunnelRoles.Balanced(RoleLocked(name)))
            {
                return name;
            }
        }

        return null;
    }

    // The set in the order the mode lists its servers; one the order does not name keeps the place it was
    // asked in, behind those it does.
    private IEnumerable<string> PriorityLocked()
    {
        foreach (var name in _order)
        {
            if (_wanted.Contains(name))
            {
                yield return name;
            }
        }

        foreach (var name in _wanted)
        {
            if (!_order.Contains(name))
            {
                yield return name;
            }
        }
    }

    private string PrimaryLocked()
    {
        foreach (var pair in _roles)
        {
            if (pair.Value == TunnelRoles.Primary)
            {
                return pair.Key;
            }
        }

        return string.Empty;
    }

    // A machine holds one primary, so naming one puts the one before it back in the balancer.
    private void PromoteLocked(string name)
    {
        foreach (var other in _roles.Where(r => r.Value == TunnelRoles.Primary).Select(r => r.Key).ToArray())
        {
            _roles[other] = TunnelRoles.Reserve;
        }

        _roles[name] = TunnelRoles.Primary;
    }

    private string RoleLocked(string name)
    {
        return _roles.TryGetValue(name, out var role) ? role : TunnelRoles.Default;
    }

    private static void Fill(List<string> list, IEnumerable<string> names)
    {
        foreach (var name in names)
        {
            if (!string.IsNullOrEmpty(name) && !list.Contains(name))
            {
                list.Add(name);
            }
        }
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
