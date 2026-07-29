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
    // Microsoft-Windows-Kernel-Network - per-segment/datagram network events.
    private static readonly Guid KernelNetworkProvider = new("7DD42A49-5329-4832-8DFD-43D979153A88");
    // KERNEL_NETWORK_KEYWORD_IPV4 (0x10) / _IPV6 (0x20).
    private const ulong IPv4Keyword = 0x10UL;
    private const ulong IPv6Keyword = 0x20UL;
    // Send-event ids: TCPv4=10, TCPv6=26, UDPv4=42, UDPv6=58.
    private const int TcpV4SendId = 10;
    private const int TcpV6SendId = 26;
    private const int UdpV4SendId = 42;
    private const int UdpV6SendId = 58;
    // Send payload (little-endian): PID(4) size(4) daddr saddr dport(2) sport(2); daddr is 4 bytes (v4) or 16 (v6).
    // Payload PID (offset 0) is more reliable than the ETW header PID; daddr at offset 8.
    private const int PidOffset = 0;
    private const int RemoteAddrOffset = 8;
    private const int MinV4PayloadBytes = RemoteAddrOffset + 4;
    private const int MinV6PayloadBytes = RemoteAddrOffset + 16;
    // Own process - its underlay and DNS-proxy UDP must never be tunneled (would loop).
    private static readonly uint OwnProcessId = (uint)Environment.ProcessId;

    private readonly AppMatcher? _matcher;
    private readonly DomainTracker _tracker;
    private readonly bool _allUdp;
    private readonly bool _tunnelV6;
    private readonly IPAddress? _excludeEndpoint;
    private readonly ILogger _logger;
    private TraceEventSession? _session;
    // Seen destinations; ETW handler is single-threaded, no lock needed.
    private readonly HashSet<uint> _seenUdp = [];
    private readonly HashSet<string> _seenUdpV6 = [];
    private readonly HashSet<uint> _seenTcpV4 = [];
    private readonly HashSet<string> _seenTcpV6 = [];
    // Per-pid match decision with a short TTL. MatchesPid does a full process-tree snapshot, and a busy app
    // emits thousands of segments/datagrams a second - caching the decision for ~1s removes the data plane's
    // worst CPU sink. Single-threaded handler, no lock.
    private const long PidCacheTtlMs = 1000;
    private readonly Dictionary<uint, (long Expiry, bool Match)> _pidMatch = [];

    // Proactive backstop: a matched app's SYN to a direct-blocked, DNS-less destination (Telegram MTProto DC)
    // never yields an ETW send event, so poll the TCP table for its SYN_SENT remotes and route them. Own thread.
    private const int ScanIntervalMs = 2000;
    private const int AfInet = 2;
    private const int AfInet6 = 23;
    private const int TcpTableOwnerPidAll = 5;
    private const int MibTcpStateSynSent = 3;
    private readonly HashSet<string> _scanSeen = [];

    /// <summary>
    /// ctor
    /// </summary>
    public NetworkFlowTracker(AppMatcher? matcher, DomainTracker tracker, bool allUdp, bool tunnelV6, IPAddress? excludeEndpoint, ILogger logger)
    {
        _matcher = matcher;
        _tracker = tracker;
        _allUdp = allUdp;
        _tunnelV6 = tunnelV6;
        _excludeEndpoint = excludeEndpoint;
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
                if (_session.EnableProvider(KernelNetworkProvider, TraceEventLevel.Informational, keywords))
                {
                    _logger.LogDebug("NetworkFlowTracker: restarted a pre-existing ETW session {Name}", sessionName);
                }

                _session.Source.AllEvents += evt => Handle(evt, ct);
                _logger.LogInformation("NetworkFlowTracker: ETW session {Name} started (v6={V6})", sessionName, _tunnelV6);

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
            _logger.LogDebug(ex, "NetworkFlowTracker: session ended");
        }
    }

    private void Handle(TraceEvent evt, CancellationToken ct)
    {
        if (ct.IsCancellationRequested)
        {
            Stop();
            return;
        }

        switch ((int)evt.ID)
        {
            case UdpV4SendId:
                HandleUdpV4(evt);
                break;
            case UdpV6SendId:
                HandleUdpV6(evt);
                break;
            case TcpV4SendId:
                HandleTcpV4(evt);
                break;
            case TcpV6SendId:
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

            // Dedupe by raw daddr before any allocation.
            var daddr = BitConverter.ToUInt32(data, RemoteAddrOffset);
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
                _logger.LogTrace("udp request -> {Remote} (pid {Pid})", remoteIp, pid);
                if (RouteLog.Enabled)
                {
                    RouteLog.Note($"udp request -> {remoteIp} (pid {pid})");
                }

                MarkSeen(_seenUdp, daddr);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "NetworkFlowTracker: udp parse error");
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
                _logger.LogTrace("udp request -> {Remote} (pid {Pid})", remoteIp, pid);
                if (RouteLog.Enabled)
                {
                    RouteLog.Note($"udp request -> {remoteIp} (pid {pid})");
                }

                MarkSeen(_seenUdpV6, key);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "NetworkFlowTracker: udp parse error");
        }
    }

    private void HandleTcpV4(TraceEvent evt)
    {
        // TCP is always match-gated; without an app matcher there is nothing to steer.
        if (_matcher is null)
        {
            return;
        }

        try
        {
            var data = evt.EventData();
            if (data is null || data.Length < MinV4PayloadBytes)
            {
                return;
            }

            var pid = BitConverter.ToUInt32(data, PidOffset);
            if (!MatchesPidCached(pid))
            {
                return;
            }

            // Dedupe by raw daddr before any allocation.
            var daddr = BitConverter.ToUInt32(data, RemoteAddrOffset);
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
            _logger.LogDebug(ex, "NetworkFlowTracker: tcp parse error");
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
            _logger.LogDebug(ex, "NetworkFlowTracker: tcp parse error");
        }
    }

    // Routes a UDP destination: all-UDP routes it plainly, an app match also promotes its domain.
    private bool RouteUdp(IPAddress remoteIp)
    {
        var key = remoteIp.ToString();
        return _allUdp ? _tracker.UpdateAppIps([key]) : _tracker.NoteAppRemotes([key]);
    }

    // Routes a matched app's TCP remote and promotes the domain(s) it resolved to; true when routed.
    private bool RouteMatched(IPAddress remoteIp, uint pid)
    {
        if (!_tracker.NoteAppRemotes([remoteIp.ToString()]))
        {
            return false;
        }

        _logger.LogTrace("tcp request -> {Remote} (pid {Pid})", remoteIp, pid);
        if (RouteLog.Enabled)
        {
            RouteLog.Note($"tcp request -> {remoteIp} (pid {pid})");
        }

        return true;
    }

    // Polls the TCP table for matched apps' SYN_SENT remotes and routes them, covering destinations that never
    // emit a send event because their handshake is blocked on the direct path. Own task; ends when ct cancels.
    private async Task ScanLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(ScanIntervalMs, ct).ConfigureAwait(false);
                try
                {
                    ScanConnections();
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "NetworkFlowTracker: connection scan error");
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void ScanConnections()
    {
        if (_matcher is null)
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
        if (_tracker.NoteAppRemotes(batch))
        {
            foreach (var ip in batch)
            {
                _scanSeen.Add(ip);
                _logger.LogTrace("tcp scan -> {Remote} (matched app, syn-sent)", ip);
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
