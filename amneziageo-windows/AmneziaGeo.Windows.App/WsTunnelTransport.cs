using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;

namespace AmneziaGeo.Windows.App;

/// <summary>
/// Carries a tunnel's AmneziaWG UDP over a wstunnel WebSocket (TCP/TLS) so the tunnel works on networks that block UDP.
/// </summary>
internal sealed class WsTunnelTransport : IAsyncDisposable
{
    private const int AfInet = 2;
    private const int TcpTableOwnerPidAll = 5;
    private const int TcpStateEstablished = 5;

    private readonly string _serverHost;
    private readonly int _wsPort;
    private readonly int _targetPort;   // server-side AmneziaWG UDP port (original Endpoint port)
    private readonly string _pathPrefix; // path token for server-side --restrict-http-upgrade-path-prefix
    private readonly string _credentials; // optional basic-auth "user[:pass]"
    private readonly Action<string>? _onRejected;
    private readonly ILogger _logger;
    private readonly CancellationTokenSource _cts = new();
    private Process? _process;
    private Task? _supervisor;
    private int _rejectionReported;
    private int _redials;

    private WsTunnelTransport(string serverHost, int wsPort, int targetPort, string pathPrefix, string credentials, int localPort, Action<string>? onRejected, ILogger logger)
    {
        _serverHost = serverHost;
        _wsPort = wsPort;
        _targetPort = targetPort;
        _pathPrefix = pathPrefix;
        _credentials = credentials;
        LocalPort = localPort;
        _onRejected = onRejected;
        _logger = logger;
    }

    /// <summary>
    /// Loopback UDP port the WG engine dials instead of the blocked public endpoint.
    /// </summary>
    public int LocalPort { get; }

    /// <summary>
    /// How many times the carrier has been re-dialled during this session.
    /// </summary>
    public int Redials => Volatile.Read(ref _redials);

    /// <summary>
    /// Drops the carrier process; the supervisor dials a fresh websocket on the same local port a second later.
    /// The WG engine keeps its session: it goes on addressing the same loopback port throughout.
    /// </summary>
    public void Redial(string reason)
    {
        var process = _process;
        if (process is null)
        {
            return;
        }

        Interlocked.Increment(ref _redials);
        _logger.LogWarning("the websocket carrier is being re-dialled ({Reason}); the tunnel keeps its session and traffic resumes once the new carrier is up", reason);

        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            _logger.LogWarning(ex, "the websocket carrier would not stop for a re-dial, so the stalled one stays in place");
        }
    }

    /// <summary>
    /// TCP connections the carrier process holds, and how many of them are established. A carrier with no
    /// established connection carries nothing, whatever its process is doing.
    /// </summary>
    public (int Total, int Established) Sessions()
    {
        var process = _process;
        if (process is null)
        {
            return (0, 0);
        }

        var pid = TryGetPid(process);
        return pid == 0 ? (0, 0) : CountSessions(pid);
    }

    private static uint TryGetPid(Process process)
    {
        try
        {
            return process.HasExited ? 0 : (uint)process.Id;
        }
        catch (InvalidOperationException)
        {
            return 0;
        }
    }

    private static (int Total, int Established) CountSessions(uint pid)
    {
        var size = 0;
        GetExtendedTcpTable(IntPtr.Zero, ref size, false, AfInet, TcpTableOwnerPidAll, 0);
        if (size <= 0)
        {
            return (0, 0);
        }

        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            if (GetExtendedTcpTable(buffer, ref size, false, AfInet, TcpTableOwnerPidAll, 0) != 0)
            {
                return (0, 0);
            }

            var total = 0;
            var established = 0;
            var count = Marshal.ReadInt32(buffer);
            var basePtr = buffer + 4;
            for (var i = 0; i < count; i++)
            {
                // MIB_TCPROW_OWNER_PID: state at offset 0, owning pid at 20, each row 24 bytes.
                var row = basePtr + (i * 24);
                if ((uint)Marshal.ReadInt32(row, 20) != pid)
                {
                    continue;
                }

                total++;
                if (Marshal.ReadInt32(row) == TcpStateEstablished)
                {
                    established++;
                }
            }

            return (total, established);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    /// <summary>
    /// Starts a wstunnel client and waits until its local UDP listener is bound; null on missing binary or timeout.
    /// The callback fires once when the carrier reports a permanent rejection (TLS certificate).
    /// </summary>
    public static async Task<WsTunnelTransport?> StartAsync(string serverHost, int wsPort, int targetPort, string pathPrefix, string credentials, Action<string>? onRejected, ILogger logger, CancellationToken ct)
    {
        var exe = TunnelPaths.WsTunnelExe();
        if (!File.Exists(exe))
        {
            logger.LogError("this configuration asks to be carried inside a websocket, but the program that does it is missing ({Exe}); the connection cannot start — reinstall the app", exe);
            return null;
        }

        var transport = new WsTunnelTransport(serverHost, wsPort, targetPort, pathPrefix, credentials, FreeUdpPort(), onRejected, logger);
        transport.Spawn();
        transport._supervisor = Task.Run(() => transport.SuperviseAsync(transport._cts.Token));

        if (await transport.WaitUntilListeningAsync(TimeSpan.FromSeconds(8), ct).ConfigureAwait(false))
        {
            return transport;
        }

        logger.LogError("the websocket carrier never started listening on port {Port}, so the tunnel has nothing to dial; the connect is aborted", transport.LocalPort);
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
            _logger.LogError(ex, "the websocket carrier could not be launched; this connection cannot be disguised as web traffic and will not come up");
            _process = null;
            return;
        }

        if (process is null)
        {
            _logger.LogError("the websocket carrier did not start and reported no reason; this connection will not come up");
            return;
        }

        process.OutputDataReceived += (_, e) => Trace(e.Data);
        process.ErrorDataReceived += (_, e) => Trace(e.Data);
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        _process = process;
        _logger.LogInformation(
            "the websocket carrier is running (process {Pid}): it takes the tunnel from local port {Local}, wraps it in an encrypted web connection to {Host}:{Ws}, and the server hands it to port {Target}",
            process.Id, LocalPort, _serverHost, _wsPort, _targetPort);
    }

    // wstunnel carries its own level in every line; keep that level instead of burying the whole stream at Debug,
    // where the cause of a refused carrier never reaches the journal.
    private void Trace(string? line)
    {
        if (line is null)
        {
            return;
        }

        if (IsRejection(line))
        {
            _logger.LogError("the websocket carrier says: {Line}", line);
            ReportRejection(line);
            return;
        }

        if (line.Contains("ERROR", StringComparison.Ordinal) || line.Contains("WARN", StringComparison.Ordinal))
        {
            _logger.LogWarning("the websocket carrier says: {Line}", line);
            return;
        }

        _logger.LogInformation("the websocket carrier says: {Line}", line);
    }

    // A refused certificate is permanent: an expired or untrusted server cert never clears by re-dialing.
    private static bool IsRejection(string line)
    {
        if (line.Contains("invalid peer certificate", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!line.Contains("certificate", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return line.Contains("expired", StringComparison.OrdinalIgnoreCase)
            || line.Contains("unknown issuer", StringComparison.OrdinalIgnoreCase)
            || line.Contains("unknownissuer", StringComparison.OrdinalIgnoreCase)
            || line.Contains("not valid", StringComparison.OrdinalIgnoreCase)
            || line.Contains("notvalidfor", StringComparison.OrdinalIgnoreCase)
            || line.Contains("verify failed", StringComparison.OrdinalIgnoreCase)
            || line.Contains("bad certificate", StringComparison.OrdinalIgnoreCase);
    }

    private void ReportRejection(string line)
    {
        if (Interlocked.Exchange(ref _rejectionReported, 1) != 0)
        {
            return;
        }

        try
        {
            _onRejected?.Invoke(line);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "the carrier's refusal could not be recorded, so this attempt may be reported as an unreachable server instead of naming the real cause");
        }
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

            _logger.LogWarning("the websocket carrier stopped (exit code {Code}); traffic is interrupted until it is started again on port {Port}, in a second", process.ExitCode, LocalPort);
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
        _logger.LogInformation("the websocket carrier is stopped");
    }

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedTcpTable(IntPtr pTcpTable, ref int pdwSize, [MarshalAs(UnmanagedType.Bool)] bool bOrder, int ulAf, int tableClass, int reserved);
}
