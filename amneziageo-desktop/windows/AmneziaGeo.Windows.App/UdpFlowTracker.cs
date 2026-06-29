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
    // Microsoft-Windows-Kernel-Network – kernel-mode per-datagram events.
    private static readonly Guid KernelNetworkProvider = new("7DD42A49-5329-4832-8DFD-43D979153A88");
    // KERNEL_NETWORK_KEYWORD_IPV4: restricts events to IPv4 only.
    private const ulong IPv4Keyword = 0x10UL;
    // EventId 10 = UDPv4 datagram sent; present on Win10 1803+ from this provider.
    private const int UdpV4SendId = 10;
    // UDPv4 Send payload: LocalAddr(4) LocalPort(2) RemoteAddr(4) RemotePort(2) Size(4).
    // Remote address sits at byte offset 6.
    private const int RemoteAddrOffset = 6;
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

                var ok = _session.EnableProvider(KernelNetworkProvider, TraceEventLevel.Informational, IPv4Keyword);
                if (!ok)
                {
                    _logger.LogWarning("UdpFlowTracker: kernel-network ETW provider unavailable; UDP app routing disabled");
                    return;
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

        var pid = (uint)evt.ProcessID;
        if (!_watcher.MatchesPid(pid))
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

            var remoteIp = new IPAddress(new ReadOnlySpan<byte>(data, RemoteAddrOffset, 4).ToArray());

            if (!AppRouteWatcher.IsTunnelableRemote(remoteIp))
            {
                return;
            }

            _tracker.UpdateAppIps([remoteIp.ToString()]);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "UdpFlowTracker: parse error pid={Pid}", pid);
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
