using System.Net;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Session;
using Microsoft.Extensions.Logging;

namespace AmneziaGeo.Windows.App;

/// <summary>
/// UDP complement to <see cref="AppRouteWatcher"/>: subscribes to the kernel-network ETW provider and
/// routes remote IPs of UDP datagrams sent by matched apps through the tunnel. The TCP-based watcher
/// cannot see UDP flows, so real-time media apps (voice calls, online games) whose servers deliver IPs through
/// an application-layer signaling channel (not DNS) need this path to land UDP traffic in the tunnel.
/// </summary>
internal sealed class UdpFlowTracker : IDisposable
{
    // Microsoft-Windows-Kernel-Network - per-datagram network events.
    private static readonly Guid KernelNetworkProvider = new("7DD42A49-5329-4832-8DFD-43D979153A88");
    // KERNEL_NETWORK_KEYWORD_IPV4 (0x10): IPv4 TCP and UDP datagram events from this provider.
    private const ulong IPv4Keyword = 0x10UL;
    // EventId 42 = KNetEvt_SendIPV4Udp (UDPv4 datagram sent). NB: id 10 is TCPv4 send, which the TCP watcher
    // already covers - real-time media (voice calls) is UDP, so the UDP send event (42) is the one we want.
    private const int UdpV4SendId = 42;
    // KNetEvt_SendIPV4Udp payload (little-endian): PID(4) size(4) daddr(4) saddr(4) dport(2) sport(2) ...
    // The owning process is an explicit payload field at offset 0 - more reliable than the ETW header PID,
    // which this provider can log under a system worker thread. daddr (the remote/destination IPv4) is at 8.
    private const int PidOffset = 0;
    private const int RemoteAddrOffset = 8;
    private const int MinPayloadBytes = RemoteAddrOffset + 4;
    // Our own service process - the in-process WG engine's underlay UDP (and the loopback DNS proxy's
    // upstream queries) come from here and must never be tunneled in all-UDP mode (would loop the transport).
    private static readonly uint OwnProcessId = (uint)Environment.ProcessId;

    private readonly AppRouteWatcher? _watcher;
    private readonly DomainTracker _tracker;
    private readonly bool _allUdp;
    private readonly IPAddress? _excludeEndpoint;
    private readonly ILogger _logger;
    private TraceEventSession? _session;
    // Destinations already routed (or already found non-tunnelable) this session, keyed by the raw 4-byte
    // daddr. Handle runs on a single ETW processing thread, so this needs no lock; it lets the common
    // repeat-datagram case skip the per-packet IPAddress/string allocation and the shared tracker lock.
    private readonly HashSet<uint> _seen = [];

    /// <param name="watcher">Per-app PID matcher; may be null in all-UDP mode (the PID gate is bypassed).</param>
    /// <param name="allUdp">When true, tunnel EVERY process's UDP destinations, not just matched apps' (#77-udp).</param>
    /// <param name="excludeEndpoint">The tunnel's own underlay server IP, never tunneled (avoids a transport loop).</param>
    public UdpFlowTracker(AppRouteWatcher? watcher, DomainTracker tracker, bool allUdp, IPAddress? excludeEndpoint, ILogger logger)
    {
        _watcher = watcher;
        _tracker = tracker;
        _allUdp = allUdp;
        _excludeEndpoint = excludeEndpoint;
        _logger = logger;
    }

    public async Task RunAsync(CancellationToken ct)
    {
        var sessionName = $"AmneziaGeoUdp_{Environment.ProcessId}";
        try
        {
            using (_session = new TraceEventSession(sessionName, TraceEventSessionOptions.Create))
            {
                ct.Register(Stop);

                // EnableProvider returns true only when it RESTARTED a pre-existing session; false is the
                // normal first-enable path, and a genuine failure throws (caught below). Treating false as
                // "unavailable" disabled the tracker on every clean start - so do not gate on the result.
                if (_session.EnableProvider(KernelNetworkProvider, TraceEventLevel.Informational, IPv4Keyword))
                {
                    _logger.LogDebug("UdpFlowTracker: restarted a pre-existing ETW session {Name}", sessionName);
                }

                _session.Source.AllEvents += evt => Handle(evt, ct);
                _logger.LogInformation("UdpFlowTracker: ETW session {Name} started", sessionName);

                // Source.Process() blocks the thread until Stop() is called.
                await Task.Run(() => _session.Source.Process(), CancellationToken.None).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "UdpFlowTracker: session ended");
        }
    }

    private void Handle(TraceEvent evt, CancellationToken ct)
    {
        if (ct.IsCancellationRequested)
        {
            Stop();
            return;
        }

        if ((int)evt.ID != UdpV4SendId)
        {
            return;
        }

        try
        {
            var data = evt.EventData();
            if (data is null || data.Length < MinPayloadBytes)
            {
                return;
            }

            // Owning process is the payload PID field (offset 0), not the ETW header PID. In all-UDP mode
            // every process's UDP is tunneled EXCEPT our own; otherwise only matched apps' (by PID).
            var pid = BitConverter.ToUInt32(data, PidOffset);
            if (_allUdp)
            {
                // Never tunnel our OWN process's UDP. The in-process WG engine sends the plain-UDP underlay
                // to the server; pinning that to the tunnel it carries is a fatal loop - and keying on the PID
                // (not the endpoint IP) is robust against a round-robin endpoint that resolves to several IPs.
                // The loopback DNS proxy's upstream queries run from here too and need no tunneling.
                if (pid == OwnProcessId)
                {
                    return;
                }
            }
            else if (_watcher is null || !_watcher.MatchesPid(pid))
            {
                return;
            }

            // Cheap per-packet dedupe BEFORE any allocation or the shared tracker lock: the raw 4 bytes as
            // a hash key (endianness is irrelevant for a key). A destination already routed - or already
            // rejected below - is the overwhelmingly common case under a live call and is skipped here.
            var daddr = BitConverter.ToUInt32(data, RemoteAddrOffset);
            if (_seen.Contains(daddr))
            {
                return;
            }

            // daddr is the IPv4 destination in network byte order - read the 4 bytes straight into IPAddress
            // (do not BitConverter it, which would byte-swap on a little-endian host).
            var remoteIp = new IPAddress(new ReadOnlySpan<byte>(data, RemoteAddrOffset, 4).ToArray());

            // Never tunnel the WG underlay's own datagrams to the server endpoint: that would route the
            // transport into the tunnel it carries (a loop). Matters in all-UDP mode, where the engine's
            // plain-UDP send to the real server would otherwise be captured and pinned to the tunnel.
            if (_excludeEndpoint is not null && remoteIp.Equals(_excludeEndpoint))
            {
                MarkSeen(daddr);
                return;
            }

            if (!AppRouteWatcher.IsTunnelableRemote(remoteIp))
            {
                MarkSeen(daddr);
                return;
            }

            // Remember the destination only once the tracker actually routed it; a failure (adapter not up
            // yet, route add error) leaves it unseen so a later datagram retries it.
            if (_tracker.UpdateAppIps([remoteIp.ToString()]))
            {
                MarkSeen(daddr);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "UdpFlowTracker: parse error");
        }
    }

    // Records a destination as handled. Bounded so a long session cannot grow it without limit; on overflow
    // the whole set is dropped (each live destination is simply re-evaluated once on its next datagram).
    private void MarkSeen(uint daddr)
    {
        if (_seen.Count >= 65536)
        {
            _seen.Clear();
        }

        _seen.Add(daddr);
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
}
