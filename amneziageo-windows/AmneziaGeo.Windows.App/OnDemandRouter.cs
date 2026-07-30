using System.Collections.Concurrent;
using System.Net;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;

namespace AmneziaGeo.Windows.App;

/// <summary>
/// Installs Direct host routes on first contact and reclaims them once idle, so a country-sized rule costs only the
/// addresses actually used instead of its whole prefix list.
/// </summary>
internal sealed class OnDemandRouter(
    VerdictResolver resolver,
    RouteManager routes,
    WindowsFirewall firewall,
    Func<(IPAddress? Gateway, uint InterfaceIndex)> hopProvider,
    bool killSwitch,
    ILogger<OnDemandRouter> logger)
{
    // Idle window before a host route is reclaimed. Long enough that a page still open keeps its route even when
    // the OS resolver cache stops the name from being re-queried.
    private const long IdleTtlMs = 15 * 60 * 1000;
    private const int ScanIntervalMs = 5_000;
    // Reclaim every twelfth scan, so a route outlives its idle window by at most one scan.
    private const int ScansPerSweep = 12;
    // Reclaim in slices: dropping hundreds of filters at once is the storm this design exists to avoid.
    private const int SweepBatch = 64;
    // Backstop against a pathological session; past this the address rides the tunnel instead.
    private const int MaxEntries = 8192;
    private const long HopTtlMs = 30_000;
    private const int AfInet = 2;
    private const int TcpTableOwnerPidAll = 5;

    private sealed class Entry
    {
        public IPAddress Address = IPAddress.Any;
        public long LastTouch;
        public ulong FilterOut;
        public ulong FilterIn;
        public int Generation;
        public uint InterfaceIndex;
        public bool Routed;
    }

    private sealed record Hop(IPAddress? Gateway, uint InterfaceIndex, long Stamp);

    private readonly ConcurrentDictionary<uint, Entry> _applied = new();
    private Hop? _hop;
    private int _count;
    private int _installed;
    private int _reclaimed;
    private int _capacityWarned;

    /// <summary>
    /// Host routes currently installed.
    /// </summary>
    public int Active => Volatile.Read(ref _count);

    /// <summary>
    /// Installs or refreshes the Direct route for a destination, and marks it in use. Called per DNS answer, per
    /// connect event and per table scan, so the fast path is a single dictionary hit.
    /// </summary>
    public void Note(uint address)
    {
        var now = Environment.TickCount64;
        if (_applied.TryGetValue(address, out var existing))
        {
            Volatile.Write(ref existing.LastTouch, now);
            if (existing.Routed && !NeedsFilterRefresh(existing))
            {
                return;
            }
        }
        else if (resolver.Classify(address) != RouteVerdict.Direct)
        {
            return;
        }

        Install(address, now);
    }

    /// <summary>
    /// Installs or refreshes the Direct route for an address; non-IPv4 is ignored.
    /// </summary>
    public void Note(IPAddress address)
    {
        if (GeoIpRanges.TryToNumeric(address, out var value))
        {
            Note(value);
        }
    }

    /// <summary>
    /// Reinstalls filters for live entries after the filter set was rebuilt; routes survive an arm, filters do not.
    /// </summary>
    public void Reinstall()
    {
        if (!killSwitch)
        {
            return;
        }

        var generation = firewall.Generation;
        var refreshed = 0;
        foreach (var pair in _applied)
        {
            var entry = pair.Value;
            if (entry.Generation == generation)
            {
                continue;
            }

            lock (entry)
            {
                if (entry.Generation == generation)
                {
                    continue;
                }

                if (firewall.TryPermitHost(pair.Key, out var outId, out var inId, out var installedGeneration))
                {
                    entry.FilterOut = outId;
                    entry.FilterIn = inId;
                    entry.Generation = installedGeneration;
                    refreshed++;
                }
            }
        }

        if (refreshed > 0)
        {
            logger.LogDebug("on-demand: reinstalled {Count} host permits after rearm", refreshed);
        }
    }

    /// <summary>
    /// Scans the connection table and reclaims idle routes until cancelled.
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
                        Sweep(active);
                    }
                }
                catch (Exception ex)
                {
                    logger.LogDebug(ex, "on-demand: scan failed");
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>
    /// Drops every installed route and filter.
    /// </summary>
    public void RemoveAll()
    {
        var filters = new List<(ulong Out, ulong In)>();
        var generation = firewall.Generation;
        foreach (var address in _applied.Keys)
        {
            if (!_applied.TryRemove(address, out var entry))
            {
                continue;
            }

            Interlocked.Decrement(ref _count);
            lock (entry)
            {
                Release(entry, generation, filters);
            }
        }

        if (filters.Count > 0)
        {
            firewall.DeleteHostFilters(filters, generation);
        }

        logger.LogInformation("on-demand: {Installed} host routes installed, {Reclaimed} reclaimed this session", Volatile.Read(ref _installed), Volatile.Read(ref _reclaimed));
    }

    // A rebuilt filter set voided the permit even though the route is still there.
    private bool NeedsFilterRefresh(Entry entry)
    {
        return killSwitch && entry.Generation != firewall.Generation;
    }

    private void Install(uint address, long now)
    {
        if (!_applied.TryGetValue(address, out var entry))
        {
            if (Volatile.Read(ref _count) >= MaxEntries)
            {
                if (Interlocked.Exchange(ref _capacityWarned, 1) == 0)
                {
                    logger.LogWarning("on-demand: {Max} host routes in use; further Direct addresses ride the tunnel", MaxEntries);
                }

                return;
            }

            entry = new Entry { Address = ToAddress(address), LastTouch = now };
            if (_applied.TryAdd(address, entry))
            {
                Interlocked.Increment(ref _count);
            }
            else if (!_applied.TryGetValue(address, out entry))
            {
                return;
            }
        }

        lock (entry)
        {
            Volatile.Write(ref entry.LastTouch, now);
            var generation = firewall.Generation;
            if (entry.Routed && (!killSwitch || entry.Generation == generation))
            {
                return;
            }

            // Permit first: until the route exists the traffic still rides the tunnel, whereas a route without a
            // permit hands the packets to a physical path the kill-switch drops.
            if (killSwitch && entry.Generation != generation)
            {
                if (firewall.TryPermitHost(address, out var outId, out var inId, out var installedGeneration))
                {
                    entry.FilterOut = outId;
                    entry.FilterIn = inId;
                    entry.Generation = installedGeneration;
                }
            }

            if (entry.Routed)
            {
                return;
            }

            var hop = ResolveHop();
            if (hop.InterfaceIndex == 0)
            {
                return;
            }

            if (routes.AddDirectHost(entry.Address, hop.Gateway, hop.InterfaceIndex))
            {
                entry.InterfaceIndex = hop.InterfaceIndex;
                entry.Routed = true;
                Interlocked.Increment(ref _installed);
            }
        }
    }

    // Reclaims idle entries. The busy set is the scan's own snapshot: a live connection must keep its route, because
    // moving its egress interface swaps the source address and the peer drops the flow.
    private void Sweep(HashSet<uint> busy)
    {
        var now = Environment.TickCount64;
        var stale = new List<KeyValuePair<uint, Entry>>();
        foreach (var pair in _applied)
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
        var generation = firewall.Generation;
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

            if (!_applied.TryRemove(address, out _))
            {
                continue;
            }

            Interlocked.Decrement(ref _count);
            lock (entry)
            {
                Release(entry, generation, filters);
            }

            dropped++;
        }

        if (filters.Count > 0)
        {
            firewall.DeleteHostFilters(filters, generation);
        }

        if (dropped > 0)
        {
            Interlocked.Add(ref _reclaimed, dropped);
            Volatile.Write(ref _capacityWarned, 0);
            logger.LogDebug("on-demand: reclaimed {Dropped} idle host routes, {Kept} still carrying traffic, {Active} active", dropped, kept, Volatile.Read(ref _count));
        }
    }

    // Removes the route now and queues the filter ids for the batched delete: the route goes first so the traffic
    // falls back to the tunnel before the permit disappears.
    private void Release(Entry entry, int generation, List<(ulong Out, ulong In)> filters)
    {
        if (entry.Routed)
        {
            routes.RemoveDirectHost(entry.Address, entry.InterfaceIndex);
            entry.Routed = false;
        }

        if (entry.Generation == generation && (entry.FilterOut != 0 || entry.FilterIn != 0))
        {
            filters.Add((entry.FilterOut, entry.FilterIn));
        }

        entry.FilterOut = 0;
        entry.FilterIn = 0;
    }

    private Hop ResolveHop()
    {
        var now = Environment.TickCount64;
        var cached = _hop;
        if (cached is not null && cached.InterfaceIndex != 0 && now - cached.Stamp < HopTtlMs)
        {
            return cached;
        }

        var (gateway, interfaceIndex) = hopProvider();
        var fresh = new Hop(gateway, interfaceIndex, now);
        if (interfaceIndex != 0)
        {
            _hop = fresh;
        }

        return fresh;
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
