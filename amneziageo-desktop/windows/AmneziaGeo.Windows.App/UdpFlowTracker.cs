using System.Net;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Session;
using Microsoft.Extensions.Logging;

namespace AmneziaGeo.Windows.App;

/// <summary>
/// UDP complement to AppRouteWatcher: tunnels UDP destinations of matched apps via the kernel-network ETW provider.
/// </summary>
internal sealed class UdpFlowTracker : IDisposable
{
    // Microsoft-Windows-Kernel-Network - per-datagram network events.
    private static readonly Guid KernelNetworkProvider = new("7DD42A49-5329-4832-8DFD-43D979153A88");
    // KERNEL_NETWORK_KEYWORD_IPV4 (0x10): IPv4 TCP and UDP datagram events.
    private const ulong IPv4Keyword = 0x10UL;
    // EventId 42 = UDPv4 send; 10 is TCPv4 and already covered by the TCP watcher.
    private const int UdpV4SendId = 42;
    // SendIPV4Udp payload (little-endian): PID(4) size(4) daddr(4) saddr(4) dport(2) sport(2).
    // Payload PID (offset 0) is more reliable than the ETW header PID; daddr at offset 8.
    private const int PidOffset = 0;
    private const int RemoteAddrOffset = 8;
    private const int MinPayloadBytes = RemoteAddrOffset + 4;
    // Own process - its underlay and DNS-proxy UDP must never be tunneled (would loop).
    private static readonly uint OwnProcessId = (uint)Environment.ProcessId;

    private readonly AppRouteWatcher? _watcher;
    private readonly DomainTracker _tracker;
    private readonly bool _allUdp;
    private readonly IPAddress? _excludeEndpoint;
    private readonly ILogger _logger;
    private TraceEventSession? _session;
    // Seen destinations (raw daddr); ETW handler is single-threaded, no lock needed.
    private readonly HashSet<uint> _seen = [];

    /// <summary>
    /// ctor
    /// </summary>
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

                // EnableProvider false is normal first-enable; do not gate on the result.
                if (_session.EnableProvider(KernelNetworkProvider, TraceEventLevel.Informational, IPv4Keyword))
                {
                    _logger.LogDebug("UdpFlowTracker: restarted a pre-existing ETW session {Name}", sessionName);
                }

                _session.Source.AllEvents += evt => Handle(evt, ct);
                _logger.LogInformation("UdpFlowTracker: ETW session {Name} started", sessionName);

                // Source.Process() blocks until Stop().
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
            else if (_watcher is null || !_watcher.MatchesPid(pid))
            {
                return;
            }

            // Dedupe by raw daddr before any allocation.
            var daddr = BitConverter.ToUInt32(data, RemoteAddrOffset);
            if (_seen.Contains(daddr))
            {
                return;
            }

            // daddr is network byte order; read bytes directly into IPAddress.
            var remoteIp = new IPAddress(new ReadOnlySpan<byte>(data, RemoteAddrOffset, 4).ToArray());

            // Skip the WG underlay endpoint to avoid a transport loop.
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

            // Mark seen only after a successful route; failures retry on the next datagram.
            if (_tracker.UpdateAppIps([remoteIp.ToString()]))
            {
                _logger.LogTrace("udp request -> {Remote} (pid {Pid})", remoteIp, pid);
                if (RouteLog.Enabled)
                {
                    RouteLog.Note($"udp request -> {remoteIp} (pid {pid})");
                }

                MarkSeen(daddr);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "UdpFlowTracker: parse error");
        }
    }

    // Marks destination handled; clears on overflow to bound the set.
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
