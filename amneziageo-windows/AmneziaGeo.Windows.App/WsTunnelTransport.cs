using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;

namespace AmneziaGeo.Windows.App;

/// <summary>
/// Carries a tunnel's AmneziaWG UDP over a wstunnel WebSocket (TCP/TLS) so the tunnel works on networks that block UDP.
/// </summary>
internal sealed class WsTunnelTransport : IAsyncDisposable
{
    private readonly string _serverHost;
    private readonly int _wsPort;
    private readonly int _targetPort;   // server-side AmneziaWG UDP port (original Endpoint port)
    private readonly string _pathPrefix; // path token for server-side --restrict-http-upgrade-path-prefix
    private readonly string _credentials; // optional basic-auth "user[:pass]"
    private readonly ILogger _logger;
    private readonly CancellationTokenSource _cts = new();
    private Process? _process;
    private Task? _supervisor;

    private WsTunnelTransport(string serverHost, int wsPort, int targetPort, string pathPrefix, string credentials, int localPort, ILogger logger)
    {
        _serverHost = serverHost;
        _wsPort = wsPort;
        _targetPort = targetPort;
        _pathPrefix = pathPrefix;
        _credentials = credentials;
        LocalPort = localPort;
        _logger = logger;
    }

    /// <summary>
    /// Loopback UDP port the WG engine dials instead of the blocked public endpoint.
    /// </summary>
    public int LocalPort { get; }

    /// <summary>
    /// Starts a wstunnel client and waits until its local UDP listener is bound; null on missing binary or timeout.
    /// </summary>
    public static async Task<WsTunnelTransport?> StartAsync(string serverHost, int wsPort, int targetPort, string pathPrefix, string credentials, ILogger logger, CancellationToken ct)
    {
        var exe = TunnelPaths.WsTunnelExe();
        if (!File.Exists(exe))
        {
            logger.LogError("websocket transport requested but {Exe} is missing", exe);
            return null;
        }

        var transport = new WsTunnelTransport(serverHost, wsPort, targetPort, pathPrefix, credentials, FreeUdpPort(), logger);
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
        // -L udp://<localPort>:127.0.0.1:<targetPort> forwards to the AmneziaWG container on the server;
        // timeout_sec=0 keeps the UDP association alive. --tls-verify-certificate is required (wstunnel
        // disables verification by default). Optional -P path token and basic-auth credentials.
        var auth = string.Empty;
        if (_pathPrefix.Length > 0)
        {
            auth += $" -P \"{_pathPrefix}\"";
        }

        if (_credentials.Length > 0)
        {
            auth += $" --http-upgrade-credentials \"{_credentials}\"";
        }

        var args = $"client --tls-verify-certificate{auth} -L \"udp://{LocalPort}:127.0.0.1:{_targetPort}?timeout_sec=0\" \"wss://{_serverHost}:{_wsPort}\"";
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

            // wstunnel binds its local UDP socket on start; the WS connection opens lazily on the first datagram.
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

    private static int FreeUdpPort()
    {
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)socket.LocalEndPoint!).Port;
    }

    /// <inheritdoc/>
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
