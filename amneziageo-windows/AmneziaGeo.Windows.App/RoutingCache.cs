using System.Collections.Concurrent;
using System.Net;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;

namespace AmneziaGeo.Windows.App;

/// <summary>
/// Classifies destinations against the routing lists and installs a bypass route for those that need one, reclaiming
/// it once idle. One entry per address carries both the verdict and what the verdict installed, so a repeat
/// destination costs a single dictionary hit.
/// </summary>
internal sealed class RoutingCache
{
    // Idle window before an entry is reclaimed. Long enough that a page still open keeps its route even when the OS
    // resolver cache stops the name from being re-queried.
    private const long IdleTtlMs = 15 * 60 * 1000;
    private const int ScanIntervalMs = 5_000;
    // Reclaim every twelfth scan, so an entry outlives its idle window by at most one scan.
    private const int ScansPerSweep = 12;
    // Reclaim in slices: dropping hundreds of filters at once is the storm this design exists to avoid.
    private const int SweepBatch = 64;
    // Entries holding system resources; past this an address follows the default route instead.
    private const int MaxApplied = 8192;
    // Entries overall. A verdict without resources costs a dictionary slot, so it gets a wider ceiling.
    private const int MaxEntries = 65536;
    private const int AfInet = 2;
    private const int TcpTableOwnerPidAll = 5;

    private sealed class Entry
    {
        public IPAddress Address = IPAddress.Any;
        public uint Numeric;
        public RouteVerdict Verdict;
        public bool Bypass;
        public long LastTouch;
        public bool Routed;
        public uint InterfaceIndex;
        public ulong FilterOut;
        public ulong FilterIn;
        public int Generation;
    }

    // Swapped as one immutable set, so a live rule edit never leaves a half-applied mix.
    private sealed record RuleSet(GeoIpRanges Proxy, GeoIpRanges Direct, GeoIpRanges Block);

    private readonly ConcurrentDictionary<uint, Entry> _entries = new();
    private readonly IRouteApplier _applier;
    private readonly bool _split;
    private readonly ILogger<RoutingCache> _logger;
    private RuleSet _rules;
    private int _size;
    private int _applied;
    private int _installed;
    private int _reclaimed;
    private int _capacityWarned;

    /// <summary>
    /// ctor
    /// </summary>
    public RoutingCache(IRouteApplier applier, bool split, IReadOnlyList<string> proxy, IReadOnlyList<string> direct, IReadOnlyList<string> block, ILogger<RoutingCache> logger)
    {
        _applier = applier;
        _split = split;
        _logger = logger;
        _rules = Build(proxy, direct, block);
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
    /// Records contact with a destination and installs its bypass route on first sight. Called per DNS answer, per
    /// connect event and per table scan.
    /// </summary>
    public void Note(uint address)
    {
        var now = Environment.TickCount64;
        if (_entries.TryGetValue(address, out var existing))
        {
            Volatile.Write(ref existing.LastTouch, now);
            if (!existing.Bypass || (existing.Routed && existing.Generation == _applier.Generation))
            {
                return;
            }

            Install(existing, now);
            return;
        }

        Admit(address, now);
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
    /// Reinstalls permits for live entries after the filter set was rebuilt; routes survive an arm, filters do not.
    /// </summary>
    public void Reinstall()
    {
        var generation = _applier.Generation;
        var refreshed = 0;
        foreach (var pair in _entries)
        {
            var entry = pair.Value;
            if (!entry.Bypass || entry.Generation == generation)
            {
                continue;
            }

            lock (entry)
            {
                if (entry.Generation == generation)
                {
                    continue;
                }

                if (_applier.TryPermit(pair.Key, out var outId, out var inId, out var installed))
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
            _logger.LogDebug("routing cache: reinstalled {Count} host permits after rearm", refreshed);
        }
    }

    /// <summary>
    /// Scans the connection table and reclaims idle entries until cancelled.
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
                    var active = ActiveRemotes();
                    foreach (var address in active)
                    {
                        Note(address);
                    }

                    if (++scans % ScansPerSweep == 0)
                    {
                        Sweep(active, Environment.TickCount64);
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
    /// Swaps the rule sets after a live list edit and drops everything applied under the old rules, so the next
    /// contact re-decides. Without this a rule change would never reach an address already classified.
    /// </summary>
    public void Rebuild(IReadOnlyList<string> proxy, IReadOnlyList<string> direct, IReadOnlyList<string> block)
    {
        var rules = Build(proxy, direct, block);
        Volatile.Write(ref _rules, rules);
        Drop();
        _logger.LogInformation("routing cache: rules rebuilt - {Direct} direct, {Block} block, {Proxy} proxy ranges",
            rules.Direct.Count, rules.Block.Count, rules.Proxy.Count);
    }

    /// <summary>
    /// Drops every installed route and filter.
    /// </summary>
    public void RemoveAll()
    {
        Drop();
        _logger.LogInformation("routing cache: {Installed} host routes installed, {Reclaimed} reclaimed this session",
            Volatile.Read(ref _installed), Volatile.Read(ref _reclaimed));
    }

    // Creates the entry for an address seen for the first time and applies what its verdict asks for.
    private void Admit(uint address, long now)
    {
        var rules = Volatile.Read(ref _rules);
        var verdict = Evaluate(rules, address);
        var entry = new Entry
        {
            Address = ToAddress(address),
            Numeric = address,
            Verdict = verdict,
            Bypass = NeedsBypass(verdict, rules, address),
            LastTouch = now,
        };

        if (Volatile.Read(ref _size) >= MaxEntries)
        {
            TrimUnapplied();
        }

        if (!_entries.TryAdd(address, entry))
        {
            if (_entries.TryGetValue(address, out var raced) && raced.Bypass)
            {
                Install(raced, now);
            }

            return;
        }

        Interlocked.Increment(ref _size);
        if (entry.Bypass)
        {
            Install(entry, now);
        }
    }

    private void Install(Entry entry, long now)
    {
        lock (entry)
        {
            Volatile.Write(ref entry.LastTouch, now);
            var generation = _applier.Generation;
            if (entry.Routed && entry.Generation == generation)
            {
                return;
            }

            if (!entry.Routed && Volatile.Read(ref _applied) >= MaxApplied)
            {
                if (Interlocked.Exchange(ref _capacityWarned, 1) == 0)
                {
                    _logger.LogWarning("routing cache: {Max} host routes in use; further addresses follow the default route", MaxApplied);
                }

                return;
            }

            // Permit first: until the route exists the traffic still rides the tunnel, whereas a route without a
            // permit hands the packets to a physical path the kill-switch drops.
            if (entry.Generation != generation && _applier.TryPermit(entry.Numeric, out var outId, out var inId, out var installed))
            {
                entry.FilterOut = outId;
                entry.FilterIn = inId;
                entry.Generation = installed;
            }

            if (entry.Routed)
            {
                return;
            }

            if (_applier.TryAddRoute(entry.Address, out var index))
            {
                entry.InterfaceIndex = index;
                entry.Routed = true;
                Interlocked.Increment(ref _applied);
                Interlocked.Increment(ref _installed);
            }
        }
    }

    // Reclaims idle entries. The busy set is the scan's own snapshot: a live connection must keep its route, because
    // moving its egress interface swaps the source address and the peer drops the flow. RunAsync owns the schedule,
    // this owns the decision.
    internal void Sweep(HashSet<uint> busy, long now)
    {
        var stale = new List<KeyValuePair<uint, Entry>>();
        foreach (var pair in _entries)
        {
            if (now - Volatile.Read(ref pair.Value.LastTouch) <= IdleTtlMs)
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
                Release(entry, generation, filters);
            }

            dropped++;
        }

        _applier.DeleteFilters(filters, generation);

        if (dropped > 0)
        {
            Interlocked.Add(ref _reclaimed, dropped);
            Volatile.Write(ref _capacityWarned, 0);
            _logger.LogDebug("routing cache: reclaimed {Dropped} idle entries, {Kept} still carrying traffic, {Active} routed",
                dropped, kept, Volatile.Read(ref _applied));
        }
    }

    // Drops verdict-only entries at capacity: they hold no system resources, so refilling one costs a binary search.
    private void TrimUnapplied()
    {
        var dropped = 0;
        foreach (var pair in _entries)
        {
            if (pair.Value.Bypass || pair.Value.Routed)
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
            _logger.LogDebug("routing cache: dropped {Count} verdict-only entries at capacity", dropped);
        }
    }

    private void Drop()
    {
        var filters = new List<(ulong Out, ulong In)>();
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
                Release(entry, generation, filters);
            }
        }

        _applier.DeleteFilters(filters, generation);
        Volatile.Write(ref _capacityWarned, 0);
    }

    // Removes the route now and queues the filter ids for the batched delete: the route goes first so the traffic
    // falls back to the default path before the permit disappears.
    private void Release(Entry entry, int generation, List<(ulong Out, ulong In)> filters)
    {
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

    // Only Direct installs anything here: Block is already dropped by WFP from connect, Proxy rides the set applied
    // at bring-up, and None follows the default route. In split the default is physical already, so a bypass is
    // needed only where a proxy range would otherwise pull the address into the tunnel.
    private bool NeedsBypass(RouteVerdict verdict, RuleSet rules, uint address)
    {
        if (verdict != RouteVerdict.Direct)
        {
            return false;
        }

        return !_split || rules.Proxy.Contains(address);
    }

    private static RuleSet Build(IReadOnlyList<string> proxy, IReadOnlyList<string> direct, IReadOnlyList<string> block)
    {
        return new RuleSet(GeoIpRanges.Build(proxy), GeoIpRanges.Build(direct), GeoIpRanges.Build(block));
    }

    private static IPAddress ToAddress(uint address)
    {
        return new IPAddress(new[] { (byte)(address >> 24), (byte)(address >> 16), (byte)(address >> 8), (byte)address });
    }

    // Remote addresses of every current TCP connection, host order. Feeds both the routing pass - which is how an
    // inbound connection and one already established at bring-up earn their route - and the reclaim pass.
    private static HashSet<uint> ActiveRemotes()
    {
        var remotes = new HashSet<uint>();
        var size = 0;
        GetExtendedTcpTable(IntPtr.Zero, ref size, false, AfInet, TcpTableOwnerPidAll, 0);
        if (size <= 0)
        {
            return remotes;
        }

        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            if (GetExtendedTcpTable(buffer, ref size, false, AfInet, TcpTableOwnerPidAll, 0) != 0)
            {
                return remotes;
            }

            var count = Marshal.ReadInt32(buffer);
            var basePtr = buffer + 4;
            for (var i = 0; i < count; i++)
            {
                // MIB_TCPROW_OWNER_PID: state, local addr, local port, remote addr at offset 12, remote port, pid.
                var addr = new byte[4];
                Marshal.Copy(basePtr + (i * 24) + 12, addr, 0, 4);
                var value = ((uint)addr[0] << 24) | ((uint)addr[1] << 16) | ((uint)addr[2] << 8) | addr[3];
                if (value != 0)
                {
                    remotes.Add(value);
                }
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }

        return remotes;
    }

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedTcpTable(IntPtr pTcpTable, ref int pdwSize, [MarshalAs(UnmanagedType.Bool)] bool bOrder, int ulAf, int tableClass, int reserved);
}
