using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Session;
using Microsoft.Extensions.Logging;

namespace AmneziaGeo.Windows.App;

/// <summary>
/// Routes matched apps' TCP and UDP destinations through the tunnel via the kernel-network ETW provider,
/// replacing per-connection table polling. TCP is always match-gated; UDP also supports an all-UDP catch-all.
/// </summary>
internal sealed class NetworkFlowTracker : IDisposable
{
    // Microsoft-Windows-Kernel-Network.
    private static readonly Guid KernelNetworkProvider = new("7DD42A49-5329-4832-8DFD-43D979153A88");
    // KERNEL_NETWORK_KEYWORD_IPV4 (0x10) / _IPV6 (0x20).
    private const ulong IPv4Keyword = 0x10UL;
    private const ulong IPv6Keyword = 0x20UL;
    // TCP connection-attempt ids: v4=12, v6=28. The routing decision belongs to the handshake - a transfer emits a
    // send event per segment and every one of them would repeat the same answer.
    private const int TcpV4ConnectId = 12;
    private const int TcpV6ConnectId = 28;
    // UDP send ids: v4=42, v6=58. UDP has no handshake, so its first datagram to an address is the trigger.
    private const int UdpV4SendId = 42;
    private const int UdpV6SendId = 58;
    // Payload (little-endian): PID(4) size(4) daddr saddr dport(2) sport(2); daddr is 4 bytes (v4) or 16 (v6).
    // Payload PID (offset 0) is more reliable than the ETW header PID; daddr at offset 8.
    private const int PidOffset = 0;
    private const int RemoteAddrOffset = 8;
    private const int MinV4PayloadBytes = RemoteAddrOffset + 4;
    private const int MinV6PayloadBytes = RemoteAddrOffset + 16;
    // Own process - its underlay and DNS-proxy UDP must never be tunneled (would loop).
    private static readonly uint OwnProcessId = (uint)Environment.ProcessId;

    private readonly AppMatcher? _matcher;
    private readonly DomainTracker? _tracker;
    private readonly bool _allUdp;
    private readonly bool _tunnelV6;
    private readonly IPAddress? _excludeEndpoint;
    // Network-order endpoint address, compared before any allocation on the per-packet path.
    private readonly uint _excludeEndpointV4;
    // Every v4 destination is offered here regardless of app rules, on connect and on a first datagram: it drives the
    // on-demand Direct routes for addresses that never went through the resolver.
    private readonly Action<uint, bool>? _noteV4;
    private readonly ILogger _logger;
    private TraceEventSession? _session;
    // Seen destinations; ETW handler is single-threaded, no lock needed.
    private readonly HashSet<uint> _seenUdp = [];
    private readonly HashSet<string> _seenUdpV6 = [];
    private readonly HashSet<uint> _seenTcpV4 = [];
    private readonly HashSet<string> _seenTcpV6 = [];
    // Released destinations, handed over from the eviction thread and applied on the thread that owns each set.
    private readonly ConcurrentQueue<string> _forgetFlow = new();
    private readonly ConcurrentQueue<string> _forgetScan = new();
    // Per-pid match decision with a short TTL. MatchesPid does a full process-tree snapshot, and a busy app
    // emits thousands of datagrams a second - caching the decision for ~1s removes the data plane's worst CPU
    // sink. Single-threaded handler, no lock.
    private const long PidCacheTtlMs = 1000;
    private readonly Dictionary<uint, (long Expiry, bool Match)> _pidMatch = [];

    // Proactive backstop for a SYN the packet filter dropped: polls the TCP table for half-open attempts and
    // decides their destinations, so the permit is there before the stack repeats the SYN a second later. Own thread.
    private const int ScanIntervalMs = 400;
    // Matching an app walks the whole process tree, so that half keeps the old cadence.
    private const int ScansPerAppMatch = 5;
    private const int AfInet = 2;
    private const int AfInet6 = 23;
    private const int TcpTableOwnerPidAll = 5;
    private const int MibTcpStateSynSent = 3;
    private readonly HashSet<string> _scanSeen = [];

    /// <summary>
    /// ctor
    /// </summary>
    public NetworkFlowTracker(AppMatcher? matcher, DomainTracker? tracker, bool allUdp, bool tunnelV6, IPAddress? excludeEndpoint, ILogger logger, Action<uint, bool>? noteV4 = null)
    {
        _matcher = matcher;
        _tracker = tracker;
        _allUdp = allUdp;
        _tunnelV6 = tunnelV6;
        _excludeEndpoint = excludeEndpoint;
        _excludeEndpointV4 = excludeEndpoint is not null && excludeEndpoint.AddressFamily == AddressFamily.InterNetwork
            ? BitConverter.ToUInt32(excludeEndpoint.GetAddressBytes(), 0)
            : 0;
        _noteV4 = noteV4;
        _logger = logger;
    }

    public async Task RunAsync(CancellationToken ct)
    {
        // Fixed name (a single agent is guaranteed by SoleAgentGuard): EnableProvider reclaims a session
        // orphaned by a prior crash instead of leaking a new one per PID.
        var sessionName = "AmneziaGeoFlow";
        try
        {
            using (_session = new TraceEventSession(sessionName, TraceEventSessionOptions.Create))
            {
                ct.Register(Stop);

                var keywords = _tunnelV6 ? IPv4Keyword | IPv6Keyword : IPv4Keyword;
                // EnableProvider true means it restarted a leftover session; do not gate on the result.
                if (EnableFiltered(keywords))
                {
                    _logger.LogDebug("a leftover monitoring session {Name} was found and restarted", sessionName);
                }

                _session.Source.AllEvents += evt => Handle(evt, ct);
                _logger.LogInformation("watching app connections (session {Name}, IPv6 {V6}); connections of tunneled apps are routed as they appear", sessionName, _tunnelV6);

                // Backstop scan for matched apps' SYN_SENT remotes; only meaningful with a TCP app matcher.
                if (_matcher is not null)
                {
                    _ = Task.Run(() => ScanLoopAsync(ct), CancellationToken.None);
                }

                // Source.Process() blocks until Stop().
                await Task.Run(() => _session.Source.Process(), CancellationToken.None).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "stopped watching app connections");
        }
    }

    // Subscribes to the connection-establishment events only, dropping the rest in the kernel. Falls back to the
    // unfiltered subscription where filtering is unavailable; the handler switch keeps the behaviour identical.
    private bool EnableFiltered(ulong keywords)
    {
        var ids = new List<int> { TcpV4ConnectId, UdpV4SendId };
        if (_tunnelV6)
        {
            ids.Add(TcpV6ConnectId);
            ids.Add(UdpV6SendId);
        }

        try
        {
            var options = new TraceEventProviderOptions { EventIDsToEnable = ids };
            return _session!.EnableProvider(KernelNetworkProvider, TraceEventLevel.Informational, keywords, options);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "this system cannot narrow the network events, so all of them are read - slightly more CPU, same behaviour");
            return _session!.EnableProvider(KernelNetworkProvider, TraceEventLevel.Informational, keywords);
        }
    }

    /// <summary>
    /// Forgets destinations whose routes were released, so the next packet to one installs them again instead of
    /// being skipped as already handled.
    /// </summary>
    public void Forget(IReadOnlyList<string> addresses)
    {
        // A queue nobody drains - no ETW events, no scan loop - is capped: a missed forget costs one repeat route,
        // an unbounded queue costs the process.
        Trim(_forgetFlow);
        foreach (var address in addresses)
        {
            _forgetFlow.Enqueue(address);
        }

        if (_matcher is null)
        {
            return;
        }

        Trim(_forgetScan);
        foreach (var address in addresses)
        {
            _forgetScan.Enqueue(address);
        }
    }

    private static void Trim(ConcurrentQueue<string> queue)
    {
        while (queue.Count >= 65536 && queue.TryDequeue(out _))
        {
        }
    }

    // Applies the queue on the ETW handler thread, which owns these sets.
    private void DrainFlowForgets()
    {
        while (_forgetFlow.TryDequeue(out var address))
        {
            _seenUdpV6.Remove(address);
            _seenTcpV6.Remove(address);
            if (IPAddress.TryParse(address, out var parsed) && parsed.AddressFamily == AddressFamily.InterNetwork)
            {
                var key = BitConverter.ToUInt32(parsed.GetAddressBytes(), 0);
                _seenUdp.Remove(key);
                _seenTcpV4.Remove(key);
            }
        }
    }

    private void Handle(TraceEvent evt, CancellationToken ct)
    {
        if (ct.IsCancellationRequested)
        {
            Stop();
            return;
        }

        if (!_forgetFlow.IsEmpty)
        {
            DrainFlowForgets();
        }

        switch ((int)evt.ID)
        {
            case UdpV4SendId:
                HandleUdpV4(evt);
                break;
            case UdpV6SendId:
                HandleUdpV6(evt);
                break;
            case TcpV4ConnectId:
                HandleTcpV4(evt);
                break;
            case TcpV6ConnectId:
                HandleTcpV6(evt);
                break;
        }
    }

    private void HandleUdpV4(TraceEvent evt)
    {
        try
        {
            var data = evt.EventData();
            if (data is null || data.Length < MinV4PayloadBytes)
            {
                return;
            }

            // Payload PID (offset 0), not the ETW header PID.
            var pid = BitConverter.ToUInt32(data, PidOffset);
            // Dedupe by raw daddr before any allocation.
            var daddr = BitConverter.ToUInt32(data, RemoteAddrOffset);
            NoteDestination(pid, daddr);

            if (_allUdp)
            {
                // Never tunnel own process: WG underlay and DNS-proxy upstream would loop.
                if (pid == OwnProcessId)
                {
                    return;
                }
            }
            else if (!MatchesPidCached(pid))
            {
                return;
            }

            if (_seenUdp.Contains(daddr))
            {
                return;
            }

            // daddr is network byte order; read bytes directly into IPAddress.
            var remoteIp = new IPAddress(new ReadOnlySpan<byte>(data, RemoteAddrOffset, 4).ToArray());

            // Skip the WG underlay endpoint to avoid a transport loop.
            if (_excludeEndpoint is not null && remoteIp.Equals(_excludeEndpoint))
            {
                MarkSeen(_seenUdp, daddr);
                return;
            }

            if (!IsTunnelableRemote(remoteIp))
            {
                MarkSeen(_seenUdp, daddr);
                return;
            }

            // Mark seen only after a successful route; failures retry on the next datagram. All-UDP tunnels every
            // destination and must not promote domains off anycast resolvers; an app match promotes.
            if (RouteUdp(remoteIp))
            {
                _logger.LogTrace("{Remote}: an app (pid {Pid}) is sending here, routed into the tunnel", remoteIp, pid);
                if (RouteLog.Enabled)
                {
                    RouteLog.Note($"udp request -> {remoteIp} (pid {pid})");
                }

                MarkSeen(_seenUdp, daddr);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "a UDP event could not be read and was skipped");
        }
    }

    private void HandleUdpV6(TraceEvent evt)
    {
        try
        {
            var data = evt.EventData();
            if (data is null || data.Length < MinV6PayloadBytes)
            {
                return;
            }

            var pid = BitConverter.ToUInt32(data, PidOffset);
            if (_allUdp)
            {
                // Never tunnel own process: WG underlay and DNS-proxy upstream would loop.
                if (pid == OwnProcessId)
                {
                    return;
                }
            }
            else if (!MatchesPidCached(pid))
            {
                return;
            }

            var remoteIp = new IPAddress(new ReadOnlySpan<byte>(data, RemoteAddrOffset, 16).ToArray());
            var key = remoteIp.ToString();
            if (_seenUdpV6.Contains(key))
            {
                return;
            }

            // Skip the WG underlay endpoint to avoid a transport loop.
            if (_excludeEndpoint is not null && remoteIp.Equals(_excludeEndpoint))
            {
                MarkSeen(_seenUdpV6, key);
                return;
            }

            if (!IsTunnelableRemote(remoteIp))
            {
                MarkSeen(_seenUdpV6, key);
                return;
            }

            // Mark seen only after a successful route; failures retry on the next datagram.
            if (RouteUdp(remoteIp))
            {
                _logger.LogTrace("{Remote}: an app (pid {Pid}) is sending here, routed into the tunnel", remoteIp, pid);
                if (RouteLog.Enabled)
                {
                    RouteLog.Note($"udp request -> {remoteIp} (pid {pid})");
                }

                MarkSeen(_seenUdpV6, key);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "a UDP event could not be read and was skipped");
        }
    }

    private void HandleTcpV4(TraceEvent evt)
    {
        try
        {
            var data = evt.EventData();
            if (data is null || data.Length < MinV4PayloadBytes)
            {
                return;
            }

            var pid = BitConverter.ToUInt32(data, PidOffset);
            // Dedupe by raw daddr before any allocation.
            var daddr = BitConverter.ToUInt32(data, RemoteAddrOffset);
            NoteDestination(pid, daddr);

            // Tunnel steering is always match-gated; without an app matcher there is nothing to steer.
            if (_matcher is null || !MatchesPidCached(pid))
            {
                return;
            }

            if (_seenTcpV4.Contains(daddr))
            {
                return;
            }

            var remoteIp = new IPAddress(new ReadOnlySpan<byte>(data, RemoteAddrOffset, 4).ToArray());
            if (_excludeEndpoint is not null && remoteIp.Equals(_excludeEndpoint))
            {
                MarkSeen(_seenTcpV4, daddr);
                return;
            }

            if (!IsTunnelableRemote(remoteIp))
            {
                MarkSeen(_seenTcpV4, daddr);
                return;
            }

            if (RouteMatched(remoteIp, pid))
            {
                MarkSeen(_seenTcpV4, daddr);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "a TCP event could not be read and was skipped");
        }
    }

    private void HandleTcpV6(TraceEvent evt)
    {
        if (_matcher is null)
        {
            return;
        }

        try
        {
            var data = evt.EventData();
            if (data is null || data.Length < MinV6PayloadBytes)
            {
                return;
            }

            var pid = BitConverter.ToUInt32(data, PidOffset);
            if (!MatchesPidCached(pid))
            {
                return;
            }

            var remoteIp = new IPAddress(new ReadOnlySpan<byte>(data, RemoteAddrOffset, 16).ToArray());
            var key = remoteIp.ToString();
            if (_seenTcpV6.Contains(key))
            {
                return;
            }

            if (_excludeEndpoint is not null && remoteIp.Equals(_excludeEndpoint))
            {
                MarkSeen(_seenTcpV6, key);
                return;
            }

            if (!IsTunnelableRemote(remoteIp))
            {
                MarkSeen(_seenTcpV6, key);
                return;
            }

            if (RouteMatched(remoteIp, pid))
            {
                MarkSeen(_seenTcpV6, key);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "a TCP event could not be read and was skipped");
        }
    }

    // Offers a v4 destination to the on-demand router, ahead of the app-match gate but never blind to it: a
    // destination handed over as ordinary traffic is settled onto the physical path, and that decision outlives
    // every later attempt of the app that opened it.
    private void NoteDestination(uint pid, uint networkOrderAddress)
    {
        if (_noteV4 is null || pid == OwnProcessId || networkOrderAddress == _excludeEndpointV4)
        {
            return;
        }

        _noteV4(BinaryPrimitives.ReverseEndianness(networkOrderAddress), MatchesPidCached(pid));
    }

    // Routes a UDP destination: all-UDP routes it plainly, an app match also promotes its domain.
    private bool RouteUdp(IPAddress remoteIp)
    {
        if (_tracker is null)
        {
            return false;
        }

        var key = remoteIp.ToString();
        return _allUdp ? _tracker.UpdateAppIps([key]) : _tracker.NoteAppRemotes([key]);
    }

    // Routes a matched app's TCP remote and promotes the domain(s) it resolved to; true when routed.
    private bool RouteMatched(IPAddress remoteIp, uint pid)
    {
        if (_tracker is null || !_tracker.NoteAppRemotes([remoteIp.ToString()]))
        {
            return false;
        }

        _logger.LogTrace("{Remote}: an app (pid {Pid}) is connecting, routed into the tunnel", remoteIp, pid);
        if (RouteLog.Enabled)
        {
            RouteLog.Note($"tcp request -> {remoteIp} (pid {pid})");
        }

        return true;
    }

    // Polls the TCP table for matched apps' SYN_SENT remotes and routes them, covering a destination whose connect
    // event was missed. Own task; ends when ct cancels.
    private async Task ScanLoopAsync(CancellationToken ct)
    {
        try
        {
            var scans = 0;
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(ScanIntervalMs, ct).ConfigureAwait(false);
                try
                {
                    ScanConnections(++scans % ScansPerAppMatch == 0);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "the sweep over open connections failed this round");
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void ScanConnections(bool matchApps)
    {
        while (_forgetScan.TryDequeue(out var address))
        {
            _scanSeen.Remove(address);
        }

        if (_matcher is null && _noteV4 is null)
        {
            return;
        }

        var candidates = new List<(uint Pid, IPAddress Remote)>();
        var pids = new HashSet<uint>();
        CollectSynSent(AfInet, candidates, pids);
        if (_tunnelV6)
        {
            CollectSynSent(AfInet6, candidates, pids);
        }

        if (candidates.Count == 0)
        {
            return;
        }

        // A dropped SYN raises no connect event, so the half-open attempt itself is what offers the destination.
        foreach (var (pid, remote) in candidates)
        {
            if (remote.AddressFamily == AddressFamily.InterNetwork)
            {
                NoteDestination(pid, BitConverter.ToUInt32(remote.GetAddressBytes(), 0));
            }
        }

        if (!matchApps || _matcher is null)
        {
            return;
        }

        var matched = _matcher.MatchPids(pids);
        if (matched.Count == 0)
        {
            return;
        }

        var batch = new List<string>();
        var picked = new HashSet<string>();
        foreach (var (pid, remote) in candidates)
        {
            var key = remote.ToString();
            if (matched.Contains(pid) && picked.Add(key))
            {
                batch.Add(key);
            }
        }

        if (batch.Count == 0)
        {
            return;
        }

        // Mark seen only on a successful route; a failed add retries on the next scan. Promotes like the ETW path:
        // a SYN caught here is the app's first contact with the domain, and its remaining addresses must follow.
        if (_tracker is not null && _tracker.NoteAppRemotes(batch))
        {
            foreach (var ip in batch)
            {
                _scanSeen.Add(ip);
                _logger.LogTrace("{Remote}: a tunneled app was found waiting to connect here, routed into the tunnel", ip);
                if (RouteLog.Enabled)
                {
                    RouteLog.Note($"tcp scan -> {ip} (matched app, syn-sent)");
                }
            }
        }
    }

    // Reads the OWNER_PID TCP table and appends tunnelable SYN_SENT remotes not already routed by a prior scan.
    private void CollectSynSent(int af, List<(uint Pid, IPAddress Remote)> candidates, HashSet<uint> pids)
    {
        var size = 0;
        GetExtendedTcpTable(IntPtr.Zero, ref size, false, af, TcpTableOwnerPidAll, 0);
        if (size <= 0)
        {
            return;
        }

        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            if (GetExtendedTcpTable(buffer, ref size, false, af, TcpTableOwnerPidAll, 0) != 0)
            {
                return;
            }

            var count = Marshal.ReadInt32(buffer);
            var isV6 = af == AfInet6;
            var rowSize = isV6 ? 56 : 24;
            var stateOffset = isV6 ? 48 : 0;
            var addrOffset = isV6 ? 24 : 12;
            var addrLen = isV6 ? 16 : 4;
            var pidOffset = isV6 ? 52 : 20;
            var basePtr = buffer + 4;
            for (var i = 0; i < count; i++)
            {
                var row = basePtr + (i * rowSize);
                if (Marshal.ReadInt32(row, stateOffset) != MibTcpStateSynSent)
                {
                    continue;
                }

                var addr = new byte[addrLen];
                Marshal.Copy(row + addrOffset, addr, 0, addrLen);
                AddCandidate(new IPAddress(addr), (uint)Marshal.ReadInt32(row, pidOffset), candidates, pids);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private void AddCandidate(IPAddress remote, uint pid, List<(uint Pid, IPAddress Remote)> candidates, HashSet<uint> pids)
    {
        if (pid == 0 || pid == OwnProcessId)
        {
            return;
        }

        if (_excludeEndpoint is not null && remote.Equals(_excludeEndpoint))
        {
            return;
        }

        if (!IsTunnelableRemote(remote) || _scanSeen.Contains(remote.ToString()))
        {
            return;
        }

        candidates.Add((pid, remote));
        pids.Add(pid);
    }

    // Cached per-pid app match; recomputes at most every PidCacheTtlMs so repeated events skip the snapshot.
    private bool MatchesPidCached(uint pid)
    {
        var now = Environment.TickCount64;
        if (_pidMatch.TryGetValue(pid, out var entry) && entry.Expiry > now)
        {
            return entry.Match;
        }

        var match = _matcher is not null && _matcher.MatchesPid(pid);
        if (_pidMatch.Count >= 4096)
        {
            _pidMatch.Clear();
        }

        _pidMatch[pid] = (now + PidCacheTtlMs, match);
        return match;
    }

    // Keep only public routable remotes.
    private static bool IsTunnelableRemote(IPAddress addr)
    {
        if (addr.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = addr.GetAddressBytes(); // network order: b[0] is the high octet
            return b[0] switch
            {
                0 => false,                                  // 0.0.0.0/8   "this network"
                10 => false,                                 // 10.0.0.0/8  private
                127 => false,                                // 127.0.0.0/8 loopback
                100 when b[1] is >= 64 and <= 127 => false,  // 100.64.0.0/10 CGNAT
                169 when b[1] == 254 => false,               // 169.254.0.0/16 link-local
                172 when b[1] is >= 16 and <= 31 => false,   // 172.16.0.0/12 private
                192 when b[1] == 168 => false,               // 192.168.0.0/16 private
                >= 224 => false,                             // 224.0.0.0/4 multicast + 240/4 reserved + 255.255.255.255
                _ => true,
            };
        }

        // IPv6: skip loopback (::1), unspecified (::), link-local (fe80::/10), ULA (fc00::/7), multicast (ff00::/8).
        if (IPAddress.IsLoopback(addr) || addr.Equals(IPAddress.IPv6Any))
        {
            return false;
        }

        var v6 = addr.GetAddressBytes();
        if (v6[0] == 0xff)
        {
            return false; // multicast ff00::/8
        }

        if (v6[0] == 0xfe && (v6[1] & 0xc0) == 0x80)
        {
            return false; // link-local fe80::/10
        }

        if ((v6[0] & 0xfe) == 0xfc)
        {
            return false; // ULA fc00::/7
        }

        return true;
    }

    // Marks a v4 destination handled; clears on overflow to bound the set.
    private static void MarkSeen(HashSet<uint> set, uint key)
    {
        if (set.Count >= 65536)
        {
            set.Clear();
        }

        set.Add(key);
    }

    // Marks a v6 destination handled; clears on overflow to bound the set.
    private static void MarkSeen(HashSet<string> set, string key)
    {
        if (set.Count >= 65536)
        {
            set.Clear();
        }

        set.Add(key);
    }

    private void Stop()
    {
        try
        {
            _session?.Stop();
        }
        catch
        {
        }
    }

    public void Dispose()
    {
        _session?.Dispose();
    }

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedTcpTable(IntPtr pTcpTable, ref int pdwSize, [MarshalAs(UnmanagedType.Bool)] bool bOrder, int ulAf, int tableClass, int reserved);
}
