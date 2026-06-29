using System.Net;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Session;
using Microsoft.Extensions.Logging;

namespace AmneziaGeo.Windows.App;

/// <summary>
/// UDP complement to <see cref="AppRouteWatcher"/>: subscribes to the kernel-network ETW provider and
/// routes remote IPs of UDP datagrams sent by matched apps through the tunnel. The TCP-based watcher
/// cannot see UDP flows, so real-time media apps (e.g. Discord voice) whose servers deliver IPs through
/// an application-layer signaling channel (not DNS) need this path to land UDP traffic in the tunnel.
/// </summary>
internal sealed class UdpFlowTracker : IDisposable
{
    // Microsoft-Windows-Kernel-Network - per-datagram network events.
    private static readonly Guid KernelNetworkProvider = new("7DD42A49-5329-4832-8DFD-43D979153A88");
    // KERNEL_NETWORK_KEYWORD_IPV4 (0x10): IPv4 TCP and UDP datagram events from this provider.
    private const ulong IPv4Keyword = 0x10UL;
    // EventId 42 = KNetEvt_SendIPV4Udp (UDPv4 datagram sent). NB: id 10 is TCPv4 send, which the TCP watcher
    // already covers - real-time media (Discord voice) is UDP, so the UDP send event (42) is the one we want.
    private const int UdpV4SendId = 42;
    // KNetEvt_SendIPV4Udp payload (little-endian): PID(4) size(4) daddr(4) saddr(4) dport(2) sport(2) ...
    // The owning process is an explicit payload field at offset 0 - more reliable than the ETW header PID,
    // which this provider can log under a system worker thread. daddr (the remote/destination IPv4) is at 8.
    private const int PidOffset = 0;
    private const int RemoteAddrOffset = 8;
    private const int MinPayloadBytes = RemoteAddrOffset + 4;

    private readonly AppRouteWatcher _watcher;
    private readonly DomainTracker _tracker;
    private readonly ILogger _logger;
    private TraceEventSession? _session;

    public UdpFlowTracker(AppRouteWatcher watcher, DomainTracker tracker, ILogger logger)
    {
        _watcher = watcher;
        _tracker = tracker;
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

            // Owning process is the payload PID field (offset 0), not the ETW header PID.
            var pid = BitConverter.ToUInt32(data, PidOffset);
            if (!_watcher.MatchesPid(pid))
            {
                return;
            }

            // daddr is the IPv4 destination in network byte order - read the 4 bytes straight into IPAddress
            // (do not BitConverter it, which would byte-swap on a little-endian host).
            var remoteIp = new IPAddress(new ReadOnlySpan<byte>(data, RemoteAddrOffset, 4).ToArray());
            if (!AppRouteWatcher.IsTunnelableRemote(remoteIp))
            {
                return;
            }

            _tracker.UpdateAppIps([remoteIp.ToString()]);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "UdpFlowTracker: parse error");
        }
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
