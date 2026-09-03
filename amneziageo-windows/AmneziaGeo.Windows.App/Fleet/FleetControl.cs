using AmneziaGeo.Decl;
using AmneziaGeo.Geo;
using AmneziaGeo.Ipc.Fleet;

namespace AmneziaGeo.Windows.App.Fleet;

/// <summary>
/// The tunnels a machine is asked to keep up at once, what each of them is for, and which one carries what no
/// rule sends elsewhere.
/// </summary>
internal sealed class FleetControl(FleetLive live) : TunnelDutyRoster
{
    private readonly Lock _gate = new();
    private readonly List<string> _order = [];
    private readonly List<string> _wanted = [];
    private readonly Dictionary<string, string> _roles = new(StringComparer.Ordinal);
    private readonly Dictionary<string, RuleRoute> _targets = new(StringComparer.Ordinal);
    private readonly List<(string From, string To)> _renamed = [];
    private CancellationTokenSource _change = new();
    private bool _moved;
    private long _stamp;
    private string _best = string.Empty;
    private int _quiet;
    private BalancePolicy _policy = BalancePolicy.Default;

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
    /// The servers in the chain, in the order it falls back through, with the ones out of it last.
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
    /// Counts the moves of the rule addresses. A tunnel raised before one carries the share it no longer has.
    /// </summary>
    public long Stamp
    {
        get
        {
            lock (_gate)
            {
                return _stamp;
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
    /// The server the balancer holds, empty while it holds none.
    /// </summary>
    public string Best
    {
        get
        {
            lock (_gate)
            {
                return _best;
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
            TouchLocked();
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

            TouchLocked();
        }

        Signal();
        return true;
    }

    /// <summary>
    /// Strikes a server the library no longer holds: it leaves the set, the order, the roles and both ends of
    /// every rule that named it. Answers whether the mode held it anywhere.
    /// </summary>
    public bool Forget(string name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return false;
        }

        lock (_gate)
        {
            var struck = _wanted.Remove(name);
            struck |= _order.Remove(name);
            struck |= _roles.Remove(name);
            struck |= ForgetAddressesLocked(name);
            if (!struck)
            {
                return false;
            }

            if (string.Equals(_best, name, StringComparison.Ordinal))
            {
                _best = string.Empty;
                _quiet = 0;
            }

            // The rest close the gap, so a struck leader leaves the machine to the one behind it.
            NormalizeLocked();
            TouchLocked();
        }

        Signal();
        return true;
    }

    /// <summary>
    /// Follows a rename through the set: the server keeps its place in the order, its role, its share of the
    /// rules and the balancer's pick under the new name. Answers whether the mode held it anywhere.
    /// </summary>
    public bool Rename(string oldName, string newName)
    {
        if (string.IsNullOrEmpty(oldName) || string.IsNullOrEmpty(newName) || string.Equals(oldName, newName, StringComparison.Ordinal))
        {
            return false;
        }

        lock (_gate)
        {
            var held = Swap(_order, oldName, newName);
            held |= Swap(_wanted, oldName, newName);
            held |= RenameRoleLocked(oldName, newName);
            held |= RenameAddressesLocked(oldName, newName);
            if (!held)
            {
                return false;
            }

            if (string.Equals(_best, oldName, StringComparison.Ordinal))
            {
                _best = newName;
            }

            // The rules ride the same tunnel as before, so nobody takes their share again; a tunnel that is up
            // follows the name instead of being dialled again.
            _renamed.Add((oldName, newName));
            _moved = true;
        }

        Signal();
        return true;
    }

    /// <summary>
    /// The renames the tunnels have not followed yet; each is handed out once.
    /// </summary>
    public IReadOnlyList<(string From, string To)> DrainRenames()
    {
        lock (_gate)
        {
            if (_renamed.Count == 0)
            {
                return [];
            }

            var pending = _renamed.ToArray();
            _renamed.Clear();
            return pending;
        }
    }

    /// <summary>
    /// Lists the chain, first to last; a server it does not name leaves the mode's list.
    /// </summary>
    public void SetOrder(IReadOnlyList<string> names)
    {
        lock (_gate)
        {
            _order.Clear();
            Fill(_order, names);
            NormalizeLocked();
            TouchLocked();
        }

        Signal();
    }

    /// <summary>
    /// Puts a server at a place in the chain; answers whether the set moved. The first place carries the
    /// machine, a further one is the reserve in order, and a place of nought takes the server out of the chain.
    /// </summary>
    public bool Place(string name, int slot)
    {
        if (string.IsNullOrEmpty(name))
        {
            return false;
        }

        var roundTrips = live.RoundTrips();
        lock (_gate)
        {
            if (StandsLocked(name, slot))
            {
                return false;
            }

            _order.Remove(name);
            if (slot <= TunnelRoles.Aside)
            {
                _roles[name] = TunnelRoles.Neutral;
                _order.Add(name);
            }
            else
            {
                // The chain stands at the head of the list, so a place in it is an index into the list itself.
                _roles[name] = TunnelRoles.Reserve;
                _order.Insert(Math.Min(slot - 1, ChainLocked().Count), name);
            }

            NormalizeLocked();

            // The leader takes the balancer's pick back the moment it is named, so the rules following the pick
            // move with the place instead of at the next look.
            _best = ReconsiderLocked(roundTrips);
            TouchLocked();
        }

        Signal();
        return true;
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

            // What the mode stored as the primary keeps the machine, so it stands first in the chain.
            if (state.Primary.Length > 0 && _order.Remove(state.Primary))
            {
                _roles[state.Primary] = TunnelRoles.Primary;
                _order.Insert(0, state.Primary);
            }

            NormalizeLocked();
            _targets.Clear();
            foreach (var pair in state.Targets)
            {
                _targets[pair.Key] = pair.Value;
            }

            _wanted.Clear();
            Fill(_wanted, state.Desired);
            _best = string.Empty;
            _quiet = 0;
            _renamed.Clear();
            _stamp++;
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
                _wanted.ToArray(),
                new Dictionary<string, RuleRoute>(_targets, StringComparer.Ordinal));
        }
    }

    /// <summary>
    /// The set as the window sees it: every server of the library, in the order of the chain.
    /// </summary>
    public FleetSnapshot Describe(IReadOnlyList<string> library)
    {
        lock (_gate)
        {
            AdoptLocked(library);
            var servers = new List<FleetEntry>();
            foreach (var name in ListedLocked(library))
            {
                var duties = DutiesLocked(name);
                servers.Add(new FleetEntry(name, RoleLocked(name), _wanted.Contains(name), duties.CarriesDefault, duties.HoldsResolver, SlotLocked(name)));
            }

            var words = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var pair in _targets)
            {
                words[pair.Key] = pair.Value.Format();
            }

            return new FleetSnapshot(servers, PrimaryLocked(), CarrierLocked() ?? string.Empty, words, _policy);
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
    /// Where a rule is addressed, or the machine's own choice while it is not.
    /// </summary>
    public RuleRoute TargetOf(string key)
    {
        lock (_gate)
        {
            return TargetOfLocked(key);
        }
    }

    /// <summary>
    /// Addresses one rule; answers whether the address moved. A rule left to the machine on both ends is not
    /// held at all.
    /// </summary>
    public bool SetTarget(string key, RuleRoute route)
    {
        lock (_gate)
        {
            if (TargetOfLocked(key) == route)
            {
                return false;
            }

            if (route.IsDefault)
            {
                _targets.Remove(key);
            }
            else
            {
                _targets[key] = route;
            }

            _stamp++;
            _moved = true;
        }

        Signal();
        return true;
    }

    /// <summary>
    /// What a tunnel carries of the addressed rules: it moves only while its own share does.
    /// </summary>
    public long StampOf(string name)
    {
        var roundTrips = live.RoundTrips();
        lock (_gate)
        {
            var hash = new HashCode();
            foreach (var key in _targets.Keys.Order(StringComparer.Ordinal))
            {
                hash.Add(key, StringComparer.Ordinal);
                hash.Add(ShareOfLocked(key, name, roundTrips));
            }

            return hash.ToHashCode();
        }
    }

    /// <summary>
    /// The tunnel a rule rides: what it names while the set holds it, else what it falls to. A keyword answers
    /// for itself; null means nobody takes it, and the rule leaves every tunnel's share.
    /// </summary>
    public string? Rides(RuleRoute route, IReadOnlyDictionary<string, int>? roundTrips = null)
    {
        lock (_gate)
        {
            return ResolveLocked(route.Target, roundTrips) ?? ResolveLocked(route.Fallback, roundTrips);
        }
    }

    /// <summary>
    /// Takes the numbers the balancer is looked at by.
    /// </summary>
    public void SetPolicy(BalancePolicy policy)
    {
        lock (_gate)
        {
            _policy = policy;
        }
    }

    /// <summary>
    /// Looks the balancer over: the primary takes the pick back the moment it answers, and any other server
    /// takes it only by answering within its share of the pick's round trip. Answers whether the tunnels have
    /// to take their share again.
    /// </summary>
    public bool Rebalance(IReadOnlyDictionary<string, int> roundTrips)
    {
        lock (_gate)
        {
            var pick = ReconsiderLocked(roundTrips);
            if (string.Equals(pick, _best, StringComparison.Ordinal))
            {
                return false;
            }

            _best = pick;
            if (!RidesBestLocked())
            {
                return false;
            }

            _stamp++;
        }

        Signal();
        return true;
    }

    /// <summary>
    /// Gives a tunnel its role; answers whether the set moved. The role is the place: the primary stands first
    /// in the chain, a reserve out of it joins the end, and a neutral one leaves the chain.
    /// </summary>
    public bool SetRole(string name, string role)
    {
        var given = TunnelRoles.Of(role);
        if (given == TunnelRoles.Primary)
        {
            return Place(name, TunnelRoles.Lead);
        }

        if (given == TunnelRoles.Neutral)
        {
            return Place(name, TunnelRoles.Aside);
        }

        // A reserve already in the chain keeps the place it holds, which no role names.
        return RoleOf(name) == TunnelRoles.Neutral && Place(name, int.MaxValue);
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
    public override IReadOnlyList<GeoRule> Share(string name, long listId, IReadOnlyList<GeoRule> rules)
    {
        var roundTrips = live.RoundTrips();
        var share = new List<GeoRule>(rules.Count);
        var moved = false;
        foreach (var rule in rules)
        {
            // Only a rule that names a tunnel rides one: what is kept off the tunnel or dropped reads the same
            // on every server of the set.
            if (rule.Role != RouteRole.Proxy)
            {
                share.Add(rule);
                continue;
            }

            var rides = Rides(TargetOf(FleetTargets.Key(listId, GeoConfigurator.Format(rule))), roundTrips);
            if (string.Equals(rides, name, StringComparison.Ordinal))
            {
                share.Add(rule);
                continue;
            }

            moved = true;
            if (rides == RuleTarget.Block)
            {
                share.Add(rule with { Role = RouteRole.Block });
            }
            else if (rides == RuleTarget.Direct)
            {
                share.Add(rule with { Role = RouteRole.Direct });
            }
        }

        // The list itself while every rule of it rides this tunnel: a machine nobody has addressed a rule on
        // carries what it always carried.
        return moved ? share : rules;
    }

    /// <inheritdoc/>
    public override TunnelDuties For(string name)
    {
        lock (_gate)
        {
            return DutiesLocked(name);
        }
    }

    // One end of a rule: the server it points at while the set holds it.
    private string? ResolveLocked(RuleTarget target, IReadOnlyDictionary<string, int>? roundTrips)
    {
        return target.Mode switch
        {
            RuleTarget.Block => RuleTarget.Block,
            RuleTarget.Direct => RuleTarget.Direct,
            RuleTarget.Server => _wanted.Contains(target.Name) ? target.Name : null,
            RuleTarget.Best => BestLocked(roundTrips) ?? CarrierLocked(),
            _ => CarrierLocked(),
        };
    }

    // The pick stands while the set holds the server it names; nobody measured at all leaves the choice to the
    // order, which is the answer auto gives.
    private string? BestLocked(IReadOnlyDictionary<string, int>? roundTrips)
    {
        if (roundTrips is null)
        {
            return null;
        }

        if (!HoldsLocked(_best))
        {
            _best = QuickestLocked(roundTrips) ?? string.Empty;
        }

        return _best.Length > 0 ? _best : null;
    }

    // The timed look at the balancer: the primary while it answers, else the pick it holds, and another server
    // only while it answers in less than half the time.
    private string ReconsiderLocked(IReadOnlyDictionary<string, int> roundTrips)
    {
        var primary = PrimaryLocked();
        if (AvailableLocked(primary, roundTrips))
        {
            _quiet = 0;
            return primary;
        }

        var quickest = QuickestLocked(roundTrips);
        if (!HoldsLocked(_best))
        {
            _quiet = 0;
            return quickest ?? string.Empty;
        }

        if (AvailableLocked(_best, roundTrips))
        {
            _quiet = 0;
            return quickest is not null && roundTrips[quickest] * 100 < roundTrips[_best] * _policy.MarginPercent ? quickest : _best;
        }

        // The pick is silent: a look or two is a tunnel being dialled again, and a set where nobody answers has
        // nobody to hand the rules to either.
        if (quickest is null || ++_quiet < _policy.Strikes)
        {
            return _best;
        }

        _quiet = 0;
        return quickest;
    }

    // The quickest to answer of the servers the balancer may pick.
    private string? QuickestLocked(IReadOnlyDictionary<string, int> roundTrips)
    {
        var best = default(string);
        var quickest = int.MaxValue;
        foreach (var name in PriorityLocked())
        {
            if (!AvailableLocked(name, roundTrips) || roundTrips[name] >= quickest)
            {
                continue;
            }

            best = name;
            quickest = roundTrips[name];
        }

        return best;
    }

    // A server the balancer may pick right now: held by it and answering.
    private bool AvailableLocked(string name, IReadOnlyDictionary<string, int> roundTrips)
    {
        return HoldsLocked(name) && roundTrips.TryGetValue(name, out var trip) && trip >= 0;
    }

    // A server the balancer may pick at all: asked for and in the balancer.
    private bool HoldsLocked(string name)
    {
        return name.Length > 0 && _wanted.Contains(name) && TunnelRoles.Balanced(RoleLocked(name));
    }

    // Whether any rule follows the balancer; a set where none does reads the same whoever the pick is.
    private bool RidesBestLocked()
    {
        return _targets.Values.Any(route => route.Target.Mode == RuleTarget.Best || route.Fallback.Mode == RuleTarget.Best);
    }

    // Both ends of every rule: one naming a server that is gone is left to the machine again.
    private bool ForgetAddressesLocked(string name)
    {
        var struck = false;
        foreach (var key in _targets.Keys.ToArray())
        {
            var route = _targets[key];
            var moved = new RuleRoute(
                Names(route.Target, name) ? RuleTarget.Default : route.Target,
                Names(route.Fallback, name) ? RuleTarget.Default : route.Fallback);
            if (moved == route)
            {
                continue;
            }

            struck = true;
            if (moved.IsDefault)
            {
                _targets.Remove(key);
            }
            else
            {
                _targets[key] = moved;
            }
        }

        return struck;
    }

    // Both ends of every rule: one naming a renamed server names it as it is called now.
    private bool RenameAddressesLocked(string oldName, string newName)
    {
        var moved = false;
        foreach (var key in _targets.Keys.ToArray())
        {
            var route = _targets[key];
            var renamed = new RuleRoute(
                Names(route.Target, oldName) ? new RuleTarget(RuleTarget.Server, newName) : route.Target,
                Names(route.Fallback, oldName) ? new RuleTarget(RuleTarget.Server, newName) : route.Fallback);
            if (renamed == route)
            {
                continue;
            }

            _targets[key] = renamed;
            moved = true;
        }

        return moved;
    }

    // Carries the role of a renamed server over.
    private bool RenameRoleLocked(string oldName, string newName)
    {
        if (!_roles.Remove(oldName, out var role))
        {
            return false;
        }

        _roles[newName] = role;
        return true;
    }

    private static bool Names(RuleTarget end, string name)
    {
        return end.Mode == RuleTarget.Server && string.Equals(end.Name, name, StringComparison.Ordinal);
    }

    // How one rule reads on a tunnel: its own, kept off the tunnel, dropped, or somebody else's.
    private int ShareOfLocked(string key, string name, IReadOnlyDictionary<string, int> roundTrips)
    {
        var route = _targets[key];
        var rides = ResolveLocked(route.Target, roundTrips) ?? ResolveLocked(route.Fallback, roundTrips);
        if (string.Equals(rides, name, StringComparison.Ordinal))
        {
            return 1;
        }

        if (rides == RuleTarget.Block)
        {
            return 2;
        }

        return rides == RuleTarget.Direct ? 3 : 0;
    }

    private RuleRoute TargetOfLocked(string key)
    {
        return _targets.TryGetValue(key, out var route) ? route : RuleRoute.Default;
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

    // A move of the set changes where an addressed rule rides, so the tunnels carrying one are asked to take
    // their share again. A set nobody addressed a rule in reads the same after the move as before it.
    private void TouchLocked()
    {
        _moved = true;
        if (_targets.Count > 0)
        {
            _stamp++;
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

    // The servers the fallback walks, in the order it walks them.
    private List<string> ChainLocked()
    {
        return _order.Where(name => RoleLocked(name) != TunnelRoles.Neutral).ToList();
    }

    // The place a server holds in the chain; one out of it holds none.
    private int SlotLocked(string name)
    {
        var at = TunnelRoles.Aside;
        foreach (var listed in ChainLocked())
        {
            at++;
            if (string.Equals(listed, name, StringComparison.Ordinal))
            {
                return at;
            }
        }

        return TunnelRoles.Aside;
    }

    // Whether a server already stands where it is asked to.
    private bool StandsLocked(string name, int slot)
    {
        return slot > TunnelRoles.Aside
            ? SlotLocked(name) == slot
            : _order.Contains(name) && RoleLocked(name) == TunnelRoles.Neutral;
    }

    // The chain stands at the head of the list and the servers out of it after it, so a place in the list is
    // the priority itself: the first carries the machine, the rest are the reserve in order.
    private void NormalizeLocked()
    {
        var chain = ChainLocked();
        var aside = _order.Where(name => RoleLocked(name) == TunnelRoles.Neutral).ToList();
        _order.Clear();
        _order.AddRange(chain);
        _order.AddRange(aside);
        for (var at = 0; at < chain.Count; at++)
        {
            _roles[chain[at]] = TunnelRoles.At(at + 1);
        }
    }

    // A server the chain has not met joins the end of the reserve, and the first to join it carries the machine.
    private void AdoptLocked(IReadOnlyList<string> library)
    {
        var joined = false;
        foreach (var name in library)
        {
            if (name.Length > 0 && !_order.Contains(name))
            {
                _order.Add(name);
                joined = true;
            }
        }

        if (joined)
        {
            NormalizeLocked();
        }
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

    // Keeps the place a renamed server holds in a list.
    private static bool Swap(List<string> list, string oldName, string newName)
    {
        var at = list.IndexOf(oldName);
        if (at < 0)
        {
            return false;
        }

        list[at] = newName;
        return true;
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
