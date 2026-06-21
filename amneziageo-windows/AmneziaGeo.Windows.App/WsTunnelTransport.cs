using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;

namespace AmneziaGeo.Windows.App;

/// <summary>
/// Carries a tunnel's AmneziaWG UDP over a wstunnel WebSocket (TCP/TLS) so the tunnel works on networks
/// that block UDP. Spawns the bundled wstunnel client as a child process: it listens on a loopback UDP
/// port and forwards datagrams to the server's WebSocket on the configured TLS port (usually 443), which
/// the server unwraps to the AmneziaWG container's loopback UDP. The WG engine then dials the local port
/// instead of the blocked public endpoint. Supervises the child (restarts it on unexpected exit) until
/// stopped with the session.
/// </summary>
internal sealed class WsTunnelTransport : IAsyncDisposable
{
    private readonly string _serverHost;
    private readonly int _wsPort;
    private readonly int _targetPort;   // server-side AmneziaWG UDP port (the original Endpoint's port)
    private readonly ILogger _logger;
    private readonly CancellationTokenSource _cts = new();
    private Process? _process;
    private Task? _supervisor;

    private WsTunnelTransport(string serverHost, int wsPort, int targetPort, int localPort, ILogger logger)
    {
        _serverHost = serverHost;
        _wsPort = wsPort;
        _targetPort = targetPort;
        LocalPort = localPort;
        _logger = logger;
    }

    /// <summary>
    /// The loopback UDP port the WG engine should dial — written into the config's Endpoint in place of
    /// the blocked public endpoint.
    /// </summary>
    public int LocalPort { get; }

    /// <summary>
    /// Starts a wstunnel client for a config and waits until its local UDP listener is bound. Returns null
    /// when the bundled binary is missing or the listener never came up, so the caller can fail the connect
    /// cleanly rather than bring up a tunnel whose endpoint goes nowhere.
    /// </summary>
    public static async Task<WsTunnelTransport?> StartAsync(string serverHost, int wsPort, int targetPort, ILogger logger, CancellationToken ct)
    {
        var exe = TunnelPaths.WsTunnelExe();
        if (!File.Exists(exe))
        {
            logger.LogError("websocket transport requested but {Exe} is missing", exe);
            return null;
        }

        var transport = new WsTunnelTransport(serverHost, wsPort, targetPort, FreeUdpPort(), logger);
        transport.Spawn();
        transport._supervisor = Task.Run(() => transport.SuperviseAsync(transport._cts.Token));

        if (await transport.WaitUntilListeningAsync(TimeSpan.FromSeconds(8), ct).ConfigureAwait(false))
        {
            return transport;
        }

        logger.LogError("wstunnel local UDP :{Port} did not come up", transport.LocalPort);
        await transport.DisposeAsync().ConfigureAwait(false);
        return null;
    }

    private void Spawn()
    {
        // -L udp://<localPort>:<dstHost>:<dstPort> : listen on loopback UDP <localPort> (default bind is
        // 127.0.0.1) and forward to <dstHost>:<dstPort> resolved ON THE SERVER, i.e. the AmneziaWG
        // container at 127.0.0.1:<targetPort>. timeout_sec=0 keeps the UDP association alive for the
        // long-lived WireGuard session. The wss:// target carries the real server hostname so its TLS
        // certificate validates. --tls-verify-certificate is required because wstunnel DISABLES
        // verification by default (it would connect to any self-signed cert); the server reuses the
        // existing real x-ui/no-ip certificate for this hostname, so verifying it defeats MITM.
        var args = $"client --tls-verify-certificate -L \"udp://{LocalPort}:127.0.0.1:{_targetPort}?timeout_sec=0\" \"wss://{_serverHost}:{_wsPort}\"";
        var info = new ProcessStartInfo(TunnelPaths.WsTunnelExe(), args)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        Process? process;
        try
        {
            process = Process.Start(info);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "failed to start wstunnel");
            _process = null;
            return;
        }

        if (process is null)
        {
            _logger.LogError("failed to start wstunnel");
            return;
        }

        process.OutputDataReceived += (_, e) => { if (e.Data is not null) { _logger.LogDebug("wstunnel: {Line}", e.Data); } };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) { _logger.LogDebug("wstunnel: {Line}", e.Data); } };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        _process = process;
        _logger.LogInformation(
            "wstunnel started (pid {Pid}): local udp :{Local} -> wss://{Host}:{Ws} -> 127.0.0.1:{Target}",
            process.Id, LocalPort, _serverHost, _wsPort, _targetPort);
    }

    /// <summary>
    /// Keeps a wstunnel client alive for the session: if the child exits unexpectedly (network drop,
    /// crash) it is restarted on the same local port so the WG engine's loopback endpoint stays valid.
    /// Ends when the session token is cancelled (teardown).
    /// </summary>
    private async Task SuperviseAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var process = _process;
            if (process is null)
            {
                try
                {
                    await Task.Delay(1000, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }

                Spawn();
                continue;
            }

            try
            {
                await process.WaitForExitAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            if (ct.IsCancellationRequested)
            {
                return;
            }

            _logger.LogWarning("wstunnel exited (code {Code}); restarting on :{Port}", process.ExitCode, LocalPort);
            process.Dispose();
            _process = null;

            try
            {
                await Task.Delay(1000, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            Spawn();
        }
    }

    private async Task<bool> WaitUntilListeningAsync(TimeSpan timeout, CancellationToken ct)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (ct.IsCancellationRequested)
            {
                return false;
            }

            // wstunnel binds its local UDP socket as soon as the client starts (the WS connection to the
            // server is established lazily on the first datagram), so a bound loopback listener on our
            // port means the engine can dial it.
            var listeners = IPGlobalProperties.GetIPGlobalProperties().GetActiveUdpListeners();
            foreach (var endpoint in listeners)
            {
                if (endpoint.Port == LocalPort && (IPAddress.IsLoopback(endpoint.Address) || endpoint.Address.Equals(IPAddress.Any)))
                {
                    return true;
                }
            }

            try
            {
                await Task.Delay(150, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return false;
            }
        }

        return false;
    }

    /// <summary>
    /// Reserves a free loopback UDP port by binding ephemeral and reading the assigned number; the socket
    /// is released immediately so wstunnel can take the port. A brief TOCTOU window, acceptable here.
    /// </summary>
    private static int FreeUdpPort()
    {
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)socket.LocalEndPoint!).Port;
    }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync().ConfigureAwait(false);

        if (_supervisor is not null)
        {
            try
            {
                await _supervisor.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        var process = _process;
        if (process is not null)
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
            }

            process.Dispose();
            _process = null;
        }

        _cts.Dispose();
        _logger.LogInformation("wstunnel transport stopped");
    }
}
