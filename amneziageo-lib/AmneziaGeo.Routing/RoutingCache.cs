using System.Collections.Concurrent;
using System.Net;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;

namespace AmneziaGeo.Routing;

/// <summary>
/// What a verdict asks the system for once the mode is taken into account.
/// </summary>
public enum RoutePlan
{
    /// <summary>
    /// Nothing: the destination already follows the right default.
    /// </summary>
    None,

    /// <summary>
    /// A firewall permit out the physical path.
    /// </summary>
    Permit,

    /// <summary>
    /// A permit plus a host route out the physical path.
    /// </summary>
    Bypass,

    /// <summary>
    /// A firewall drop for this one address.
    /// </summary>
    Drop,

    /// <summary>
    /// A host route into the tunnel plus its advertisement to the peer.
    /// </summary>
    Tunnel,

    /// <summary>
    /// Installed by the domain tracker; the cache tracks the contact and touches nothing.
    /// </summary>
    External,
}

/// <summary>
/// Classifies destinations against the routing lists and installs a bypass route for those that need one, reclaiming
/// it once idle. One entry per address carries both the verdict and what the verdict installed, so a repeat
/// destination costs a single dictionary hit.
/// </summary>
public sealed class RoutingCache
{
    private const int ScanIntervalMs = 5_000;
    // Upper bound on scans between reclaims; a short idle window reclaims more often so it is actually honoured.
    private const int MaxScansPerSweep = 12;
    // Reclaim in slices: dropping hundreds of filters at once is the storm this design exists to avoid.
    private const int SweepBatch = 64;
    // Entries holding system resources; past this an address follows the default route instead.
    private const int MaxApplied = 8192;
    // Entries overall. A verdict without resources costs a dictionary slot, so it gets a wider ceiling.
    private const int MaxEntries = 65536;

    private sealed class Entry
    {
        public IPAddress Address = IPAddress.Any;
        public uint Numeric;
        public RouteVerdict Verdict;
        public RoutePlan Plan;
        public long LastTouch;
        public bool Routed;
        public bool Tunneled;
        public bool ByApp;
        // Settled by a name, not by the ranges: a range covering the address must not take it back.
        public bool ByName;
        public uint InterfaceIndex;
        public ulong FilterOut;
        public ulong FilterIn;
        public int Generation;
        // Rule set the verdict was taken under.
        public int Rules;
    }

    // Swapped as one immutable set, so a live rule edit never leaves a half-applied mix. The generation tells an
    // entry decided under an older set from one already decided under this one.
    private sealed record RuleSet(GeoIpRanges Proxy, GeoIpRanges Direct, GeoIpRanges Block, int Generation);

    // A dropped destination and the image it belonged to, as reported by the firewall.
    private readonly record struct Reported(uint Address, string? App);

    // Destinations reported from a caller that must not block - the firewall's own event thread. Bounded and
    // drop-on-full: a flood of drops to one address repeats, so losing a report costs a retransmit, not a route.
    private readonly Channel<Reported> _reported = Channel.CreateBounded<Reported>(
        new BoundedChannelOptions(4096) { FullMode = BoundedChannelFullMode.DropWrite, SingleReader = true });

    private readonly ConcurrentDictionary<uint, Entry> _entries = new();
    // Addresses the cache neither installs nor reclaims: the tunnel resolver, routed as infrastructure at bring-up.
    // Its route must outlive every idle window - the agent's own queries to it are not attributed to any process,
    // so nothing here would ever refresh it and a sweep would take the tunnel's DNS down with it.
    private readonly GeoIpRanges _pinned;
    private readonly IRouteApplier _applier;
    private readonly ILiveDestinations _live;
    private readonly bool _split;
    private long _idleTtlMs;
    private int _scansPerSweep;
    private readonly ILogger<RoutingCache> _logger;
    // Tells whether a dropped image belongs to the app rules; its destinations then take the tunnel.
    private Func<string, bool>? _appMatch;
    // Tells whether an adopted address is still held by a tracked domain.
    private Func<IPAddress, bool>? _adopted;
    private RuleSet _rules;
    private int _size;
    private int _applied;
    private int _installed;
    private int _reclaimed;
    private int _capacityWarned;

    /// <summary>
    /// ctor
    /// </summary>
    public RoutingCache(IRouteApplier applier, ILiveDestinations live, bool split, IReadOnlyList<string> proxy, IReadOnlyList<string> direct, IReadOnlyList<string> block, int ttlSeconds, ILogger<RoutingCache> logger, IReadOnlyCollection<string>? pinned = null)
    {
        _applier = applier;
        _live = live;
        _split = split;
        SetTtl(ttlSeconds);
        _logger = logger;
        _rules = Build(proxy, direct, block, 0);
        _pinned = pinned is null ? GeoIpRanges.Empty : GeoIpRanges.Build([.. pinned]);
        if (_pinned.Count > 0)
        {
            _logger.LogDebug("{Count} address range(s) are held outside the cache: their path through the tunnel is set up with the connection and stays for as long as it lasts", _pinned.Count);
        }
    }

    /// <summary>
    /// Idle window an entry survives without traffic, in seconds.
    /// </summary>
    public int TtlSeconds => (int)(Volatile.Read(ref _idleTtlMs) / 1000);

    /// <summary>
    /// Applies an idle window to the entries already held; the next sweep reclaims whatever it now covers.
    /// </summary>
    public void SetTtl(int seconds)
    {
        var idle = Math.Max(seconds, 0) * 1000L;
        Volatile.Write(ref _idleTtlMs, idle);
        Volatile.Write(ref _scansPerSweep, (int)Math.Clamp(idle / 5 / ScanIntervalMs, 1, MaxScansPerSweep));
    }

    /// <summary>
    /// Host routes currently installed.
    /// </summary>
    public int Active => Volatile.Read(ref _applied);

    /// <summary>
    /// Entries held, applied or verdict-only.
    /// </summary>
    public int Size => Volatile.Read(ref _size);

    /// <summary>
    /// Whether the tunnel carries only what the list names; everything else follows the physical path.
    /// </summary>
    public bool Split => _split;

    /// <summary>
    /// A held destination: its verdict, what that verdict installed, whether a name settled it, and the idle time
    /// left before reclaim.
    /// </summary>
    public sealed record Held(IPAddress Address, RouteVerdict Verdict, RoutePlan Plan, bool Routed, bool Adopted, bool ByName, int IdleSeconds, int TtlSeconds);

    /// <summary>
    /// Destinations held right now, each with the time left on it.
    /// </summary>
    public IReadOnlyList<Held> Snapshot()
    {
        var now = Environment.TickCount64;
        var ttl = TtlSeconds;
        var result = new List<Held>();
        foreach (var pair in _entries)
        {
            var entry = pair.Value;
            var idle = (int)Math.Clamp((now - Volatile.Read(ref entry.LastTouch)) / 1000, 0, ttl);
            result.Add(new Held(entry.Address, entry.Verdict, entry.Plan, entry.Routed || entry.Tunneled,
                entry.Plan == RoutePlan.External, entry.ByName, idle, ttl));
        }

        return result;
    }

    /// <summary>
    /// Merged range counts, for logging.
    /// </summary>
    public (int Proxy, int Direct, int Block) RangeCounts
    {
        get
        {
            var rules = Volatile.Read(ref _rules);
            return (rules.Proxy.Count, rules.Direct.Count, rules.Block.Count);
        }
    }

    /// <summary>
    /// Whether any rule can match.
    /// </summary>
    public bool HasRules
    {
        get
        {
            var rules = Volatile.Read(ref _rules);
            return rules.Direct.Count > 0 || rules.Block.Count > 0 || rules.Proxy.Count > 0;
        }
    }

    /// <summary>
    /// Verdict for a host-order address, from the cache when already seen. Creates no entry.
    /// </summary>
    public RouteVerdict Classify(uint address)
    {
        if (_entries.TryGetValue(address, out var entry))
        {
            return entry.Verdict;
        }

        return Evaluate(Volatile.Read(ref _rules), address);
    }

    /// <summary>
    /// Verdict for an address; anything but IPv4 is unlisted - the rule sets are v4-only.
    /// </summary>
    public RouteVerdict Classify(IPAddress address)
    {
        return GeoIpRanges.TryToNumeric(address, out var value) ? Classify(value) : RouteVerdict.None;
    }

    /// <summary>
    /// Raised when an app rule alone puts a destination in the tunnel - no range covers it. Nothing resolved to
    /// such an address, so this is the only moment it can be learned.
    /// </summary>
    public event Action<IPAddress>? AppDestination;

    /// <summary>
    /// Records contact with a destination and installs its bypass route on first sight. Called per DNS answer, per
    /// connect event and per table scan.
    /// </summary>
    public void Note(uint address)
    {
        Note(address, false);
    }

    /// <summary>
    /// Records contact with a destination, saying whether a process the app rules cover owns it. In split such a
    /// destination rides the tunnel whatever the ranges say, and one already pinned to the physical path is moved
    /// onto it - which is what makes a wrong first guess recoverable instead of permanent.
    /// </summary>
    public void Note(uint address, bool app)
    {
        if (_pinned.Contains(address))
        {
            return;
        }

        var now = Environment.TickCount64;
        if (_entries.TryGetValue(address, out var existing))
        {
            Volatile.Write(ref existing.LastTouch, now);
            if (app)
            {
                Promote(existing, now);
                return;
            }

            if (Redecided(existing, now) || Installed(existing))
            {
                return;
            }

            Install(existing, now);
            return;
        }

        Admit(address, now, app);
    }

    /// <summary>
    /// Records contact with a destination whose verdict something outside the ranges already settled - a name, not
    /// an address. A verdict that differs from the one in force releases what the old one installed first.
    /// </summary>
    public void Note(uint address, RouteVerdict verdict)
    {
        if (_pinned.Contains(address))
        {
            return;
        }

        var now = Environment.TickCount64;
        if (!_entries.TryGetValue(address, out var existing))
        {
            Admit(address, now, false, verdict);
            return;
        }

        Volatile.Write(ref existing.LastTouch, now);
        existing.ByName = true;
        existing.Rules = Volatile.Read(ref _rules).Generation;

        // An adopted address belongs to the domain tracker; the two must not install competing routes for it.
        if (existing.Verdict == verdict || existing.Plan == RoutePlan.External)
        {
            if (!Installed(existing))
            {
                Install(existing, now);
            }

            return;
        }

        Reclassify(existing, verdict, now);
    }

    /// <summary>
    /// Records contact with an address; non-IPv4 is ignored.
    /// </summary>
    public void Note(IPAddress address)
    {
        if (GeoIpRanges.TryToNumeric(address, out var value))
        {
            Note(value);
        }
    }

    /// <summary>
    /// Records contact with an address whose name settled its verdict; non-IPv4 is ignored.
    /// </summary>
    public void Note(IPAddress address, RouteVerdict verdict)
    {
        if (GeoIpRanges.TryToNumeric(address, out var value))
        {
            Note(value, verdict);
        }
    }

    /// <summary>
    /// Reinstalls permits for live entries after the filter set was rebuilt; routes survive an arm, filters do not.
    /// </summary>
    public void Reinstall()
    {
        var generation = _applier.Generation;
        var refreshed = 0;
        foreach (var pair in _entries)
        {
            var entry = pair.Value;
            if (entry.Plan is not (RoutePlan.Permit or RoutePlan.Bypass or RoutePlan.Drop) || entry.Generation == generation)
            {
                continue;
            }

            lock (entry)
            {
                if (entry.Generation == generation)
                {
                    continue;
                }

                var filtered = entry.Plan == RoutePlan.Drop
                    ? _applier.TryDrop(pair.Key, out var outId, out var inId, out var installed)
                    : _applier.TryPermit(pair.Key, out outId, out inId, out installed);
                if (filtered)
                {
                    entry.FilterOut = outId;
                    entry.FilterIn = inId;
                    entry.Generation = installed;
                    refreshed++;
                }
            }
        }

        if (refreshed > 0)
        {
            _logger.LogDebug("the firewall was rearmed: {Count} host permit(s) reinstalled so their traffic keeps flowing", refreshed);
        }
    }

    /// <summary>
    /// Queues a destination reported from a thread that must not do work: installing a route or a filter from the
    /// firewall's event callback would re-enter the engine that raised it.
    /// </summary>
    public void Report(IPAddress address, string? app)
    {
        if (GeoIpRanges.TryToNumeric(address, out var value))
        {
            _reported.Writer.TryWrite(new Reported(value, app));
        }
    }

    /// <summary>
    /// Attaches the app-rule test applied to reported drops; without it a dropped destination is decided by ranges alone.
    /// </summary>
    public void SetAppMatch(Func<string, bool>? match)
    {
        _appMatch = match;
    }

    /// <summary>
    /// Attaches the test that says whether an adopted address is still held by a tracked domain; without it an
    /// adopted address is reclaimed on the ordinary idle clock like any other.
    /// </summary>
    public void SetAdoptionCheck(Func<IPAddress, bool>? check)
    {
        Volatile.Write(ref _adopted, check);
    }

    /// <summary>
    /// Applies reported destinations as they arrive, until cancelled.
    /// </summary>
    public async Task PumpAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var reported in _reported.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                try
                {
                    Note(reported.Address, Matches(reported.App));
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "routing cache: reported destination failed");
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>
    /// Scans the live destinations and reclaims idle entries until cancelled.
    /// </summary>
    public async Task RunAsync(CancellationToken ct)
    {
        var scans = 0;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(ScanIntervalMs, ct).ConfigureAwait(false);
                try
                {
                    var active = _live.Snapshot();
                    foreach (var address in active.All)
                    {
                        // Attribution decides the verdict: a matched app's destination noted as ordinary traffic
                        // earns a permit out the physical path, and a permitted address is never dropped again - so
                        // the firewall's report, the one place the app rule could still apply, never comes.
                        Note(address, active.App.Contains(address));
                    }

                    if (++scans % Volatile.Read(ref _scansPerSweep) == 0)
                    {
                        Sweep(active.All, Environment.TickCount64);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "routing cache: scan failed");
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>
    /// Swaps the rule sets after a live list edit and decides every destination already held against them, moving
    /// the ones that changed side there and then. Without this a rule change would reach an address in use only
    /// once its traffic stopped for the whole idle window - which under load never happens.
    /// </summary>
    public void Rebuild(IReadOnlyList<string> proxy, IReadOnlyList<string> direct, IReadOnlyList<string> block)
    {
        var rules = Build(proxy, direct, block, Volatile.Read(ref _rules).Generation + 1);
        Volatile.Write(ref _rules, rules);
        var moved = Redecide(rules);
        _logger.LogInformation("routing rules reloaded: {Proxy} tunnel, {Direct} direct, {Block} blocked range(s); {Moved} destination(s) in use changed side at once, the rest keep the path they had",
            rules.Proxy.Count, rules.Direct.Count, rules.Block.Count, moved);
    }

    /// <summary>
    /// Drops every installed route and filter.
    /// </summary>
    public void RemoveAll()
    {
        Drop();
        _logger.LogInformation("routing cache cleared: {Installed} host route(s) installed and {Reclaimed} released during this session",
            Volatile.Read(ref _installed), Volatile.Read(ref _reclaimed));
    }

    // Whether a dropped image is covered by the app rules.
    private bool Matches(string? app)
    {
        if (app is null || _appMatch is not { } match)
        {
            return false;
        }

        try
        {
            return match(app);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "routing cache: app match failed for {App}", app);
            return false;
        }
    }

    // Moves an already-decided destination onto the tunnel once an app rule claims it: what the old plan installed
    // is released first, so a permit out the physical path never outlives the decision that replaced it.
    private void Promote(Entry entry, long now)
    {
        if (!_split || entry.Plan is RoutePlan.Tunnel or RoutePlan.Drop or RoutePlan.External || entry.Verdict is RouteVerdict.Direct)
        {
            if (!Installed(entry))
            {
                Install(entry, now);
            }

            return;
        }

        var filters = new List<(ulong Out, ulong In)>();
        var withdrawn = new List<IPAddress>();
        var generation = _applier.Generation;
        lock (entry)
        {
            Release(entry, generation, filters, withdrawn);
            entry.Plan = RoutePlan.Tunnel;
            entry.ByApp = true;
        }

        _applier.RemoveTunnel(withdrawn);
        _applier.DeleteFilters(filters, generation);
        Install(entry, now);
    }

    // Swaps a held destination onto another verdict: what the old plan installed is released first, so a bypass
    // never outlives the decision that replaced it.
    private void Reclassify(Entry entry, RouteVerdict verdict, long now)
    {
        var filters = new List<(ulong Out, ulong In)>();
        var withdrawn = new List<IPAddress>();
        var generation = _applier.Generation;
        lock (entry)
        {
            Release(entry, generation, filters, withdrawn);
            entry.Verdict = verdict;
            entry.Plan = Decide(verdict);
        }

        _applier.RemoveTunnel(withdrawn);
        _applier.DeleteFilters(filters, generation);
        Install(entry, now);
    }

    // Decides every held destination against the rules just installed and moves the ones that changed side.
    // A verdict a name settled goes with the old rules: the name decides again on its next answer, and until
    // then the ranges own the address.
    private int Redecide(RuleSet rules)
    {
        var moved = 0;
        var now = Environment.TickCount64;
        foreach (var pair in _entries)
        {
            var entry = pair.Value;
            entry.Rules = rules.Generation;
            if (entry.Plan == RoutePlan.External || entry.ByApp)
            {
                continue;
            }

            entry.ByName = false;
            var verdict = Evaluate(rules, entry.Numeric);
            if (verdict == entry.Verdict)
            {
                continue;
            }

            Reclassify(entry, verdict, now);
            moved++;
        }

        return moved;
    }

    // Decides a destination admitted under an older rule set again on its next contact; true when it changed side.
    // Covers what the rebuild's own pass could not see: an address admitted while it was running.
    private bool Redecided(Entry entry, long now)
    {
        var rules = Volatile.Read(ref _rules);
        if (entry.Rules == rules.Generation)
        {
            return false;
        }

        entry.Rules = rules.Generation;
        if (entry.ByName || entry.ByApp || entry.Plan == RoutePlan.External)
        {
            return false;
        }

        var verdict = Evaluate(rules, entry.Numeric);
        if (verdict == entry.Verdict)
        {
            return false;
        }

        Reclassify(entry, verdict, now);
        return true;
    }

    // Creates the entry for an address seen for the first time and applies what its verdict asks for.
    private void Admit(uint address, long now, bool app = false, RouteVerdict? forced = null)
    {
        var rules = Volatile.Read(ref _rules);
        var verdict = forced ?? Evaluate(rules, address);
        var entry = new Entry
        {
            Address = ToAddress(address),
            Numeric = address,
            Verdict = verdict,
            Plan = Decide(verdict, app),
            ByApp = app,
            ByName = forced is not null,
            Rules = rules.Generation,
            LastTouch = now,
        };

        if (Volatile.Read(ref _size) >= MaxEntries)
        {
            TrimUnapplied();
        }

        if (!_entries.TryAdd(address, entry))
        {
            if (_entries.TryGetValue(address, out var raced) && !Installed(raced))
            {
                Install(raced, now);
            }

            return;
        }

        Interlocked.Increment(ref _size);
        if (!Installed(entry))
        {
            Install(entry, now);
        }
    }

    /// <summary>
    /// Adopts addresses the domain tracker installed: the cache records the contact but installs and reclaims
    /// nothing, so the two never fight over one address.
    /// </summary>
    public void Adopt(IReadOnlyCollection<IPAddress> addresses)
    {
        var now = Environment.TickCount64;
        foreach (var address in addresses)
        {
            if (!GeoIpRanges.TryToNumeric(address, out var value) || _pinned.Contains(value))
            {
                continue;
            }

            if (_entries.TryGetValue(value, out var existing))
            {
                Volatile.Write(ref existing.LastTouch, now);
                continue;
            }

            var rules = Volatile.Read(ref _rules);
            var entry = new Entry
            {
                Address = address,
                Numeric = value,
                Verdict = Evaluate(rules, value),
                Plan = RoutePlan.External,
                Rules = rules.Generation,
                LastTouch = now,
            };

            if (_entries.TryAdd(value, entry))
            {
                Interlocked.Increment(ref _size);
            }
        }
    }

    /// <summary>
    /// Most recent contact recorded for any of these addresses; null when none of them is held. Lets a name's
    /// lifetime follow the traffic to its addresses, not only the resolutions of the name itself.
    /// </summary>
    public long? LastContact(IReadOnlyCollection<string> addresses)
    {
        var last = default(long?);
        foreach (var address in addresses)
        {
            if (!IPAddress.TryParse(address, out var parsed)
                || !GeoIpRanges.TryToNumeric(parsed, out var value)
                || !_entries.TryGetValue(value, out var entry))
            {
                continue;
            }

            var touch = Volatile.Read(ref entry.LastTouch);
            if (last is null || touch > last)
            {
                last = touch;
            }
        }

        return last;
    }

    /// <summary>
    /// Drops adopted addresses the domain tracker evicted.
    /// </summary>
    public void Forget(IReadOnlyCollection<IPAddress> addresses)
    {
        foreach (var address in addresses)
        {
            if (!GeoIpRanges.TryToNumeric(address, out var value))
            {
                continue;
            }

            if (_entries.TryGetValue(value, out var entry) && entry.Plan == RoutePlan.External && _entries.TryRemove(value, out _))
            {
                Interlocked.Decrement(ref _size);
            }
        }
    }

    // Whether the entry already holds everything its plan asks for under the current filter set.
    private bool Installed(Entry entry)
    {
        return entry.Plan switch
        {
            RoutePlan.Tunnel => entry.Tunneled,
            RoutePlan.Bypass => entry.Routed && entry.Generation == _applier.Generation,
            RoutePlan.Permit or RoutePlan.Drop => entry.Generation == _applier.Generation,
            _ => true,
        };
    }

    private void Install(Entry entry, long now)
    {
        if (Apply(entry, now))
        {
            // Off the entry lock: the sink routes the rest of the app's remembered addresses, which takes locks of
            // its own.
            AppDestination?.Invoke(entry.Address);
        }
    }

    // Applies what the plan asks for; true when an app rule alone brought the destination into the tunnel.
    private bool Apply(Entry entry, long now)
    {
        lock (entry)
        {
            Volatile.Write(ref entry.LastTouch, now);
            if (Installed(entry))
            {
                return false;
            }

            var holds = entry.Plan is RoutePlan.Bypass or RoutePlan.Tunnel;
            if (holds && !entry.Routed && !entry.Tunneled && Volatile.Read(ref _applied) >= MaxApplied)
            {
                if (Interlocked.Exchange(ref _capacityWarned, 1) == 0)
                {
                    _logger.LogWarning("the host-route limit is reached ({Max} in use); further addresses follow the default route instead of their own rule until some are freed", MaxApplied);
                }

                return false;
            }

            // Into the tunnel: the route and the advertisement travel together, and no permit applies - the traffic
            // leaves through the tunnel interface, which the kill-switch permits wholesale.
            if (entry.Plan == RoutePlan.Tunnel)
            {
                if (!_applier.TryTunnel(entry.Address))
                {
                    return false;
                }

                entry.Tunneled = true;
                Interlocked.Increment(ref _applied);
                Interlocked.Increment(ref _installed);
                return entry.ByApp && entry.Verdict != RouteVerdict.Proxy;
            }

            // Permit first: until the route exists the traffic still rides the tunnel, whereas a route without a
            // permit hands the packets to a physical path the kill-switch drops. A blocked address takes the same
            // path with the opposite action.
            var generation = _applier.Generation;
            var filtered = entry.Plan == RoutePlan.Drop
                ? _applier.TryDrop(entry.Numeric, out var outId, out var inId, out var installed)
                : _applier.TryPermit(entry.Numeric, out outId, out inId, out installed);
            if (entry.Generation != generation && filtered)
            {
                entry.FilterOut = outId;
                entry.FilterIn = inId;
                entry.Generation = installed;
            }

            if (entry.Plan != RoutePlan.Bypass || entry.Routed)
            {
                return false;
            }

            if (_applier.TryAddRoute(entry.Address, out var index))
            {
                entry.InterfaceIndex = index;
                entry.Routed = true;
                Interlocked.Increment(ref _applied);
                Interlocked.Increment(ref _installed);
            }

            return false;
        }
    }

    // Reclaims idle entries. The busy set is the scan's own snapshot: a live connection must keep its route, because
    // moving its egress interface swaps the source address and the peer drops the flow. RunAsync owns the schedule,
    // this owns the decision.
    internal void Sweep(HashSet<uint> busy, long now)
    {
        var stale = new List<KeyValuePair<uint, Entry>>();
        var idleTtlMs = Volatile.Read(ref _idleTtlMs);
        foreach (var pair in _entries)
        {
            if (now - Volatile.Read(ref pair.Value.LastTouch) <= idleTtlMs)
            {
                continue;
            }

            // An adopted address leaves with the name that resolved it; one whose name is already gone belongs to
            // nobody and goes now, whatever installed it.
            if (pair.Value.Plan == RoutePlan.External && Volatile.Read(ref _adopted) is { } adopted && adopted(pair.Value.Address))
            {
                continue;
            }

            // A destination an app rule claimed keeps its route: no name resolves it back into the tunnel, so the
            // attempt that would earn the route again is the very attempt that loses its answer.
            if (pair.Value.ByApp)
            {
                continue;
            }

            stale.Add(pair);
            if (stale.Count >= SweepBatch)
            {
                break;
            }
        }

        if (stale.Count == 0)
        {
            return;
        }

        var filters = new List<(ulong Out, ulong In)>();
        var withdrawn = new List<IPAddress>();
        var generation = _applier.Generation;
        var kept = 0;
        var dropped = 0;

        foreach (var (address, entry) in stale)
        {
            if (busy.Contains(address))
            {
                Volatile.Write(ref entry.LastTouch, now);
                kept++;
                continue;
            }

            if (!_entries.TryRemove(address, out _))
            {
                continue;
            }

            Interlocked.Decrement(ref _size);
            lock (entry)
            {
                Release(entry, generation, filters, withdrawn);
            }

            dropped++;
        }

        _applier.RemoveTunnel(withdrawn);
        _applier.DeleteFilters(filters, generation);

        if (dropped > 0)
        {
            Interlocked.Add(ref _reclaimed, dropped);
            Volatile.Write(ref _capacityWarned, 0);
            _logger.LogDebug("{Dropped} unused destination(s) forgotten, {Kept} kept because traffic is still flowing; {Active} host route(s) remain, and a forgotten address is decided again on the next contact",
                dropped, kept, Volatile.Read(ref _applied));
        }
    }

    // Drops verdict-only entries at capacity: they hold no system resources, so refilling one costs a binary search.
    private void TrimUnapplied()
    {
        var dropped = 0;
        foreach (var pair in _entries)
        {
            if (pair.Value.Plan != RoutePlan.None)
            {
                continue;
            }

            if (_entries.TryRemove(pair.Key, out _))
            {
                Interlocked.Decrement(ref _size);
                dropped++;
            }
        }

        if (dropped > 0)
        {
            _logger.LogDebug("the cache is full: {Count} remembered decision(s) without a route were forgotten to make room", dropped);
        }
    }

    private void Drop()
    {
        var filters = new List<(ulong Out, ulong In)>();
        var withdrawn = new List<IPAddress>();
        var generation = _applier.Generation;
        foreach (var address in _entries.Keys)
        {
            if (!_entries.TryRemove(address, out var entry))
            {
                continue;
            }

            Interlocked.Decrement(ref _size);
            lock (entry)
            {
                Release(entry, generation, filters, withdrawn);
            }
        }

        _applier.RemoveTunnel(withdrawn);
        _applier.DeleteFilters(filters, generation);
        Volatile.Write(ref _capacityWarned, 0);
    }

    // Removes the physical-path route now and queues the tunnelled address and the filter ids for their batched
    // removal: the route goes first so the traffic falls back to the default path before the permit disappears.
    private void Release(Entry entry, int generation, List<(ulong Out, ulong In)> filters, List<IPAddress> tunneled)
    {
        if (entry.Tunneled)
        {
            tunneled.Add(entry.Address);
            entry.Tunneled = false;
            Interlocked.Decrement(ref _applied);
        }

        if (entry.Routed)
        {
            _applier.RemoveRoute(entry.Address, entry.InterfaceIndex);
            entry.Routed = false;
            Interlocked.Decrement(ref _applied);
        }

        if (entry.Generation == generation && (entry.FilterOut != 0 || entry.FilterIn != 0))
        {
            filters.Add((entry.FilterOut, entry.FilterIn));
        }

        entry.FilterOut = 0;
        entry.FilterIn = 0;
    }

    // Block wins over Direct: a blocked address must never earn a bypass. Direct wins over Proxy: an address in both
    // lists gets one verdict, so the two can no longer install competing routes.
    private static RouteVerdict Evaluate(RuleSet rules, uint address)
    {
        if (rules.Block.Contains(address))
        {
            return RouteVerdict.Block;
        }

        if (rules.Direct.Contains(address))
        {
            return RouteVerdict.Direct;
        }

        return rules.Proxy.Contains(address) ? RouteVerdict.Proxy : RouteVerdict.None;
    }

    // Full tunnel: the tunnel is the default, so only Direct leaves it and Block is already dropped by WFP.
    // Split: nothing rides the tunnel until a destination earns it, and the physical path is blocked until a
    // verdict exists - so Proxy earns the tunnel, Block stays dropped, and everything else earns a permit.
    // app: claimed by an app rule. Block still wins, and so does an explicit Direct range - the user pinned that
    // destination to the physical path on purpose.
    private RoutePlan Decide(RouteVerdict verdict, bool app = false)
    {
        if (verdict == RouteVerdict.Block)
        {
            return RoutePlan.Drop;
        }

        if (!_split)
        {
            return verdict == RouteVerdict.Direct ? RoutePlan.Bypass : RoutePlan.None;
        }

        if (verdict == RouteVerdict.Direct)
        {
            return RoutePlan.Permit;
        }

        return verdict == RouteVerdict.Proxy || app ? RoutePlan.Tunnel : RoutePlan.Permit;
    }

    private static RuleSet Build(IReadOnlyList<string> proxy, IReadOnlyList<string> direct, IReadOnlyList<string> block, int generation)
    {
        return new RuleSet(GeoIpRanges.Build(proxy), GeoIpRanges.Build(direct), GeoIpRanges.Build(block), generation);
    }

    private static IPAddress ToAddress(uint address)
    {
        return new IPAddress(new[] { (byte)(address >> 24), (byte)(address >> 16), (byte)(address >> 8), (byte)address });
    }
}
